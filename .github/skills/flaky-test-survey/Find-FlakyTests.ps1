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

    Output is written as a Markdown table to the console and (optionally) as
    JSON for automation.

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

.PARAMETER MinDistinctBranches
    Minimum distinct branches required for a test to qualify as flake (vs a
    single-PR break). Default 1 (off); use 2 for stricter triage.

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
    [int]$MinDistinctBranches = 1,
    [int]$TopCount = 20
)

$ErrorActionPreference = 'Stop'

# ----- Auth -----
$azdoResource = '499b84ac-1321-427f-aa17-267ca6975798'
# az is a native exe, so it doesn't throw on failure. Capture stderr so the
# user sees the underlying message (e.g. "Please run 'az login'") if it fails.
$token = az account get-access-token --resource $azdoResource --query accessToken -o tsv
if (-not $token) {
    throw "Failed to acquire AzDO token. Run 'az login --allow-no-subscriptions' first."
}
$headers = @{ Authorization = "Bearer $token" }
$apiBase = "https://dev.azure.com/$Organization/$Project/_apis"
$tmrBase = "https://vstmr.dev.azure.com/$Organization/$Project/_apis"

# ----- Helper: invoke an AzDO REST URL with retry + paging -----
# Returns the full JSON body for one page and the continuation token (if any).
function Invoke-AzdoRequest {
    param(
        [Parameter(Mandatory)] [string]$Uri,
        [int]$MaxRetries = 4
    )

    $delaySeconds = 1
    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
        try {
            $resp = Invoke-WebRequest -Headers $headers -Uri $Uri -UseBasicParsing -ErrorAction Stop
            $continuation = $resp.Headers['x-ms-continuationtoken']
            if ($continuation -is [array]) { $continuation = $continuation[0] }
            $body = if ($resp.Content) { $resp.Content | ConvertFrom-Json } else { $null }
            return [pscustomobject]@{ Body = $body; Continuation = $continuation }
        } catch {
            $status = $null
            $retryAfter = $null
            if ($_.Exception.Response) {
                $status = [int]$_.Exception.Response.StatusCode
                $retryAfter = $_.Exception.Response.Headers['Retry-After']
                if ($retryAfter -is [array]) { $retryAfter = $retryAfter[0] }
            }

            $transient = ($status -eq 429) -or ($status -ge 500 -and $status -lt 600) -or (-not $status)
            if (-not $transient -or $attempt -eq $MaxRetries) {
                Write-Warning "  Request failed (status=$status, attempt=$attempt/$MaxRetries): $Uri"
                throw
            }

            $wait = if ($retryAfter -and [int]::TryParse([string]$retryAfter, [ref]$null)) {
                [int]$retryAfter
            } else {
                $delaySeconds
            }
            Write-Warning "  Transient $status on attempt $attempt/$MaxRetries; sleeping ${wait}s before retry."
            Start-Sleep -Seconds $wait
            $delaySeconds = [Math]::Min($delaySeconds * 2, 30)
        }
    }
}

# Pages a collection-typed endpoint (one that returns {value:[...]}) using
# the x-ms-continuationtoken header. Honors the AzDO convention of passing
# the token back as 'continuationToken=' (some endpoints) or as a header.
function Get-AzdoPaged {
    param(
        [Parameter(Mandatory)] [string]$Uri,
        [int]$MaxPages = 50
    )

    $all = [System.Collections.Generic.List[object]]::new()
    $pageUri = $Uri
    for ($i = 0; $i -lt $MaxPages; $i++) {
        $page = Invoke-AzdoRequest -Uri $pageUri
        if ($page.Body -and $page.Body.value) {
            foreach ($v in $page.Body.value) { $all.Add($v) | Out-Null }
        }
        if (-not $page.Continuation) { return $all }
        $sep = if ($pageUri.Contains('?')) { '&' } else { '?' }
        $token = [System.Uri]::EscapeDataString($page.Continuation)
        # Strip any prior continuationToken= param before appending the new one.
        $pageUri = ($Uri -replace '&continuationToken=[^&]*', '') + "${sep}continuationToken=$token"
    }
    Write-Warning "  Paging cap hit ($MaxPages pages) for $Uri; results may be truncated."
    return $all
}

# ----- 1. List builds (paged) -----
$minTime = (Get-Date).ToUniversalTime().AddDays(-$Days).ToString('o')
Write-Host "Fetching builds for definitionId=$DefinitionId since $minTime ..."
$buildsUrl = "$apiBase/build/builds?definitions=$DefinitionId&minTime=$minTime&statusFilter=completed&api-version=7.1"
$allBuilds = Get-AzdoPaged -Uri $buildsUrl
$totalBuilds = $allBuilds.Count
Write-Host "  $totalBuilds completed builds in window."

$buildsToScan = $allBuilds | Where-Object { $_.result -ne 'canceled' }

# ----- 2. For each build: fetch summary + failed test details -----
$failedRecords = [System.Collections.Generic.List[object]]::new()
$buildFailedTestCount = @{}
$progress = 0
foreach ($b in $buildsToScan) {
    $progress++
    Write-Progress -Activity "Scanning builds" -Status "$progress / $($buildsToScan.Count) (build $($b.id))" -PercentComplete (100*$progress/$buildsToScan.Count)

    # Build summary tells us how many failures AzDO recorded (catches retry-recovered ghosts).
    # api-version=7.1-preview.1 is the current revision; the bare '7.1-preview' alias has
    # been observed to 4xx on some org/project combos.
    try {
        $page = Invoke-AzdoRequest -Uri "$apiBase/test/resultsummarybybuild?buildId=$($b.id)&api-version=7.1-preview.1"
        $failedCount = [int]$page.Body.aggregatedResultsAnalysis.resultsByOutcome.Failed.count
    } catch {
        $failedCount = 0
    }
    if ($failedCount -le 0) { continue }
    $buildFailedTestCount[$b.id] = $failedCount

    # Failed test details via vstmr testresults endpoint (the one the AzDO UI uses).
    # The classic test/resultdetailsbybuild endpoint ignores outcome filters and strips test names.
    try {
        $details = Get-AzdoPaged -Uri "$tmrBase/testresults/resultsbybuild?buildId=$($b.id)&outcomes=Failed&api-version=7.1-preview.1"
    } catch {
        Write-Warning "  build $($b.id): vstmr testresults failed, skipping."
        continue
    }
    foreach ($r in $details) {
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

# Index by test for O(1) enrichment lookup later.
$recordsByTest = $failedRecords | Group-Object test -AsHashTable -AsString

# ----- 3. Aggregate by test -----
$byTest = $failedRecords | Group-Object test
$flakes = foreach ($g in $byTest) {
    $records = @($g.Group)
    $distinctBuilds = @($records | Select-Object -ExpandProperty buildId -Unique)
    $distinctBranches = @($records | Select-Object -ExpandProperty branch -Unique)
    $failingFraction = if ($totalBuilds -gt 0) { $distinctBuilds.Count / [double]$totalBuilds } else { 0 }

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
        ExampleError       = $null
    }
}

# A test is "flaky" if it's NOT a systematic regression AND it spans the configured number of branches.
$ranked = $flakes |
    Where-Object { $_.FailRatio -le $MaxFailRatio } |
    Where-Object { $_.DistinctBranches -ge $MinDistinctBranches } |
    Sort-Object @{Expression='FailingBuilds';Descending=$true},
                @{Expression='DistinctBranches';Descending=$true}

$top = $ranked | Select-Object -First $TopCount

# ----- Enrich top flakes with one example error message -----
Write-Host "Fetching error messages for top $($top.Count) flakes ..."
foreach ($t in $top) {
    $bucket = $recordsByTest[$t.Test]
    $sample = $bucket | Where-Object { $_.runId -and $_.resultId } | Select-Object -First 1
    if ($sample) {
        try {
            $page = Invoke-AzdoRequest -Uri "$apiBase/test/Runs/$($sample.runId)/Results/$($sample.resultId)?api-version=7.1"
            $msg = $page.Body.errorMessage
            if ($msg) { $msg = ($msg -replace '\s+', ' ').Substring(0, [Math]::Min(280, $msg.Length)) }
            $t.ExampleError = $msg
        } catch {}
    }
}

# ----- 4. Output (Markdown table) -----
Write-Host ""
Write-Host "=== Top $TopCount flaky tests (window: $Days days, $totalBuilds builds) ==="
Write-Host ""
Write-Host "| Test | Assembly | FailingBuilds | DistinctBranches | FailRatio | LastSeen |"
Write-Host "|---|---|---:|---:|---:|---|"
foreach ($t in $top) {
    $name = ($t.Test -replace '\|', '\|')
    $asm  = ($t.Assembly -replace '\|', '\|')
    Write-Host "| $name | $asm | $($t.FailingBuilds) | $($t.DistinctBranches) | $($t.FailRatio) | $($t.LastSeen) |"
}

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
$ghosts | Sort-Object GhostCount -Descending | Format-Table -AutoSize
