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

## Findings

Code inspection of the intrinsic `<MSBuild>` task callback path identifies a
serialization bottleneck that is consistent with the observed +56% regression on
`eshop-inc-build` and the solution-graph-size-dependent per-call delta.

### The hot path

The intrinsic `MSBuild` task (`src/Build/BackEnd/Components/RequestBuilder/IntrinsicTasks/MSBuild.cs`)
dispatches nested project builds through the `IBuildEngine` callback surface
exposed by `TaskHost` (`src/Build/BackEnd/Components/RequestBuilder/TaskHost.cs`).
Every nested-build entry point in `TaskHost` is wrapped in
`lock (_callbackMonitor)` — a single per-task-host monitor.

The relevant call (`TaskHost.cs:335`):

```csharp
public BuildEngineResult BuildProjectFilesInParallel(
    string[] projectFileNames, string[] targetNames,
    IDictionary[] globalProperties, IList<string>[] undefineProperties,
    string[] toolsVersion, bool returnTargetOutputs)
{
    lock (_callbackMonitor)
    {
        return BuildProjectFilesInParallelAsync(
            projectFileNames, targetNames, globalProperties,
            undefineProperties, toolsVersion, returnTargetOutputs).Result;
    }
}
```

Two compounding problems:

1. **The lock is held for the entire duration of the child build.** The
   synchronous `.Result` blocks inside the critical section, so the monitor is
   pinned from request dispatch through scheduler round-trip, child-node
   execution, and result marshalling.
2. **The `await` continuations of `BuildProjectFilesInParallelAsync` can land on
   a different thread** that then re-enters the same `TaskHost` (e.g. logging,
   `LogMessageEvent`, `ContinueWhenAll`, target-result callbacks — every public
   member of `TaskHost` takes `_callbackMonitor`). Combined with the blocking
   `.Result`, this turns the "parallel" entry point into a strictly serialized
   dispatcher per task host.

### Why eshop is hit hardest

`eshop-inc-build` issues ~16 `MSBuild`-task invocations during a single
incremental build, most of them recursive cross-project dispatches that fan out
across the solution graph. Under MT the scheduler genuinely has multiple
in-process worker threads available — but every nested `<MSBuild>` invocation
that touches the same parent task host must serialize at `_callbackMonitor`
before its child build can even be queued. The extra worker threads idle while
they wait their turn at the lock.

This matches the empirical shape of the regression:

- per-call delta scales with solution size: eshop ~226 ms/call vs.
  blazorwasm/console ~25 ms/call;
- the regression is concentrated in the intrinsic `MSBuild` task while siblings
  (`Csc`, `ResolveAssemblyReference`) are flat under MT;
- `eshop-rebuild` (fewer cache hits, longer child builds) shows a smaller
  *percentage* delta but the same absolute pattern, because each lock holder
  occupies the monitor for longer.

Non-MT does not pay this cost: with a single worker the lock is uncontended,
and `BuildProjectFilesInParallelAsync(...).Result` collapses to ordinary
synchronous dispatch.

### Relationship to the E1 experiment

E1 removed the **log-event** lock (`LogMessageEvent` / `LogWarningEvent` /
`LogErrorEvent` paths through `_callbackMonitor`). This investigation concerns
a **different code path** that happens to share the same monitor:
`BuildProjectFilesInParallel`. The E1 change does nothing for nested-build
dispatch — the `.Result` inside the lock is independent. Both fixes are needed.

### Proposed fix shape (any one of, in increasing order of intrusiveness)

1. **Drop the lock from `BuildProjectFilesInParallel` and rely on a
   genuinely-async pipeline.** Expose an async entry point on `IBuildEngine9`
   (or thread it through the existing `ContinueWhenAll` mechanism) so the
   intrinsic `MSBuild` task can `await` the child build instead of blocking on
   `.Result` inside a monitor. This is the correct long-term fix and lines up
   with the existing private `BuildProjectFilesInParallelAsync` signature.
2. **Replace the monitor with a `SemaphoreSlim` sized to the node count** for
   the nested-build path only. Keep the monitor for state-mutating callbacks
   (log routing, `Yield`/`Reacquire`, `RequestCores` bookkeeping) but allow up
   to N concurrent in-flight child dispatches per task host. Minimal change,
   recovers most of the parallelism without redesigning the callback API.
3. **Split `_callbackMonitor` into per-concern locks.** Read-only / dispatch
   callbacks (`BuildProjectFilesInParallel`, `ContinueWhenAll` setup, project
   metadata queries) get their own monitor — or no monitor — while
   state-mutating callbacks keep the existing one. Lowest blast radius but
   leaves the `.Result`-under-lock anti-pattern in place.

The fix should be validated by re-running `eshop-inc-build` MT and confirming
that the intrinsic `MSBuild` task delta drops back toward the
blazorwasm/console per-call cost (~25 ms/call), which represents the residual
non-lock-bound MT overhead.
