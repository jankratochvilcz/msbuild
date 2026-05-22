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
2. `GET {vstmr}/testresults/resultsbybuild?outcomes=Failed` — fully-qualified failed test names. The classic `test/resultdetailsbybuild` endpoint silently ignores the outcome filter and strips test names, so we use the vstmr endpoint that the AzDO UI itself uses.
3. For the top N flakes only, `GET test/Runs/{runId}/Results/{resultId}` — fetches the example error message.

All requests page on `x-ms-continuationtoken` and retry on 429 / 5xx with `Retry-After`-aware backoff.

Then aggregates by test, computes `FailRatio = failingBuilds / totalBuilds` and `DistinctBranches`, and **filters out systematic regressions** (`FailRatio > MaxFailRatio`, default 30%) and **single-branch noise** (`DistinctBranches < MinDistinctBranches`, default 1; set 2 for stricter triage). Ranked by `FailingBuilds` desc.

Ghost-failure builds (where AzDO summary > extracted TRX names) are listed separately — those need manual UI inspection because the test runner retried and the pass overwrites the failure in the result store.

## Usage

```powershell
# Default: pipeline 75, past 7 days, print Markdown table
pwsh .github/skills/flaky-test-survey/Find-FlakyTests.ps1

# Wider window, save JSON for automation (issue filing)
pwsh .github/skills/flaky-test-survey/Find-FlakyTests.ps1 -Days 14 -JsonOutPath flakes.json -TopCount 25

# Skip tests already tracked, emit one issue-body draft per remaining top flake
pwsh .github/skills/flaky-test-survey/Find-FlakyTests.ps1 `
    -ExcludeTestsFile .github/skills/flaky-test-survey/known-flakes.txt `
    -IssueDraftDir ./out/flake-drafts
```

`-ExcludeTestsFile` is a plain-text list (one fully-qualified test name per line, `#` comments allowed) of flakes already on the board or already fixed-but-cooling. Curate it as you triage so the next run focuses on genuinely new noise.

`-IssueDraftDir` writes one `<short-name>.md` per top flake using the issue-body template below. Review each draft, then file with:

```powershell
gh issue create --repo jankratochvilcz/msbuild --label flaky-test `
    --title "Flaky test: <name>" --body-file ./out/flake-drafts/<file>.md
```

## Weekly Cadence

Run on Monday morning. For each top flake **not in the exclusion list**:

1. Search `dotnet/msbuild` for an existing tracker by test name (de-dup; see below).
2. If untracked, file in the fork and add to the project board.
3. If tracked upstream and the failure looks like the *same* root cause, **comment on the upstream tracker** with the new evidence — do not file a duplicate.
4. Append the test name to `known-flakes.txt` once a tracker exists, so the next run skips it.

## Where to file: fork, not upstream

All personal-triage flake trackers live in `jankratochvilcz/msbuild` on the **"Customer Patterns Code Coverage"** project board (`jankratochvilcz/projects/2`). Upstream `dotnet/msbuild` already has its own flaky-test tracking workflow — filing personal-triage tickets there creates noise and duplicates.

Rules:

- **File in `jankratochvilcz/msbuild`** with the `flaky-test` label and title prefix `Flaky test:`.
- **Add to the board** so it shows up alongside the guard-test work:
  ```powershell
  gh project item-add 2 --owner jankratochvilcz --url <issue-url>
  ```
  (lands in Todo by default).
- **If you mistakenly filed upstream first**, close the upstream issue with a one-line redirect comment to the fork copy. Do not lose the upstream build links — paste them into the fork issue body.
- **Adding more evidence to an existing upstream tracker** (e.g., a second test hitting the same root cause) is fine to do as a comment on the upstream issue — that is sharing data, not filing a new tracker.

## De-dup against upstream before filing

Before filing a new tracker, search `dotnet/msbuild` for the test name and any obvious symptom strings from the error message:

```powershell
gh search issues "<TestName>" --repo dotnet/msbuild --json number,title,state,assignees
```

Common outcomes:

- **Already tracked, assigned, open** → skip, just add the test to your `known-flakes.txt`.
- **Tracked, but the failure here is a *different* symptom of the same root cause** → comment on the upstream tracker linking your build and noting the additional symptom. Then add the new test name to `known-flakes.txt`.
- **No tracker** → file in the fork.

The same de-dup applies inside the fork itself — search before filing.

## Issue body template

Every flaky-test tracker (fork or otherwise) follows this shape. The script's `-IssueDraftDir` pre-fills it for you; you fill in the blanks marked `<...>`.

```markdown
## Symptom

`<FullyQualifiedTestName>` failed on `main` (build [<id>](<url>), `<job name>`, <yyyy-mm-dd>):

<paste the error message verbatim, fenced as a code block>

## Where

- `<source/path/to/Test.cs>` — test `<TestName>`
- `<source/path/to/Production.cs>` — `<Class.Method>` that the test exercises

## Likely cause

<2-4 sentence hypothesis. Identify the race, missing sync, timing assumption, or environment dependency. If you cannot identify a cause, say so explicitly and propose telemetry-only as the next step.>

## Frequency

First observed in <N>h scan window (<X> of <Y> recent main builds). Tracking here to gather more data; if it recurs >1×/week, stabilize.

## Suggested next step

<One of: fix (only if confident), bump-timeout-with-telemetry, telemetry-only.>

## How to reproduce

<Concrete repro command, or "Cannot reproduce locally; only on busy CI agents" with the conditions known to amplify it.>
```

## Multiple symptoms, one root cause

If two tests fail with assertions that match the same root cause (e.g., last-line-of-stdout assertions both hitting an `AsyncStreamReader` flush race), **do not file two trackers**. Comment on the existing root-cause tracker with the second test's evidence, and add that test to the acceptance-criteria list for the eventual fix. This keeps the tracker count honest and gives the fix author a richer acceptance gate.

## Follow-up "shrink" PRs stay draft until data accumulates

When mitigating with a timeout bump (per the rules below), a follow-up PR to **shrink the budget back** once telemetry confirms a tight-but-safe value is the second half of the contract. Those follow-up PRs:

- Open as **draft**, not ready-for-review.
- Cite the elapsed-time distribution observed since the bump merged (use `Find-FlakyTests.ps1` plus the test's `Stopwatch` telemetry).
- Stay draft until **at least one full cycle (typically 3-7 days) of clean main + PR data** confirms the new budget. A clean 24-hour window is not enough — flake rates below ~1% will not surface in 20 runs.
- When marking ready, post a comment on the original mitigation PR linking the shrink PR with the supporting data.

## Workflow: Diagnose → Mitigate → Iterate

For each flaky test, an **issue is a long-lived tracker** that follows this lifecycle:

1. **Diagnose** — read the failure messages and recent passing runs. Try to identify a root cause (race, missing sync, environmental dependency, slow machine, etc.).
2. **Mitigate** — open a PR that does one or more of:
   - **Fix** the underlying race/bug (preferred — only do this if you are confident).
   - **Bump** a timeout if the failure is "ran out of time on a slow machine" and you cannot identify a sharper fix.
   - **Add telemetry** so the *next* CI failure produces enough information to diagnose. Always do this if you cannot fix.
3. **Wait for the PR to merge** (a human must merge). The issue stays **open and in-progress** even after the PR merges — the PR is not a fix-confirming event, only a hypothesis.
4. **Re-run this skill in the next cycle** (e.g., next week). For each open flake tracker:
   - If the test no longer appears in the report → close the issue with a comment linking to the report.
   - If it still appears → comment on the issue with the new diagnostics surfaced by the telemetry you added, and iterate (another PR if needed).
5. **Close the issue only when validated** — the test is absent from the report for at least one full cycle (ideally two).

### Mandatory rules when bumping a timeout

Every PR that bumps a timeout MUST also:

- **Log the actual elapsed time** the operation took (e.g., `_output.WriteLine($"Operation completed in {sw.ElapsedMilliseconds}ms (timeout was {timeoutMs}ms)")`). This is non-negotiable — without it, we cannot tell after the fact whether the bump was sufficient or whether the real timeout was much smaller than the bumped value. Future iterations will use this number to **constrain the timeout back down** to a tight but safe value.
- **Attempt to diagnose where the time is being spent** before deciding the bump is the only option. If you can identify a step that can be replaced with a deterministic synchronization (e.g., wait on a process exit event instead of polling, replace `Thread.Sleep` with a condition variable, await a known signal in the log), do that instead — but only if you are confident the rewrite is correct. If unsure, prefer "bump + telemetry" over a risky rewrite.
- **Record what you tried** in the tracker issue, so the next iteration does not repeat the analysis.

### Telemetry-only PRs

If the failure mode is unclear, open a telemetry-only PR (no behavior change) and explicitly say "this PR does not attempt to fix; it adds diagnostics so the next CI failure is actionable." The issue stays open.

## Parameters

| Parameter        | Default            | Notes                                                   |
|------------------|--------------------|---------------------------------------------------------|
| `-DefinitionId`  | 75                 | dotnet-msbuild-public                                   |
| `-Organization`  | `dnceng-public`    |                                                         |
| `-Project`       | `public`           |                                                         |
| `-Days`          | 7                  | Look-back window                                        |
| `-MaxFailRatio`  | 0.30               | Above this, treat as systematic regression, not flake   |
| `-MinDistinctBranches` | 1            | Set 2 for stricter "must span multiple PRs" triage      |
| `-TopCount`      | 20                 | Cap on the printed table                                |
| `-JsonOutPath`   | (none)             | Optional JSON dump for downstream automation            |
| `-ExcludeTestsFile` | (none)          | Text file of test names to suppress (one per line, `#` comments) |
| `-IssueDraftDir` | (none)             | If set, write one issue-body markdown draft per top flake here |

## Caveats

- AzDO's anonymous APIs do NOT expose test results on `dnceng-public`; auth is required.
- A test that fails only on a single PR's builds is reported but may be a real PR regression — inspect `DistinctBranches`. `DistinctBranches >= 2` is a stronger flake signal.
- Tests that fail and then pass on retry within the same build don't appear in `resultdetailsbybuild` (only the passing result wins). They are reported in the "ghost failures" section, by build, without test names — use the AzDO UI for those.
