---
name: process-strict-mode-issues
description: Pick the next top-priority open issue tagged `strict-mode` in jankratochvilcz/msbuild, execute it on the local checkout, push to the fork, run the perf bench, validate no unexpected regressions, and file follow-up issues for any new problems discovered. Use this when the user says "process the next strict-mode issue", "do the next P0/P1", or similar.
---

# Process Strict Mode Issues

This skill drives the end-to-end loop of working through the **Strict Mode** epic (jankratochvilcz/msbuild#2). Per pass it picks one issue, ships it, validates it on the bench, and files follow-ups for anything surfaced along the way.

## Hard rules (read first, do not violate)

- **Push target is `jankratochvilcz/msbuild` only.** That repo is the `origin` remote of `C:\Users\jankratochvl\src\msbuild`. The `upstream` remote is `dotnet/msbuild` — **never push to it, never open PRs against it, never file issues against it.**
- **Working branch is `experimental/strict-mode`.** Always commit on this branch. Pull request #1 in the fork auto-updates when you push.
- **Commit trailer is mandatory** on every commit:
  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```
- **`gh` auth via `GH_TOKEN`.** Authenticate every shell with:
  ```powershell
  $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PATH','User')
  $cred = "protocol=https`nhost=github.com`n" | git credential fill 2>$null
  $env:GH_TOKEN = ($cred | Where-Object { $_ -match '^password=' }) -replace '^password=',''
  ```
- **All bench measurements use the Release bootstrap.** Debug bootstrap measurements are not trusted for headline numbers.

## Layout

| Repo / dir | Purpose |
|---|---|
| `C:\Users\jankratochvl\src\msbuild` | MSBuild source. Branch `experimental/strict-mode` is the working branch. Remotes: `origin = jankratochvilcz/msbuild`, `upstream = dotnet/msbuild`. |
| `C:\Users\jankratochvl\src\msbuild\artifacts\bin\bootstrap-release\core` | Stashed Release bootstrap. Refreshed by step 3 below when you change MSBuild source. |
| `C:\Users\jankratochvl\src\msbuild-perf-bench` | Bench harness + Postgres + Grafana. Read its `AGENTS.md` once per session. |
| `C:\Users\jankratochvl\src\msbuild-perf-bench\bench-stack\` | `docker compose` stack: Postgres `msb-bench-pg` (port 55432) + Grafana `msb-bench-grafana` (port 53000). |

Headline baseline for regression checks: `bench_run.id = 10` (`local-release-strict-t3-robust`, 5 iters, warm-up dropped, robust-summary view exists).

## Step 1 — Pick the next issue

```powershell
$env:GH_TOKEN = ...   # see Hard rules
$issues = gh issue list --repo jankratochvilcz/msbuild --label strict-mode --state open --limit 100 --json number,title,labels,body
$next = $issues | ConvertFrom-Json `
  | Where-Object { -not ($_.labels.name -contains 'epic') } `
  | Where-Object { -not ($_.labels.name -contains 'in-progress') } `
  | ForEach-Object {
      $pri = ($_.labels.name | Where-Object { $_ -match '^P[0-3]$' }) -as [string]
      if (-not $pri) { $pri = 'P9' }
      [pscustomobject]@{ Pri=$pri; Num=$_.number; Title=$_.title; Body=$_.body }
    } `
  | Sort-Object Pri, Num `
  | Select-Object -First 1
"Selected: #$($next.Num) [$($next.Pri)] $($next.Title)"
```

Claim it so a second invocation does not double-work the same issue:

```powershell
gh issue edit $next.Num --repo jankratochvilcz/msbuild --add-label "in-progress"
gh issue comment $next.Num --repo jankratochvilcz/msbuild --body "Picked up by the process-strict-mode-issues skill at $(Get-Date -Format o)."
```

If `$next` is empty: stop. There is no open strict-mode work left — report this and call task_complete.

## Step 2 — Implement the change

Operating mode for issues filed under the Strict Mode epic:

1. Re-read the issue body. Note its **exit criteria checklist** (every Strict Mode child issue has one — see issue template below).
2. Make the change locally on `experimental/strict-mode`. Touch only what the issue requires; do not opportunistically refactor.
3. Add/extend tests under `src/Build.UnitTests/BackEnd/StrictProjectCache_Tests.cs`, `src/Build.UnitTests/BackEnd/StrictTargetCache_Tests.cs`, or create a new `Strict*_Tests.cs` if the area is new.
4. Run the affected test project (xUnit v3 + MTP — see `running-unit-tests` skill):
   ```powershell
   dotnet test src\Build.UnitTests\Microsoft.Build.Engine.UnitTests.csproj -c Release `
     -f net10.0 --filter "FullyQualifiedName~Strict"
   ```
5. Update `documentation/specs/StrictMode.md` if the change is user-visible (opt-in surface, env vars, telemetry).

If the change is **strictly internal** (refactor, comment, test-only) and has no measurable bench effect, skip steps 3–5 of the bench section and document why in the PR comment.

## Step 3 — Refresh the Release bootstrap

The bench is meaningless against stale binaries. Before benching:

```powershell
cd C:\Users\jankratochvl\src\msbuild

# Wipe the in-tree bootstrap so Release re-publishes cleanly (it is otherwise config-agnostic).
Remove-Item -Recurse -Force artifacts\bin\bootstrap\core -ErrorAction SilentlyContinue
.\build.cmd -configuration Release    # ~2–3 minutes

# Stash to bootstrap-release so any subsequent debug build doesn't clobber it.
Remove-Item -Recurse -Force artifacts\bin\bootstrap-release\core -ErrorAction SilentlyContinue
Copy-Item -Recurse artifacts\bin\bootstrap\core artifacts\bin\bootstrap-release\core
```

Verify Release-ness (one-liner that should print `Release`, `False`, `False`):

```powershell
$asm = [Reflection.Assembly]::LoadFile((Resolve-Path artifacts\bin\bootstrap-release\core\sdk\10.0.300\Microsoft.Build.dll))
$cfg = $asm.GetCustomAttributes([System.Reflection.AssemblyConfigurationAttribute], $false)[0].Configuration
$dbg = $asm.GetCustomAttributes([System.Diagnostics.DebuggableAttribute],          $false)[0]
"$cfg / IsJITOptimizerDisabled=$($dbg.IsJITOptimizerDisabled) / IsJITTrackingEnabled=$($dbg.IsJITTrackingEnabled)"
```

## Step 4 — Run the bench

```powershell
cd C:\Users\jankratochvl\src\msbuild-perf-bench
$issueNum  = $next.Num
$shortDesc = ($next.Title -replace '^Strict mode: ','' -replace '[^a-zA-Z0-9]+','-').ToLower().Trim('-')
$label     = "issue-$issueNum-$shortDesc".Substring(0,[Math]::Min(64,"issue-$issueNum-$shortDesc".Length))
$notes     = "Issue jankratochvilcz/msbuild#$issueNum: $($next.Title). Release bootstrap built via .\build.cmd -configuration Release."

dotnet run --project tools\BenchRunner -c Release -- `
  --msbuild-path local-release `
  --msbuild-label $label `
  --tag "strict-mode-issue-$issueNum" `
  --notes $notes `
  --runs 5
```

Capture the new `bench_run.id` printed in the script header (look for `bench_run.id = <N>`).

## Step 5 — Validate regressions

Use the robust-summary view (warm-up dropped, median + CV). Compare every (scenario × strict) cell against baseline run 10 and against the issue's **declared bench expectations** (mandatory section in every Strict Mode child issue — see template below).

```powershell
$newRun = <fill-in>      # from step 4
$base   = 10             # baseline; bump as new robust baselines land
$q = @"
WITH base AS (
  SELECT scenario, strict_mode_on, median_seconds AS base_median
  FROM v_bench_robust_summary WHERE run_id = $base),
cand AS (
  SELECT scenario, strict_mode_on, median_seconds AS cand_median, coefficient_of_variation AS cand_cv
  FROM v_bench_robust_summary WHERE run_id = $newRun)
SELECT c.scenario,
       c.strict_mode_on,
       ROUND(b.base_median::numeric,2)                              AS base_median_seconds,
       ROUND(c.cand_median::numeric,2)                              AS cand_median_seconds,
       ROUND(((c.cand_median - b.base_median) / b.base_median * 100)::numeric, 1) AS pct_delta,
       ROUND((c.cand_cv * 100)::numeric, 1)                         AS cand_cv_percent
FROM cand c LEFT JOIN base b USING (scenario, strict_mode_on)
ORDER BY c.scenario, c.strict_mode_on;
"@
docker exec -i msb-bench-pg psql -U bench -d bench -c $q
```

**Regression policy (default; an issue may relax it explicitly):**

- A scenario regressed if `pct_delta > +10%` AND `cand_cv_percent < 15%` (i.e., not noise).
- `noop` and `touch-leaf` strict-on are flagship scenarios — regression threshold there is **+5%**.
- A regression that is **not** allow-listed in the issue body's bench-expectations section is a **stop**. Do not push. Fix or back out.

## Step 6 — Push and update the PR

Only after step 5 passes:

```powershell
cd C:\Users\jankratochvl\src\msbuild
git add -A
$body = @"
<one-line summary>

Closes jankratochvilcz/msbuild#$issueNum.

Bench (vs run $base):
<paste the table from step 5>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
"@
$body | Out-File -Encoding utf8 -NoNewline $env:TEMP\commit-msg.txt
git commit -F $env:TEMP\commit-msg.txt
git push origin experimental/strict-mode    # MUST be origin, NEVER upstream
```

Then comment on the issue with the bench table + commit SHA, and close it:

```powershell
$sha = git rev-parse HEAD
$cmt = @"
Implemented in $sha (PR #1).

Bench (run $newRun vs baseline $base):

\`\`\`
<paste table>
\`\`\`

Exit criteria: <one bullet per checked-off item>.
"@
$tmp = New-TemporaryFile; Set-Content -NoNewline -Encoding utf8 $tmp $cmt
gh issue comment $issueNum --repo jankratochvilcz/msbuild --body-file $tmp
gh issue close   $issueNum --repo jankratochvilcz/msbuild --reason completed
gh issue edit    $issueNum --repo jankratochvilcz/msbuild --remove-label "in-progress"
Remove-Item $tmp
```

### Move the card to **Done** on the Strict Mode project board

The "Strict Mode" project (org `jankratochvilcz`, project number `1`,
id `PVT_kwHOACGbd84BYLZ0`) tracks every strict-mode issue on a kanban board.
Closing the issue does **not** auto-move the card — you must move it explicitly.
Do this for **every** issue in the same shell as the close, immediately after:

```powershell
# Status field + option ids (stable; cache them in the skill):
#   Status field id        = PVTSSF_lAHOACGbd84BYLZ0zhTTChc
#   Backlog                = cc8b62ac
#   Ready                  = 1d58f06d
#   In Progress            = f9b11ec8
#   In Review              = 0035d23e
#   Blocked                = c5259013
#   Done                   = 340c2f00
$itemId = (gh project item-list 1 --owner jankratochvilcz --limit 200 --format json `
  | ConvertFrom-Json).items | Where-Object { $_.content.number -eq $issueNum } | Select-Object -ExpandProperty id
gh project item-edit --id $itemId `
  --project-id PVT_kwHOACGbd84BYLZ0 `
  --field-id PVTSSF_lAHOACGbd84BYLZ0zhTTChc `
  --single-select-option-id 340c2f00
```

Same idiom moves a card to **In Progress** (`f9b11ec8`) when you claim it in
Step 1, or to **Blocked** (`c5259013`) when you bail out per Step 5. Keep the
board and the `in-progress` label in sync — the label is the lock against
double-work, the board column is the human-readable status.

## Step 7 — File follow-up issues for anything surfaced

If during the work you discover problems out of scope of the current issue (a deeper bug, a missing test class, a brittle assumption in adjacent code, a missing bench scenario), file each as a **new** strict-mode issue using the template below. Do not pile them into the current issue's resolution.

### Strict Mode child-issue template

Every new strict-mode issue must use this body. Pasting this verbatim and filling the angle brackets is the contract.

```markdown
## Problem
<one-paragraph statement of what is wrong / missing>

## Current state
<file:line citations and a brief code excerpt; what does the code actually do today>

## Suggested approach
<step-by-step plan; reject options considered but not taken with one-line rationale>

## Scope (in / out)
- IN: <bullet list>
- OUT: <bullet list>

## Bench expectations
<per-scenario expected delta vs baseline run 10>

| scenario | strict | expected median delta vs run 10 | rationale |
|---|---|---|---|
| noop | on | within ±5% | this change does not touch the fast-skip path |
| touch-leaf | on | within ±5% | ditto |
| touch-root | on | within ±5% | ditto |
| cold-clean | on | within ±10% | cache-population path may shift slightly |
| edit-package-ref | both | within ±5% | restore-bound; no relation |

(If the change is expected to *improve* a scenario, declare the minimum required improvement here, not just "any improvement". The skill enforces this on close.)

## Exit criteria
- [ ] Implementation lands on `experimental/strict-mode` and pushes to `jankratochvilcz/msbuild` (PR #1 auto-updates)
- [ ] Unit tests added/extended; relevant `Strict*_Tests.cs` projects pass under `-c Release`
- [ ] If user-visible: `documentation/specs/StrictMode.md` updated
- [ ] Bench run executed against the Release bootstrap with `-Runs 5`; new `bench_run.id` recorded in PR comment
- [ ] `v_bench_robust_summary` deltas against baseline run 10 fall within the declared bench expectations above
- [ ] Grafana `MSBuild Perf — Run Comparison` dashboard renders the new run cleanly (no NULLs, no missing series)
- [ ] Self-review pass completed (see below)
- [ ] No new bench scenarios were needed (or, if they were: one was added and linked in the comment trail)

## Self-review (run BEFORE marking the issue closed)
- [ ] Walked the diff line-by-line for unintended changes
- [ ] Checked that the change is gated by the strict-mode opt-in (no behavior change when off)
- [ ] Verified no regressions in the strict-OFF code path on the bench
- [ ] Reviewed error/log output: an MSBuild user could understand what happened
- [ ] Re-read the issue Problem statement; the change actually fixes it (not an adjacent issue)
- [ ] Filed follow-up issues for any out-of-scope problems noticed during the work
```

### Filing the follow-up

```powershell
$tmp = New-TemporaryFile
Set-Content -NoNewline -Encoding utf8 $tmp $body
gh issue create --repo jankratochvilcz/msbuild `
  --title "Strict mode: <short imperative>" `
  --body-file $tmp `
  --label "strict-mode,<area>,<P0|P1|P2|P3>"
Remove-Item $tmp
```

Then **link the new issue to the epic** (issue #2) by editing the epic body's task list. Use:

```powershell
gh issue view 2 --repo jankratochvilcz/msbuild --json body --jq .body > $env:TEMP\epic-body.md
# Edit the file to add a new "- [ ] #<N> — <title>" line under the right priority section, then:
gh issue edit 2 --repo jankratochvilcz/msbuild --body-file $env:TEMP\epic-body.md
```

## Step 8 — When to amend bench scenarios

The fixed scenario set today is: `cold-clean`, `cold-cached`, `noop`, `touch-leaf`, `touch-root`, `edit-package-ref`. If the issue you are working on cannot be validated by any of these, you must add a scenario. Concrete triggers:

| Trigger | New scenario to add |
|---|---|
| Issue about **env-var cache-key fidelity** | `edit-env-var` — flips a `<Setting>$(MyEnv)</Setting>` consumer; expected ≥ 10× speedup once fix lands |
| Issue about **MSBuild-property cache-key fidelity** | `edit-property` — flips a `<PropertyGroup>` value via `-p:Foo=Bar`; same expectation |
| Issue about **item-metadata cache-key fidelity** | `edit-item-metadata` — flips a `<Compile Foo="bar"/>` metadata value |
| Issue about **transitive dependency invalidation** | `edit-transitive` — edits a file in a project two hops away from `Hosts.Web` |
| Issue about **multi-target inner/outer staleness** | `edit-tfm-inner` — edits inside `Modules.Blog` while it targets `net8.0;net10.0` |
| Issue about **multi-proc node behavior** | `noop-parallel` — `-maxcpucount:8` baseline-vs-strict comparison |

How to add one:

1. Drop a JSON file in `C:\Users\jankratochvl\src\msbuild-perf-bench\scenarios\` following the schema in `scenarios\_schema.v1.json`. Use one of the existing files (e.g. `20-noop.json`, `30-touch-leaf.json`) as a template. Closed prep vocabulary: `clean-all`, `clean-bin-only`, `noop`, `touch-file`, `toggle-marker`, `exec`. Known path placeholders: `${workload}`, `${workload-dir}`, `${first-csproj}`, `${leaf-csproj-source}`, `${root-csproj-source}`.
2. Validate: `dotnet run --project tools\BenchRunner -- --validate`. Fix any errors it reports before continuing.
3. The Grafana scenario-reference panel now JOINs `bench_scenario`, so descriptions surface automatically the first time you run the bench (no dashboard JSON edit, no `docker restart` needed).
4. Mention the new scenario by name in the current issue's resolution comment and the bench-expectations table of any future related issue.

## Failure modes

| Symptom | What to do |
|---|---|
| Bench shows a regression on a non-allow-listed scenario | Do not push. Investigate. Either fix or revert; if the change is correct but exposes a real regression, file a P0 follow-up and STOP. |
| `bench-critique.md` numbers diverge from a fresh run by > 1 standard deviation | Re-run the bench with `-Runs 10`. If still divergent, suspect machine load. Document and continue only if the headline ordering is unchanged. |
| Push to `origin` fails with "permission denied" | You are not authenticated. Re-run the `GH_TOKEN` snippet. **Never `git push upstream`** as a workaround. |
| `gh` reports "issues are disabled" | `gh repo edit jankratochvilcz/<repo> --enable-issues=true`. |
| The bench errors before iter 0 | Check Postgres is up: `docker ps`. Start it: `cd msbuild-perf-bench\bench-stack; docker compose up -d`. |
| `dotnet run --project tools\BenchRunner -- --validate` reports a schema error | A scenario JSON is malformed or uses an unknown placeholder/action. The error message names the file and the issue. Fix the JSON; re-run `--validate`; commit. |
| Telemetry rows missing from `v_cache_hit_rate` | The change broke `MSBUILDSTRICTTELEMETRYFILE` emission. Block the issue's close; file a P0. |

## Bookkeeping at the end of every run

After step 6 (or step 7 if you only filed follow-ups):

1. Print a one-line summary: `Closed #<N>, opened follow-ups: #<M>, #<P>; bench run <newRun> all within tolerance`.
2. Make sure the `in-progress` label is removed.
3. Make sure the epic (#2) task list reflects the newly-closed item (strike-through is automatic when the issue is closed; new items must be added manually).
4. If you added a new bench scenario, the new scenario's JSON file in `msbuild-perf-bench/scenarios/` IS the inventory entry; no extra `AGENTS.md` edit is required. If the scenario warrants an expected-speedup band, set `expectations.expectedSpeedupStrictOnVsOffMin/Max` in the JSON and the runner's post-bench validation report will start enforcing it.

## When not to use this skill

- The user wants to **review** a strict-mode issue, not implement it. Read the issue and respond; don't pick up the `in-progress` label.
- The user wants to land a change in the **upstream** dotnet/msbuild repo. This skill is fork-only.
- The bench harness itself is broken (`dotnet run --project tools\BenchRunner` crashes before iter 0, or `--validate` fails for a clean JSON). Repair the harness first via `msbuild-perf-bench` repo.
