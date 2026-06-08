# Investigation: `AbsolutePath.GetCanonicalForm` Windows case-folding hot path

Tracks https://github.com/jankratochvilcz/msbuild/issues/59

## Hypothesis

Orchard-core Windows rebuild evaluation pass5 goes from 22 938 ms (non-MT) to 176 973 ms
(MT) — **+154 035 ms, +672%**. Linux orchard pass5 is essentially flat (+0%). The
Windows-only signal points at `AbsolutePath.GetCanonicalForm()` which on Windows hits the
filesystem (`GetLongPathName` / `NtQueryDirectoryFile`) for case-correction.

| scenario | os | pass | non-MT ms | MT ms | Δ % |
|---|---|---|---:|---:|---:|
| orchard-core-rebuild | Windows | pass5 | 22 938 | 176 973 | **+672%** |
| orchard-core-inc-build | Windows | pass5 | 25 482 | 84 932 | +233% |
| orchard-core-inc-build | Windows | pass1 | 30 977 | 97 108 | +214% |
| orchard-core-rebuild | Linux | pass5 | 30 353 | 30 488 | +0% |

Glob expansion in pass3/3.1 and lazy item evaluation in pass5 hit this path thousands of
times per project.

Fix candidates:

1. **Cache `GetCanonicalForm`** results by raw input string in a per-build
   `ConcurrentDictionary<string, AbsolutePath>`.
2. **Short-circuit single-threaded callers** — if the current task chain is on the main
   evaluator thread, skip canonicalization.
3. **Lazy canonicalization** — only canonicalize when consumed as a `string`.

## Investigation plan

1. Capture `dotnet-trace collect --profile cpu-sampling` of orchard-core-rebuild Windows MT
   and flame-chart top frames inside the evaluator.
2. Confirm `AbsolutePath.GetCanonicalForm` / `GetLongPathName` / `Path.GetFullPath` are
   top-N frames.
3. Prototype a per-build cache:
   - Branch: `mt/fix-abspath-canonicalform-cache`.
   - Lifetime = single `BuildManager.BeginBuild` → `EndBuild`.
4. Re-run orchard-core MT bench (10 iters per OS). Expect pass5 Windows to drop > 50 s.
5. Verify Linux pass5 stays flat (cache should be a no-op there).

## Reproducer

### Kusto query

```kusto
let m = scenario_metrics
| where metric_name startswith 'evaluation-time-pass'
| summarize p50 = percentile(value, 50) by scenario_pair, os, mt_flag, metric_name;
let mt = m | where mt_flag==true  | project scenario_pair, os, metric_name, mt=p50;
let nm = m | where mt_flag==false | project scenario_pair, os, metric_name, nonmt=p50;
mt | join kind=inner nm on scenario_pair, os, metric_name
| where scenario_pair startswith "orchard-core"
| extend delta = mt - nonmt, pct = round(100.0*(mt-nonmt)/nonmt,1)
| order by abs(delta) desc
```

### Bench harness (Windows)

```powershell
cd C:\src\msbuild
.\build.cmd -c Release /p:CreateBootstrap=true
$Bootstrap = "$pwd\artifacts\bin\bootstrap\core\dotnet.exe"
$Asset = "C:\assets\orchard-core"

foreach ($i in 0..9) {
  Get-ChildItem "$Asset\src" -Recurse -Directory -Include bin,obj |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  $env:MSBUILDFORCEMULTITHREADED = "1"
  & $Bootstrap build -c Release /bl:"bench\orchard-mt-i$i.binlog" "$Asset\OrchardCore.sln"
}
```

Use `dotnet-trace collect --profile cpu-sampling --process-id <pid>` on iteration 5 and
inspect frames containing `GetCanonicalForm`, `GetLongPathName`, `GetFullPath`.

## Expected outcome

- A confirmed flamegraph showing `GetCanonicalForm` ≥ 20% of evaluation CPU on Windows MT.
- A prototype cache that reduces orchard-core-rebuild Windows pass5 from ~177 s to < 60 s.
- Linux orchard-core unaffected (control).
