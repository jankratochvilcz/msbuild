---
name: flaky-test-survey
description: Identify the top flaky tests in an MSBuild Azure DevOps pipeline over a sliding time window. Use when the user asks "what tests are flaking", wants a weekly/periodic flake report, or wants to triage CI noise. Produces a ranked Markdown table plus optional JSON for automation (e.g., filing issues).
---

# Flaky Test Survey

Periodic survey of an Azure DevOps pipeline (default: pipeline 75 in `dnceng-public/public`, the dotnet-msbuild-public CI) for tests that flake.

## When to Use

- "What tests are flaking this week?"
- "Find top flaky tests in the past N days"
- "Generate a weekly flake report for MSBuild CI"
- Before triaging CI noise or before a release

## Prerequisites

- `az` CLI signed in (`az login --allow-no-subscriptions` is fine).
- PowerShell 5.1+ or PowerShell Core.

The script acquires an Azure DevOps OAuth token via `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798`. No PAT required.

## How It Works

For every completed build in the window the script calls:

1. `GET test/resultsummarybybuild` — gets total Failed count (catches retry-recovered ghost failures that don't appear in TRX).
2. `GET test/resultdetailsbybuild?outcomes=Failed&groupBy=TestRun` — fully-qualified failed test names with error messages.

Then aggregates by test, computes `FailRatio = failingBuilds / totalBuilds` and `DistinctBranches`, and **filters out systematic regressions** (`FailRatio > MaxFailRatio`, default 30%). Ranked by `FailingBuilds` desc.

Ghost-failure builds (where AzDO summary > extracted TRX names) are listed separately — those need manual UI inspection because the test runner retried and the pass overwrites the failure in the result store.

## Usage

```powershell
# Default: pipeline 75, past 7 days, print Markdown table
pwsh .github/skills/flaky-test-survey/Find-FlakyTests.ps1

# Wider window, save JSON for automation (issue filing)
pwsh .github/skills/flaky-test-survey/Find-FlakyTests.ps1 -Days 14 -JsonOutPath flakes.json -TopCount 25
```

## Weekly Cadence

Run on Monday morning, file/refresh issues for the top N flakes, link the build IDs in the issue body.

## Parameters

| Parameter        | Default            | Notes                                                   |
|------------------|--------------------|---------------------------------------------------------|
| `-DefinitionId`  | 75                 | dotnet-msbuild-public                                   |
| `-Organization`  | `dnceng-public`    |                                                         |
| `-Project`       | `public`           |                                                         |
| `-Days`          | 7                  | Look-back window                                        |
| `-MaxFailRatio`  | 0.30               | Above this, treat as systematic regression, not flake   |
| `-TopCount`     | 20                 | Cap on the printed table                                |
| `-JsonOutPath`   | (none)             | Optional JSON dump for downstream automation            |

## Caveats

- AzDO's anonymous APIs do NOT expose test results on `dnceng-public`; auth is required.
- A test that fails only on a single PR's builds is reported but may be a real PR regression — inspect `DistinctBranches`. `DistinctBranches >= 2` is a stronger flake signal.
- Tests that fail and then pass on retry within the same build don't appear in `resultdetailsbybuild` (only the passing result wins). They are reported in the "ghost failures" section, by build, without test names — use the AzDO UI for those.
