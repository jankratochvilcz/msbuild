<#
.SYNOPSIS
    Survey an Azure DevOps pipeline for flaky tests over a time window.

.DESCRIPTION
    Lists completed builds for an AzDO pipeline definition in a sliding window,
    fetches failed test results (including retry-recovered "ghost" failures), and
    ranks tests by their flake signal.

    A test is reported as flaky when:
      - it failed in a small fraction of the pipeline's builds in the window
        (i.e., not a systematic regression), AND
      - it failed across multiple distinct source branches / PRs (rules out a
        single-PR break), OR was retry-recovered within a build.

    Output is written as Markdown to the console and (optionally) as JSON for
    automation.

.PARAMETER DefinitionId
    AzDO pipeline definition id. Default 75 = dotnet-msbuild-public.

.PARAMETER Organization
    AzDO organization. Default 'dnceng-public'.

.PARAMETER Project
    AzDO project. Default 'public'.

.PARAMETER Days
    Look-back window in days. Default 7.

.PARAMETER JsonOutPath
    Optional file path to write the ranked flake list as JSON.

.PARAMETER MaxFailRatio
    Maximum (#failed builds / #total builds) for a test to count as flake rather
    than systematic failure. Default 0.30.

.PARAMETER TopCount
    Show only the top N flakes. Default 20.

.EXAMPLE
    pwsh ./Find-FlakyTests.ps1 -Days 7 -JsonOutPath flakes.json -TopCount 25
#>
[CmdletBinding()]
param(
    [int]$DefinitionId = 75,
    [string]$Organization = 'dnceng-public',
    [string]$Project = 'public',
    [int]$Days = 7,
    [string]$JsonOutPath,
    [double]$MaxFailRatio = 0.30,
    [int]$TopCount = 20
)

$ErrorActionPreference = 'Stop'

# ----- Auth -----
$azdoResource = '499b84ac-1321-427f-aa17-267ca6975798'
try {
    $token = az account get-access-token --resource $azdoResource --query accessToken -o tsv 2>$null
} catch {}
if (-not $token) {
    throw "Failed to acquire AzDO token. Run 'az login' first."
}
$headers = @{ Authorization = "Bearer $token" }
$apiBase = "https://dev.azure.com/$Organization/$Project/_apis"
$tmrBase = "https://vstmr.dev.azure.com/$Organization/$Project/_apis"

# ----- 1. List builds -----
$minTime = (Get-Date).ToUniversalTime().AddDays(-$Days).ToString('o')
Write-Host "Fetching builds for definitionId=$DefinitionId since $minTime ..."
$buildsUrl = "$apiBase/build/builds?definitions=$DefinitionId&minTime=$minTime&statusFilter=completed&api-version=7.1&`$top=500"
$allBuilds = (Invoke-RestMethod -Headers $headers $buildsUrl).value
$totalBuilds = $allBuilds.Count
Write-Host "  $totalBuilds completed builds in window."

# Inspect builds that have failures OR retry-recovered tests. Use build summary to discover both.
$buildsToScan = $allBuilds | Where-Object { $_.result -ne 'canceled' }

# ----- 2. For each build: fetch summary + failed test details -----
$failedRecords = [System.Collections.Generic.List[object]]::new()
$buildFailedTestCount = @{}
$progress = 0
foreach ($b in $buildsToScan) {
    $progress++
    Write-Progress -Activity "Scanning builds" -Status "$progress / $($buildsToScan.Count) (build $($b.id))" -PercentComplete (100*$progress/$buildsToScan.Count)

    # Build summary tells us how many failures AzDO recorded (catches retry-recovered ghosts)
    try {
        $summary = Invoke-RestMethod -Headers $headers "$apiBase/test/resultsummarybybuild?buildId=$($b.id)&api-version=7.1-preview"
        $failedCount = [int]$summary.aggregatedResultsAnalysis.resultsByOutcome.Failed.count
    } catch {
        $failedCount = 0
    }
    if ($failedCount -le 0) { continue }
    $buildFailedTestCount[$b.id] = $failedCount

    # Failed test details via vstmr testresults endpoint (the one the AzDO UI uses).
    # The classic test/resultdetailsbybuild endpoint ignores outcome filters and strips test names.
    try {
        $details = Invoke-RestMethod -Headers $headers "$tmrBase/testresults/resultsbybuild?buildId=$($b.id)&outcomes=Failed&api-version=7.1-preview"
    } catch {
        Write-Warning "  build $($b.id): vstmr testresults failed: $($_.Exception.Message)"
        continue
    }
    foreach ($r in $details.value) {
        $name = $r.automatedTestName
        if (-not $name) { $name = $r.testCaseTitle }
        if (-not $name) { continue }
        $failedRecords.Add([pscustomobject]@{
            test       = $name
            run        = $r.testRun.name
            buildId    = $b.id
            buildNum   = $b.buildNumber
            branch     = $b.sourceBranch
            started    = $b.startTime
            runId      = $r.runId
            resultId   = $r.id
            errorMsg   = $null
        })
    }
}
Write-Progress -Activity "Scanning builds" -Completed

Write-Host ""
Write-Host "Failed test records collected: $($failedRecords.Count)"
$buildsWithFailures = $buildFailedTestCount.Count
Write-Host "Builds with >=1 failed test: $buildsWithFailures"

# ----- 3. Aggregate by test -----
$byTest = $failedRecords | Group-Object test
$flakes = foreach ($g in $byTest) {
    $records = @($g.Group)
    $distinctBuilds = @($records | Select-Object -ExpandProperty buildId -Unique)
    $distinctBranches = @($records | Select-Object -ExpandProperty branch -Unique)
    $failingFraction = if ($totalBuilds -gt 0) { $distinctBuilds.Count / [double]$totalBuilds } else { 0 }

    # Pick most common error message
    $msgGroup = $records | Where-Object errorMsg | Group-Object errorMsg | Sort-Object Count -Descending | Select-Object -First 1
    $msg = if ($msgGroup) { $msgGroup.Name } else { $null }

    # Determine assembly from run name (e.g., "Microsoft.Build.Engine.UnitTests_net10.0_x64")
    $runNames = @($records | Select-Object -ExpandProperty run -Unique | Where-Object { $_ })
    $asmNames = $runNames | ForEach-Object { ($_ -split '_')[0] }
    $asmGroup = $asmNames | Group-Object | Sort-Object Count -Descending | Select-Object -First 1
    $asm = if ($asmGroup) { $asmGroup.Name } else { '' }

    [pscustomobject]@{
        Test               = [string]$g.Name
        Assembly           = [string]$asm
        FailingBuilds      = [int]$distinctBuilds.Count
        DistinctBranches   = [int]$distinctBranches.Count
        FailRatio          = [Math]::Round($failingFraction, 3)
        TotalFailRecords   = [int]$records.Count
        ExampleBuildIds    = @($distinctBuilds | Select-Object -First 5)
        FirstSeen          = ($records | Sort-Object started | Select-Object -First 1).started
        LastSeen           = ($records | Sort-Object started -Descending | Select-Object -First 1).started
        ExampleError       = [string]$msg
    }
}

# A test is "flaky" if it's NOT a systematic regression AND not a one-build-only PR break.
$ranked = $flakes |
    Where-Object { $_.FailRatio -le $MaxFailRatio } |
    Where-Object { $_.DistinctBranches -ge 1 } |
    Sort-Object @{Expression='FailingBuilds';Descending=$true},
                @{Expression='DistinctBranches';Descending=$true}

$top = $ranked | Select-Object -First $TopCount

# ----- Enrich top flakes with one example error message -----
Write-Host "Fetching error messages for top $($top.Count) flakes ..."
foreach ($t in $top) {
    $sample = $failedRecords | Where-Object { $_.test -eq $t.Test -and $_.runId -and $_.resultId } | Select-Object -First 1
    if ($sample) {
        try {
            $full = Invoke-RestMethod -Headers $headers "$apiBase/test/Runs/$($sample.runId)/Results/$($sample.resultId)?api-version=7.1"
            $msg = $full.errorMessage
            if ($msg) { $msg = ($msg -replace '\s+', ' ').Substring(0, [Math]::Min(280, $msg.Length)) }
            $t | Add-Member -NotePropertyName ExampleError -NotePropertyValue $msg -Force
        } catch {}
    }
}

# ----- 4. Output -----
Write-Host ""
Write-Host "=== Top $TopCount flaky tests (window: $Days days, $totalBuilds builds) ==="
Write-Host ""
$top | Format-Table Test, Assembly, FailingBuilds, DistinctBranches, FailRatio, LastSeen -AutoSize -Wrap

if ($JsonOutPath) {
    $top | ConvertTo-Json -Depth 5 | Set-Content -Path $JsonOutPath -Encoding UTF8
    Write-Host ""
    Write-Host "JSON written: $JsonOutPath"
}

# Ghost flake report: builds where AzDO summary count > what we extracted in TRX
Write-Host ""
Write-Host "=== Builds with retry-recovered (ghost) failures - drill into UI for names ==="
$ghosts = foreach ($kv in $buildFailedTestCount.GetEnumerator()) {
    $extracted = ($failedRecords | Where-Object buildId -eq $kv.Key).Count
    if ($kv.Value -gt $extracted) {
        [pscustomobject]@{
            BuildId        = $kv.Key
            ReportedFailed = $kv.Value
            ExtractedNames = $extracted
            GhostCount     = $kv.Value - $extracted
        }
    }
}
$ghosts | Sort-Object GhostCount -Desc | Format-Table -AutoSize
