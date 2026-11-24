<#
.SYNOPSIS
  Counts changed lines between the PR target branch and the PR source commit.
  Writes the count onto the PR as two properties, so the Review Guard Function can read it.

.DESCRIPTION
  Line count = added + deleted lines from "git diff --numstat", without excluded files.
  Binary files ("-" in numstat) count 0. A pure rename counts only its changed lines.

  PR properties written (Azure DevOps Pull Request Properties API, api-version 7.1):
    ReviewGuard.ChangedLines  integer as text, for example "450"
    ReviewGuard.SourceCommit  the PR source commit id (not the merge commit)

  Run with -DryRun to only print the count (local testing, no API call).
#>
[CmdletBinding()]
param(
    [string] $TargetBranch = $env:SYSTEM_PULLREQUEST_TARGETBRANCH,
    [string] $SourceCommit = $env:SYSTEM_PULLREQUEST_SOURCECOMMITID,
    [string] $ExcludePatterns = $env:EXCLUDEPATTERNS,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# A build that is not for a PR has nothing to count. Exit with success.
if (-not $DryRun -and $env:BUILD_REASON -ne 'PullRequest') {
    Write-Host "Build reason is '$($env:BUILD_REASON)', not a PR. Nothing to do."
    exit 0
}

if (-not $TargetBranch -or -not $SourceCommit) {
    throw 'TargetBranch and SourceCommit are required.'
}

# "refs/heads/main" -> "main"
$target = $TargetBranch -replace '^refs/heads/', ''

# Make sure the target branch exists locally as origin/<target>.
git fetch --quiet origin "+refs/heads/${target}:refs/remotes/origin/${target}"
if ($LASTEXITCODE -ne 0) { throw "git fetch of '$target' failed." }

# Turn a glob into a regex.
#   "**/" = any folders (also none), "**" = anything, "*" = anything except "/", "?" = one char.
# A pattern without "/" matches the file name only. A pattern with "/" matches the full path.
function ConvertTo-GlobRegex([string] $glob) {
    $sb = [System.Text.StringBuilder]::new('^')
    $i = 0
    while ($i -lt $glob.Length) {
        $c = $glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $glob.Length -and $glob[$i + 1] -eq '*') {
            if ($i + 2 -lt $glob.Length -and $glob[$i + 2] -eq '/') {
                [void]$sb.Append('(.*/)?'); $i += 3
            }
            else {
                [void]$sb.Append('.*'); $i += 2
            }
            continue
        }
        switch ($c) {
            '*' { [void]$sb.Append('[^/]*') }
            '?' { [void]$sb.Append('[^/]') }
            default { [void]$sb.Append([regex]::Escape([string]$c)) }
        }
        $i++
    }
    [void]$sb.Append('$')
    return [regex]::new($sb.ToString(), 'IgnoreCase')
}

$rules = @(
    ($ExcludePatterns -split ';') |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        ForEach-Object { [pscustomobject]@{ Glob = $_; Regex = ConvertTo-GlobRegex $_; FullPath = $_.Contains('/') } }
)

function Test-Excluded([string] $path) {
    $name = Split-Path -Leaf $path
    foreach ($r in $rules) {
        $subject = if ($r.FullPath) { $path } else { $name }
        if ($r.Regex.IsMatch($subject)) { return $r.Glob }
    }
    return $null
}

# "-z" gives NUL separated output, so paths with spaces and renames parse safely.
# Normal file: "added<TAB>deleted<TAB>path<NUL>"
# Rename:      "added<TAB>deleted<TAB><NUL>oldpath<NUL>newpath<NUL>"
$raw = git -c core.quotepath=off diff --numstat -z -M "origin/$target...$SourceCommit"
if ($LASTEXITCODE -ne 0) { throw "git diff failed for origin/$target...$SourceCommit." }

$tokens = ($raw -join "`n") -split "`0"
$total = 0
$counted = 0
$excluded = 0
$i = 0
while ($i -lt $tokens.Count) {
    $t = $tokens[$i].Trim("`n")
    $i++
    if (-not $t) { continue }

    $parts = $t -split "`t", 3
    if ($parts.Count -lt 3) { continue }
    $path = $parts[2]
    if (-not $path) {
        # Rename: the next two tokens are the old and the new path. Use the new path.
        $path = $tokens[$i + 1]
        $i += 2
    }

    $rule = Test-Excluded $path
    if ($rule) {
        Write-Host "  excluded ($rule): $path"
        $excluded++
        continue
    }

    # Binary files show "-" for both numbers. They count 0.
    $added = if ($parts[0] -eq '-') { 0 } else { [int]$parts[0] }
    $deleted = if ($parts[1] -eq '-') { 0 } else { [int]$parts[1] }
    $total += $added + $deleted
    $counted++
}

Write-Host "Changed lines: $total ($counted files counted, $excluded files excluded)"
Write-Host "Source commit: $SourceCommit"

if ($DryRun) { return $total }

# Write the PR properties. JSON Patch, "add" sets or replaces the value.
$uri = '{0}{1}/_apis/git/repositories/{2}/pullRequests/{3}/properties?api-version=7.1' -f `
    $env:SYSTEM_COLLECTIONURI, $env:SYSTEM_TEAMPROJECTID, $env:BUILD_REPOSITORY_ID, $env:SYSTEM_PULLREQUEST_PULLREQUESTID

$body = ConvertTo-Json -Depth 3 -InputObject @(
    @{ op = 'add'; path = '/ReviewGuard.ChangedLines'; value = "$total" },
    @{ op = 'add'; path = '/ReviewGuard.SourceCommit'; value = $SourceCommit }
)

if (-not $env:SYSTEM_ACCESSTOKEN) { throw 'SYSTEM_ACCESSTOKEN is not mapped into the step env.' }

Invoke-RestMethod -Method Patch -Uri $uri -Body $body `
    -ContentType 'application/json-patch+json' `
    -Headers @{ Authorization = "Bearer $($env:SYSTEM_ACCESSTOKEN)" } | Out-Null

Write-Host "Wrote ReviewGuard.ChangedLines=$total on PR $($env:SYSTEM_PULLREQUEST_PULLREQUESTID)."
