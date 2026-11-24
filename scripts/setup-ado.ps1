#Requires -Version 7
<#
.SYNOPSIS
  Sets up Azure DevOps for Review Guard. Safe to run again: each step gets the item first
  and creates it only when it is missing (service hooks are updated to the new URL/secret).

.DESCRIPTION
  What it does, in order:
    1. Checks the azure-devops CLI extension and the sign-in.
    2. Creates the sandbox repository with a README on main (if missing).
    3. Pushes the line count pipeline files into the sandbox repository.
    4. Creates the YAML pipeline "review-guard-linecount".
    5. Grants the build service "Contribute to pull requests" on the repository,
       so the pipeline can write PR properties.
    6. Branch policies on the target branch (-TargetBranch):
         - build validation: runs the line count pipeline on each PR update
         - status check: review-guard/review-depth (optional by default = report-only)
         - minimum reviewers: 1, reset votes on new push
    7. Service hooks to the Function:
         - git.pullrequest.updated (reviewer vote changes)
         - build.complete (line count pipeline finished)

  Sign-in: run this once yourself before the script. The script never reads or prints a PAT.
    az devops login --organization https://dev.azure.com/DigiCovai

.EXAMPLE
  ./scripts/setup-ado.ps1 -FunctionBaseUrl https://func-reviewguard.azurewebsites.net `
      -FunctionKey <key> -WebhookSecret <secret>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FunctionBaseUrl,
    [Parameter(Mandatory)] [string] $FunctionKey,
    [Parameter(Mandatory)] [string] $WebhookSecret,
    # Identity id allowed to post the status. Empty = any identity (weaker, see DESIGN.md section 8).
    [string] $StatusAuthorId = '',
    [string] $Organization = 'https://dev.azure.com/DigiCovai',
    [string] $Project = 'Internal',
    [string] $Repo = 'review-guard-sandbox',
    [string] $PipelineName = 'review-guard-linecount',
    # Branch the policies guard. The sandbox uses 'trunk' (see step 3).
    [string] $TargetBranch = 'main',
    # Off = report-only phase (status check is optional). On = merge is blocked.
    [switch] $PolicyBlocking
)

$ErrorActionPreference = 'Stop'
# Version 1 only: REST objects differ by type (policies, hooks), and missing properties must read as null.
Set-StrictMode -Version 1
$PSNativeCommandUseErrorActionPreference = $true

# Fixed ids from the Azure DevOps docs.
$PolicyTypeBuild = '0609b952-1397-4640-95ec-e00a01b2c241'
$PolicyTypeStatus = 'cbdc66da-9728-4af8-aada-9a5a32e4a226'
$PolicyTypeMinReviewers = 'fa4e907d-c16b-4a4c-9dfa-4906e5d171dd'
$GitSecurityNamespace = '2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87'
$PullRequestContributeBit = 16384
$EmptyObjectId = '0000000000000000000000000000000000000000'

$FunctionBaseUrl = $FunctionBaseUrl.TrimEnd('/')
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "review-guard-setup-$PID"
New-Item -ItemType Directory -Force $tempDir | Out-Null

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# Runs "az devops invoke". The body goes through a temp file, because invoke only takes --in-file.
function Invoke-Ado {
    param(
        [Parameter(Mandatory)] [string] $Area,
        [Parameter(Mandatory)] [string] $Resource,
        [string] $Method = 'GET',
        [hashtable] $Route = @{},
        [hashtable] $Query = @{},
        [object] $Body,
        [string] $ApiVersion = '7.1'
    )
    $azArgs = @('devops', 'invoke', '--org', $Organization, '--area', $Area, '--resource', $Resource,
        '--http-method', $Method, '--api-version', $ApiVersion, '--only-show-errors', '-o', 'json')
    if ($Route.Count) { $azArgs += '--route-parameters'; $azArgs += @($Route.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) }
    if ($Query.Count) { $azArgs += '--query-parameters'; $azArgs += @($Query.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) }
    if ($null -ne $Body) {
        $file = Join-Path $tempDir "body-$([guid]::NewGuid().ToString('N')).json"
        ConvertTo-Json -InputObject $Body -Depth 20 | Set-Content -Path $file -Encoding utf8NoBOM
        $azArgs += @('--in-file', $file)
    }
    $out = az @azArgs
    if ($out) { return ($out | Out-String | ConvertFrom-Json -Depth 50) }
}

# Direct REST, for areas "az devops invoke" does not know (hooks, security, location).
# Uses the same PAT as the CLI: the AZURE_DEVOPS_EXT_PAT environment variable.
function Invoke-AdoRest {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Method = 'GET',
        [object] $Body
    )
    if (-not $env:AZURE_DEVOPS_EXT_PAT) { throw 'Set AZURE_DEVOPS_EXT_PAT (the same PAT as az devops login) for this step.' }
    $auth = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$($env:AZURE_DEVOPS_EXT_PAT)")) }
    $sep = if ($Path.Contains('?')) { '&' } else { '?' }
    $params = @{ Method = $Method; Headers = $auth; Uri = "$Organization/_apis/$Path${sep}api-version=7.1" }
    if ($null -ne $Body) { $params.ContentType = 'application/json'; $params.Body = ConvertTo-Json -InputObject $Body -Depth 20 }
    Invoke-RestMethod @params
}

try {
    # ---------------------------------------------------------------- 1. CLI and sign-in
    Write-Step 'Check azure-devops CLI extension and sign-in'
    $ext = az extension list --query "[?name=='azure-devops'].version" -o tsv
    if (-not $ext) { az extension add --name azure-devops --only-show-errors }
    az devops configure --defaults organization=$Organization project=$Project | Out-Null

    $projectObj = az devops project show --project $Project -o json | ConvertFrom-Json
    $projectId = $projectObj.id
    Write-Host "Project $Project = $projectId"

    # ---------------------------------------------------------------- 2. Sandbox repository
    Write-Step "Repository $Repo"
    $repoObj = az repos list --project $Project -o json | ConvertFrom-Json | Where-Object name -eq $Repo
    if (-not $repoObj) {
        $repoObj = az repos create --name $Repo --project $Project -o json | ConvertFrom-Json
        Write-Host "Created repository $($repoObj.id)"
    }
    else {
        Write-Host "Exists: $($repoObj.id)"
    }
    $repoId = $repoObj.id

    # ---------------------------------------------------------------- 3. Pipeline files
    Write-Step "Push pipeline files to $TargetBranch"
    $files = [ordered]@{
        '/README.md'                            = "# review-guard-sandbox`n`nTest repository for Review Guard. PRs here are checked by the review-guard Function.`n"
        '/pipelines/review-guard-linecount.yml' = Get-Content -Raw (Join-Path $repoRoot 'pipelines/review-guard-linecount.yml')
        '/pipelines/count-changed-lines.ps1'    = Get-Content -Raw (Join-Path $repoRoot 'pipelines/count-changed-lines.ps1')
    }

    function Get-BranchHead([string] $name) {
        $ref = Invoke-Ado -Area git -Resource refs -Route @{ project = $projectId; repositoryId = $repoId } -Query @{ filter = "heads/$name" }
        $hit = @($ref.value | Where-Object name -eq "refs/heads/$name")
        if ($hit.Count) { $hit[0].objectId } else { $null }
    }
    $mainHead = Get-BranchHead 'main'
    # The guarded branch. In the sandbox it is not main, because project-wide policies on main
    # (yours, not ours) block direct pushes there. Created from main when missing.
    if ($TargetBranch -ne 'main' -and $mainHead -and -not (Get-BranchHead $TargetBranch)) {
        az repos ref create --name "heads/$TargetBranch" --object-id $mainHead --repository $repoId --project $Project --only-show-errors -o none
        Write-Host "Created branch $TargetBranch from main"
    }
    $targetHead = Get-BranchHead $TargetBranch

    $changes = @()
    foreach ($path in $files.Keys) {
        $newText = $files[$path] -replace "`r`n", "`n"
        $existing = $null
        if ($targetHead) {
            try {
                $existing = az devops invoke --org $Organization --area git --resource items `
                    --route-parameters project=$projectId repositoryId=$repoId `
                    --query-parameters "path=$path" 'includeContent=true' "versionDescriptor.version=$TargetBranch" `
                    --api-version 7.1 --only-show-errors -o json 2>$null | ConvertFrom-Json
            }
            catch { $existing = $null }
        }
        if ($existing -and (($existing.content -replace "`r`n", "`n") -eq $newText)) {
            Write-Host "Up to date: $path"
            continue
        }
        $changes += @{
            changeType = if ($existing) { 'edit' } else { 'add' }
            item       = @{ path = $path }
            newContent = @{ content = $newText; contentType = 'rawtext' }
        }
        Write-Host "Will $(if ($existing) { 'update' } else { 'add' }): $path"
    }

    if ($changes.Count) {
        # On a re-run, our own branch policies block a direct push (TF402455).
        # Turn off the policies on this repository for the push. Step 6 turns them on again.
        $guarded = @((Invoke-Ado -Area policy -Resource configurations -Route @{ project = $projectId }).value |
            Where-Object { $_.isEnabled -and ($_.settings.scope | Where-Object { $_.repositoryId -eq $repoId }) })
        foreach ($policy in $guarded) {
            $off = @{ isEnabled = $false; isBlocking = $policy.isBlocking; type = @{ id = $policy.type.id }; settings = $policy.settings }
            Invoke-Ado -Area policy -Resource configurations -Method PUT -Route @{ project = $projectId; configurationId = $policy.id } -Body $off | Out-Null
            Write-Host "Paused policy $($policy.id) for the push"
        }
        $push = @{
            refUpdates = @(@{ name = "refs/heads/$TargetBranch"; oldObjectId = ($targetHead ?? $EmptyObjectId) })
            commits    = @(@{ comment = 'Add Review Guard line count pipeline'; changes = $changes })
        }
        Invoke-Ado -Area git -Resource pushes -Method POST -Route @{ project = $projectId; repositoryId = $repoId } -Body $push | Out-Null
        Write-Host 'Pushed.'
    }

    # Make main the default branch (the first push on an empty repo does this, this is a safety net).
    az repos update --repository $repoId --default-branch main --project $Project --only-show-errors -o none

    # ---------------------------------------------------------------- 4. Pipeline
    Write-Step "Pipeline $PipelineName"
    $pipeline = az pipelines list --project $Project --name $PipelineName -o json | ConvertFrom-Json | Select-Object -First 1
    if (-not $pipeline) {
        $pipeline = az pipelines create --name $PipelineName --project $Project `
            --repository $Repo --repository-type tfsgit --branch main `
            --yml-path pipelines/review-guard-linecount.yml --skip-first-run true -o json | ConvertFrom-Json
        Write-Host "Created pipeline $($pipeline.id)"
    }
    else {
        Write-Host "Exists: $($pipeline.id)"
    }
    $pipelineId = $pipeline.id

    # ---------------------------------------------------------------- 5. Build service permission
    Write-Step 'Grant "Contribute to pull requests" to the build service'
    # Two build identities exist. Which one a pipeline uses depends on the project setting
    # "Limit job authorization scope to current project". Grant both.
    # "az devops invoke" does not know the Location and Security areas, so these two calls use
    # REST directly with the same PAT the CLI uses (AZURE_DEVOPS_EXT_PAT). No PAT there: skip.
    if (-not $env:AZURE_DEVOPS_EXT_PAT) {
        Write-Warning 'AZURE_DEVOPS_EXT_PAT is not set. Skipping. Grant "Contribute to pull requests" to the build service by hand.'
    }
    else {
        $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$($env:AZURE_DEVOPS_EXT_PAT)")) }
        $connection = Invoke-RestMethod -Headers $headers -Uri "$Organization/_apis/connectionData?api-version=7.1-preview"
        $collectionId = $connection.instanceId
        # Build identities are Graph "svc" users. principalName = project id (project scoped) or
        # collection id (collection scoped). The Graph descriptor "svc.<base64>" decodes to the
        # identity part of the security descriptor.
        $orgName = ([uri]$Organization).AbsolutePath.Trim('/')
        $svcUsers = (Invoke-RestMethod -Headers $headers -Uri "https://vssps.dev.azure.com/$orgName/_apis/graph/users?subjectTypes=svc&api-version=7.1-preview.1").value
        $buildDescriptors = @($svcUsers | Where-Object { $_.principalName -in @($projectId, $collectionId) } | ForEach-Object {
                $b64 = $_.descriptor.Substring(4)
                $b64 += '=' * ((4 - $b64.Length % 4) % 4)
                'Microsoft.TeamFoundation.ServiceIdentity;' + [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
            })
        $token = "repoV2/$projectId/$repoId"
        # One call per identity: an identity that was never used does not exist yet (TF14045).
        foreach ($descriptor in $buildDescriptors) {
            $ace = @{
                token                = $token
                merge                = $true
                accessControlEntries = @(@{ descriptor = $descriptor; allow = $PullRequestContributeBit; deny = 0 })
            }
            try {
                Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
                    -Uri "$Organization/_apis/accesscontrolentries/$GitSecurityNamespace`?api-version=7.1" `
                    -Body (ConvertTo-Json $ace -Depth 10) | Out-Null
                Write-Host "Allowed bit $PullRequestContributeBit on $token for $($descriptor.Split(':')[-1])"
            }
            catch {
                if ("$_" -notmatch 'TF14045') { throw }
                Write-Host "Skipped (identity does not exist): $($descriptor.Split(':')[-1])"
            }
        }
    }

    # ---------------------------------------------------------------- 6. Branch policies
    Write-Step "Branch policies on $TargetBranch"
    $scope = @(@{ repositoryId = $repoId; refName = "refs/heads/$TargetBranch"; matchKind = 'exact' })
    $existingPolicies = (Invoke-Ado -Area policy -Resource configurations -Route @{ project = $projectId }).value |
        Where-Object { $_.settings.scope | Where-Object { $_.repositoryId -eq $repoId -and $_.refName -eq "refs/heads/$TargetBranch" } }

    function Set-Policy([string] $typeId, [bool] $blocking, [hashtable] $settings, [scriptblock] $match) {
        $body = @{ isEnabled = $true; isBlocking = $blocking; type = @{ id = $typeId }; settings = $settings }
        $found = $existingPolicies | Where-Object { $_.type.id -eq $typeId } | Where-Object $match | Select-Object -First 1
        if ($found) {
            Invoke-Ado -Area policy -Resource configurations -Method PUT -Route @{ project = $projectId; configurationId = $found.id } -Body $body | Out-Null
            Write-Host "Updated policy $($found.id) ($typeId)"
            return $found.id
        }
        $created = Invoke-Ado -Area policy -Resource configurations -Method POST -Route @{ project = $projectId } -Body $body
        Write-Host "Created policy $($created.id) ($typeId)"
        return $created.id
    }

    $buildPolicyId = Set-Policy $PolicyTypeBuild $true @{
        buildDefinitionId       = $pipelineId
        displayName             = 'Review Guard line count'
        queueOnSourceUpdateOnly = $false
        manualQueueOnly         = $false
        validDuration           = 0
        scope                   = $scope
    } { $_.settings.buildDefinitionId -eq $pipelineId }

    $statusSettings = @{
        statusGenre              = 'review-guard'
        statusName               = 'review-depth'
        invalidateOnSourceUpdate = $true
        scope                    = $scope
    }
    if ($StatusAuthorId) { $statusSettings.authorId = $StatusAuthorId }
    $statusPolicyId = Set-Policy $PolicyTypeStatus $PolicyBlocking.IsPresent $statusSettings {
        $_.settings.statusGenre -eq 'review-guard' -and $_.settings.statusName -eq 'review-depth'
    }

    $reviewersPolicyId = Set-Policy $PolicyTypeMinReviewers $true @{
        minimumApproverCount = 1
        creatorVoteCounts    = $true    # The e2e test votes as the PR creator.
        allowDownvotes       = $false
        resetOnSourcePush    = $true    # Old approval must not cover new code (DESIGN.md section 9).
        scope                = $scope
    } { $true }

    # ---------------------------------------------------------------- 7. Service hooks
    Write-Step 'Service hooks'
    $existingHooks = (Invoke-AdoRest -Path hooks/subscriptions).value

    function Set-Hook([string] $eventType, [hashtable] $publisherInputs, [string] $path, [string] $resourceVersion = '1.0') {
        $body = @{
            publisherId      = 'tfs'
            eventType        = $eventType
            resourceVersion  = $resourceVersion
            consumerId       = 'webHooks'
            consumerActionId = 'httpRequest'
            publisherInputs  = $publisherInputs
            consumerInputs   = @{
                url                    = "$FunctionBaseUrl$path`?code=$FunctionKey"
                httpHeaders            = "X-ReviewGuard-Secret:$WebhookSecret"
                resourceDetailsToSend  = 'all'
                messagesToSend         = 'none'
                detailedMessagesToSend = 'none'
            }
        }
        # Match on event + project + the Function path. The key in the URL may have changed.
        $found = $existingHooks | Where-Object {
            $_.eventType -eq $eventType -and $_.publisherInputs.projectId -eq $projectId -and
            $_.consumerInputs.url -and ($_.consumerInputs.url -split '\?')[0] -eq "$FunctionBaseUrl$path"
        } | Select-Object -First 1
        if ($found) {
            Invoke-AdoRest -Path "hooks/subscriptions/$($found.id)" -Method PUT -Body $body | Out-Null
            Write-Host "Updated hook $($found.id) ($eventType)"
            return $found.id
        }
        $created = Invoke-AdoRest -Path hooks/subscriptions -Method POST -Body $body
        Write-Host "Created hook $($created.id) ($eventType)"
        return $created.id
    }

    $prHookId = Set-Hook 'git.pullrequest.updated' @{
        projectId        = $projectId
        repository       = $repoId
        branch           = ''
        notificationType = 'ReviewerVoteNotification'   # Only vote changes, not pushes or title edits.
    } '/api/hooks/pull-request-updated'

    $buildHookId = Set-Hook 'build.complete' @{
        projectId      = $projectId
        definitionName = $PipelineName
        buildStatus    = ''                              # Any result. The Function checks it.
    } '/api/hooks/build-completed' '2.0'   # 2.0 sends the Build object with triggerInfo and repository.

    # ---------------------------------------------------------------- Summary
    Write-Step 'Summary'
    [pscustomobject]@{
        ProjectId           = $projectId
        RepositoryId        = $repoId
        PipelineId          = $pipelineId
        BuildPolicyId       = $buildPolicyId
        StatusPolicyId      = $statusPolicyId
        StatusPolicyMode    = if ($PolicyBlocking) { 'Required (blocking)' } else { 'Optional (report-only)' }
        ReviewersPolicyId   = $reviewersPolicyId
        PrUpdatedHookId     = $prHookId
        BuildCompleteHookId = $buildHookId
    } | Format-List
}
finally {
    # Bodies in temp files contain the webhook secret and Function key. Remove them.
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
