# Investigation: MSBuild intrinsic task re-entrancy cost under MT

Tracks https://github.com/jankratochvilcz/msbuild/issues/61

## Hypothesis

The built-in `MSBuild` intrinsic task (nested project builds: `Restore`,
`GenerateRestoreGraph`, recursive `Build`) regresses **+56%** on `eshop-inc-build`
(6 477 → 10 093 ms, **+3 616 ms**) and +32% on `eshop-rebuild`. It also regresses +134% on
`console-rebuild`. The engine itself cannot be "migrated" — the cost has to be diagnosed in
`BuildManager` / `MSBuildTask` re-entrancy.

| scenario | non-MT ms | MT ms | Δ ms | Δ % | invocations | Δ / call ms |
|---|---:|---:|---:|---:|---:|---:|
| eshop-inc-build | 6 477 | 10 093 | **+3 616** | +56% | 16 | +226 |
| eshop-rebuild | 8 684 | 11 453 | +2 769 | +32% | 16 | +173 |
| console-rebuild | 359 | 840 | +481 | +134% | 14 | +34 |
| blazorwasm-inc-build | 516 | 816 | +300 | +58% | 14 | +21 |

The eshop per-call delta (~200 ms) is an order of magnitude larger than blazorwasm/console
(~25 ms) — pointing at solution-graph-size-dependent cost.

Working hypotheses:

1. **`BuildManager.BuildRequest` re-entrancy under MT**: per-request initialization that
   non-MT amortizes.
2. **`ProjectInstance` cloning**: `MSBuildTask` creates per-call snapshots; under MT the
   snapshot may not be cached across calls.
3. Part of the residual auto-resolves as nested-task regressions (StaticWebAssets, NuGet)
   shrink — the intrinsic task is the *sum* of nested builds.

## Investigation plan / analysis notes

1. Capture `dotnet-trace collect --providers Microsoft-Build,Microsoft-Windows-DotNETRuntime`
   on `eshop-inc-build` MT. Isolate intrinsic `MSBuild` task frames.
2. Diff against non-MT trace to find MT-only frames (likely `BuildManager`, `Scheduler`,
   `NodeManager`, `OutOfProcNode`).
3. **Decompose the per-call delta**:
   - Time in `BuildManager.PendBuildRequest`.
   - Time in `Scheduler.AssignNewRequests`.
   - Time in `ProjectInstance.DeepCopy`.
4. Quantify residual after #57, #59, #60 land. Expect Δ to drop proportionally to
   nested-task improvements.
5. If residual remains > +500 ms on eshop-inc-build: implement scheduler /
   `ProjectInstance` cache fix.

## Reproducer

### Kusto query

```kusto
let per_iter = task_runs | where run_key=="14309939"
  | summarize iter_total=sum(duration_ms), inv=count() by scenario_pair, mt_flag, task_name, iteration;
let agg = per_iter | summarize median_ms=percentile(iter_total,50), median_inv=percentile(inv,50) by scenario_pair, mt_flag, task_name;
let nm = agg | where mt_flag==false | project sp=scenario_pair, tn=task_name, nonmt_ms=median_ms, nonmt_inv=median_inv;
let mt = agg | where mt_flag==true  | project sp=scenario_pair, tn=task_name, mt_ms=median_ms, mt_inv=median_inv;
nm | join kind=inner mt on sp, tn
| where tn == "MSBuild"
| extend delta_ms = mt_ms-nonmt_ms, delta_pct = 100.0*(mt_ms-nonmt_ms)/nonmt_ms,
         per_call_delta = round((mt_ms-nonmt_ms)/mt_inv, 1)
| project scenario_pair=sp, nonmt_ms, mt_ms, delta_ms, delta_pct, mt_inv, per_call_delta
```

### Local bench

```bash
DOTNET=/Users/jan/src/microsoft/msbuild/artifacts/bin/bootstrap/core/dotnet
ASSET=/path/to/eshop
for mt in 0 1; do
  for i in $(seq 0 9); do
    find "$ASSET" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
    MSBUILDFORCEMULTITHREADED=$mt $DOTNET build -c Release \
      /bl:./bench/intrinsic-mt$mt-i$i.binlog "$ASSET/eShopOnWeb.sln"
  done
done
```

Use the binlog MCP `binlog_tasks_in_target` for `Restore` and recursive `Build`, then
`binlog_task_details` to inspect `MSBuild` task parameters and durations.

## Expected outcome

- A trace diff that isolates the MT-only frames responsible for the per-call delta.
- If engine-side: a fix or a written hand-off to the engine team.
- If propagation of nested-task regressions: a residual estimate after #57–#60 land.
