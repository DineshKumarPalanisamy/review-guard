#requires -Version 7
<#
.SYNOPSIS
    End-to-end test for PR Review Guard against the sandbox repository in Azure DevOps.

.DESCRIPTION
    Each scenario creates a branch from main, pushes one file, opens a PR, votes,
    and checks the "review-guard/review-depth" status that the Function posts.
    The PR is abandoned and the branch is deleted at the end of each scenario.

    Needs the Azure CLI with the azure-devops extension, and "az devops login"
    done by the user. The script never reads or handles a PAT.

.EXAMPLE
    ./Run-E2E.ps1
    ./Run-E2E.ps1 -Scenarios large-fast,small
#>
[CmdletBinding()]
param(
    [ValidateSet('large-fast', 'small', 'large-with-comment', 'approve-before-pipeline', 'critical-small')]
    [string[]] $Scenarios = @('large-fast', 'small', 'large-with-comment', 'approve-before-pipeline', 'critical-small'),

    [string] $Organization = 'https://dev.azure.com/DigiCovai',
    [string] $Project = 'Internal',
    [string] $Repository = 'review-guard-sandbox',
    [string] $TargetBranch = 'main',

    # How long to wait for the line count pipeline run on the PR.
    [int] $PipelineTimeoutSeconds = 600,

    # How long to wait for the expected status after the vote.
    [int] $StatusTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$StatusGenre = 'review-guard'
$StatusName = 'review-depth'
$ZeroId = '0000000000000000000000000000000000000000'

# ---------- helpers ----------

# Runs an az command and returns the parsed JSON. Throws on a non-zero exit code.
function Invoke-AzJson {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $AzArgs)
    $out = & az @AzArgs --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "az $($AzArgs -join ' ') failed with exit code $LASTEXITCODE" }
    if ([string]::IsNullOrWhiteSpace(($out -join ''))) { return $null }
    return ($out -join "`n") | ConvertFrom-Json -Depth 50
}

# Calls a REST resource through "az devops invoke". The az devops login credential is used.
function Invoke-DevOps {
    param(
        [Parameter(Mandatory)] [string] $Area,
        [Parameter(Mandatory)] [string] $Resource,
        [Parameter(Mandatory)] [hashtable] $Route,
        [string] $Method = 'GET',
        [object] $Body,
        [hashtable] $Query
    )
    $azArgs = @(
        'devops', 'invoke',
        '--org', $Organization,
        '--area', $Area,
        '--resource', $Resource,
        '--http-method', $Method,
        '--api-version', '7.1'
    )
    $azArgs += '--route-parameters'
    $azArgs += ($Route.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    if ($Query) {
        $azArgs += '--query-parameters'
        $azArgs += ($Query.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    }
    $tmp = $null
    try {
        if ($null -ne $Body) {
            $tmp = [System.IO.Path]::GetTempFileName()
            $Body | ConvertTo-Json -Depth 50 | Set-Content -Path $tmp -Encoding utf8NoBOM
            $azArgs += @('--in-file', $tmp)
        }
        return Invoke-AzJson @azArgs
    }
    finally {
        if ($tmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

function Get-RepoRoute {
    return @{ project = $Project; repositoryId = $Repository }
}

function New-FileContent([int] $Lines, [string] $Scenario) {
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 1; $i -le $Lines; $i++) {
        [void] $sb.AppendLine("// $Scenario line $i of $Lines")
    }
    return $sb.ToString()
}

# Creates a new branch from main with one commit that adds one file. Uses the Git Pushes API.
function New-BranchWithFile {
    param([string] $Branch, [string] $Path, [string] $Content)

    $main = Invoke-AzJson repos ref list --org $Organization --project $Project `
        --repository $Repository --filter "heads/$TargetBranch"
    $mainCommit = ($main | Where-Object { $_.name -eq "refs/heads/$TargetBranch" }).objectId
    if (-not $mainCommit) { throw "Branch $TargetBranch not found in $Repository" }

    $body = @{
        refUpdates = @(@{ name = "refs/heads/$Branch"; oldObjectId = $ZeroId })
        commits    = @(@{
                comment = "e2e: add $Path"
                parents = @($mainCommit)
                changes = @(@{
                        changeType = 'add'
                        item       = @{ path = $Path }
                        newContent = @{ content = $Content; contentType = 'rawtext' }
                    })
            })
    }
    $push = Invoke-DevOps -Area git -Resource pushes -Route (Get-RepoRoute) -Method POST -Body $body
    return $push.refUpdates[0].newObjectId
}

function New-PullRequest([string] $Branch, [string] $Title) {
    $pr = Invoke-AzJson repos pr create --org $Organization --project $Project `
        --repository $Repository --source-branch $Branch --target-branch $TargetBranch `
        --title $Title --description 'Created by tests/e2e/Run-E2E.ps1. Safe to abandon.'
    return [int] $pr.pullRequestId
}

function Set-Approve([int] $PrId) {
    Invoke-AzJson repos pr set-vote --org $Organization --id $PrId --vote approve | Out-Null
}

# Adds one inline thread on the new file. threadContext with a file path and a line makes it inline.
function Add-InlineComment([int] $PrId, [string] $Path, [int] $Line) {
    $route = Get-RepoRoute
    $route.pullRequestId = $PrId
    $body = @{
        comments      = @(@{
                parentCommentId = 0
                content         = "e2e: checked line $Line, the loop bound looks right."
                commentType     = 1
            })
        status        = 1
        threadContext = @{
            filePath       = $Path
            rightFileStart = @{ line = $Line; offset = 1 }
            rightFileEnd   = @{ line = $Line; offset = 5 }
        }
    }
    Invoke-DevOps -Area git -Resource pullRequestThreads -Route $route -Method POST -Body $body | Out-Null
}

# Returns the newest review-depth status on the PR, or $null.
function Get-ReviewDepthStatus([int] $PrId) {
    $route = Get-RepoRoute
    $route.pullRequestId = $PrId
    $res = Invoke-DevOps -Area git -Resource pullRequestStatuses -Route $route
    $items = @($res.value | Where-Object { $_.context.genre -eq $StatusGenre -and $_.context.name -eq $StatusName })
    if ($items.Count -eq 0) { return $null }
    return $items | Sort-Object { [int] $_.id } | Select-Object -Last 1
}

# Waits until the status has one of the expected states. Returns the last status seen.
function Wait-ReviewDepthStatus([int] $PrId, [string[]] $States, [int] $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Get-ReviewDepthStatus $PrId
        if ($last -and $States -contains $last.state) { return $last }
        Start-Sleep -Seconds 5
    }
    return $last
}

# Waits until the PR build (reason pullRequest, branch refs/pull/<id>/merge) has completed.
function Wait-PrPipeline([int] $PrId, [int] $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $runs = @(Invoke-AzJson pipelines runs list --org $Organization --project $Project `
                --branch "refs/pull/$PrId/merge" --reason pullRequest --top 5)
        $done = $runs | Where-Object { $_.status -eq 'completed' } | Select-Object -First 1
        if ($done) {
            if ($done.result -ne 'succeeded') { throw "Line count pipeline run $($done.id) ended with result $($done.result)" }
            return $done
        }
        Start-Sleep -Seconds 10
    }
    throw "Line count pipeline did not complete for PR $PrId within $TimeoutSeconds s"
}

function Remove-Scenario([Nullable[int]] $PrId, [string] $Branch, [string] $CommitId) {
    if ($PrId) {
        try {
            Invoke-AzJson repos pr update --org $Organization --id $PrId --status abandoned | Out-Null
        }
        catch { Write-Warning "Could not abandon PR $PrId : $_" }
    }
    if ($Branch -and $CommitId) {
        try {
            Invoke-AzJson repos ref delete --org $Organization --project $Project `
                --repository $Repository --name "heads/$Branch" --object-id $CommitId | Out-Null
        }
        catch { Write-Warning "Could not delete branch $Branch : $_" }
    }
}

# ---------- scenarios ----------

# Each scenario returns @{ Passed = bool; Detail = string }.
function Invoke-Scenario([string] $Name) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $branch = "e2e/$Name-$stamp"
    # critical-small writes under a critical path (critical-paths.json in the Function).
    $path = if ($Name -eq 'critical-small') { "/src/SampleApp/Payments/E2e$stamp.cs" } else { "/e2e/$Name-$stamp.cs" }
    $prId = $null
    $commit = $null

    $lines = switch ($Name) { 'small' { 50 } 'critical-small' { 30 } default { 450 } }

    try {
        Write-Host "[$Name] push $lines lines to $branch"
        $commit = New-BranchWithFile -Branch $branch -Path $path -Content (New-FileContent $lines $Name)
        $prId = New-PullRequest $branch "e2e $Name $stamp"
        Write-Host "[$Name] PR $prId created"

        switch ($Name) {
            'approve-before-pipeline' {
                # Vote at once, before the line count exists.
                Set-Approve $prId
                Write-Host "[$Name] approved before the pipeline finished"
                $first = Wait-ReviewDepthStatus $prId @('pending', 'failed', 'succeeded') $StatusTimeoutSeconds
                if (-not $first -or $first.state -ne 'pending') {
                    return @{ Passed = $false; Detail = "expected pending first, got: $(${first}?.state) $(${first}?.description)" }
                }
                Write-Host "[$Name] saw pending: $($first.description)"
                Wait-PrPipeline $prId $PipelineTimeoutSeconds | Out-Null
                # The build-completed service hook starts a new check.
                $final = Wait-ReviewDepthStatus $prId @('failed', 'succeeded') $StatusTimeoutSeconds
                $ok = $final -and $final.state -eq 'failed'
                return @{ Passed = $ok; Detail = "pending -> $(${final}?.state): $(${final}?.description)" }
            }
            default {
                # Approve at once. The clock starts at the push, so waiting for the pipeline
                # first would use up the allowed time. The build-completed hook re-checks
                # with the stored approval time.
                if ($Name -eq 'large-with-comment') {
                    Add-InlineComment $prId $path 10
                    Write-Host "[$Name] inline comment added"
                }

                Set-Approve $prId
                Write-Host "[$Name] approved, waiting for the line count pipeline"
                Wait-PrPipeline $prId $PipelineTimeoutSeconds | Out-Null

                $expected = if ($Name -in 'large-fast', 'critical-small') { 'failed' } else { 'succeeded' }
                $status = Wait-ReviewDepthStatus $prId @('failed', 'succeeded') $StatusTimeoutSeconds
                $ok = $status -and $status.state -eq $expected
                return @{ Passed = $ok; Detail = "expected $expected, got $(${status}?.state): $(${status}?.description)" }
            }
        }
    }
    catch {
        return @{ Passed = $false; Detail = "error: $($_.Exception.Message)" }
    }
    finally {
        Remove-Scenario $prId $branch $commit
    }
}

# ---------- main ----------

$results = foreach ($s in $Scenarios) {
    $r = Invoke-Scenario $s
    [pscustomobject]@{
        Scenario = $s
        Result   = if ($r.Passed) { 'PASS' } else { 'FAIL' }
        Detail   = $r.Detail
    }
}

$results | Format-Table -AutoSize -Wrap

if ($results | Where-Object Result -eq 'FAIL') { exit 1 }
exit 0
