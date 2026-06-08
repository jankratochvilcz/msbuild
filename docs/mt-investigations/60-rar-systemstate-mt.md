# Investigation: RAR `SystemState` cache lifetime under MT

Tracks https://github.com/jankratochvilcz/msbuild/issues/60

## Hypothesis

`ResolveAssemblyReference` (RAR) is migrated (`[MSBuildMultiThreadableTask]`) but still
regresses **+95%** on `console-rebuild` (63 → 123 ms median). The suspect is
`Microsoft.Build.Tasks.AssemblyDependency.SystemState` — RAR's per-process cache — whose
lifetime under the MT node-handoff model may not match the per-AppDomain lifetime it was
designed for, causing cache misses across TFM passes.

| scenario | non-MT ms | MT ms | Δ % |
|---|---:|---:|---:|
| console-rebuild | 63 | 123 | **+95%** |
| console-inc-build | 98 | 126 | +29% |
| blazorwasm-inc-build | 89 | 135 | +52% |
| blazorwasm-rebuild | 146 | 128 | -12% (control) |
| eshop-* | ~210 | ~210 | flat |

Working hypotheses:

1. `SystemState` is keyed by file path / timestamps; under MT, node-handoff may invalidate
   per-thread caches downstream (thread-static buffers, cwd-relative paths).
2. RAR state cache file (`<TargetFramework>.csproj.AssemblyReference.cache`) is read once per
   `Build` target invocation. Multiple TFM passes may experience higher cache miss rates
   under MT due to file-handle / FS-watcher state.

## Investigation plan / analysis notes

1. **Code audit** of `Microsoft.Build.Tasks.AssemblyDependency.SystemState`:
   - Thread-affinity assumptions (thread-static fields, `ThreadLocal<T>`).
   - cwd-relative paths inside cache keys.
   - FileSystemWatcher / file-handle state held across calls.
2. **Tracing**: instrument `SystemState.ReadStateFile` / `WriteStateFile` with ETW events for
   cache hit / miss / evict.
3. Run `console-rebuild` MT vs non-MT (10 iters each) and diff cache statistics.
4. Decision tree:
   - MT miss rate >> non-MT miss rate → root cause is cache-key / lifetime mismatch. Fix:
     stabilize cache key (absolute paths, no cwd) and extend lifetime to
     `BuildManager.BeginBuild` → `EndBuild`.
   - Miss rates equal → regression is downstream of cache. Profile `RAR.Execute` body.

## Reproducer

### Kusto query

```kusto
let per_iter = task_runs | where run_key=="14309939"
  | summarize iter_total=sum(duration_ms) by scenario_pair, mt_flag, task_name, iteration;
let agg = per_iter | summarize median_ms=percentile(iter_total,50) by scenario_pair, mt_flag, task_name;
let nm = agg | where mt_flag==false | project sp=scenario_pair, tn=task_name, nonmt_ms=median_ms;
let mt = agg | where mt_flag==true  | project sp=scenario_pair, tn=task_name, mt_ms=median_ms;
nm | join kind=inner mt on sp, tn
| where tn == "ResolveAssemblyReference"
| extend delta_ms = mt_ms-nonmt_ms, delta_pct = 100.0*(mt_ms-nonmt_ms)/nonmt_ms
| project scenario_pair=sp, nonmt_ms, mt_ms, delta_ms, delta_pct
```

### Local bench

```bash
DOTNET=/Users/jan/src/microsoft/msbuild/artifacts/bin/bootstrap/core/dotnet
ASSET=/path/to/net8-console-app
for mt in 0 1; do
  for i in $(seq 0 9); do
    rm -rf "$ASSET/bin" "$ASSET/obj"
    MSBUILDFORCEMULTITHREADED=$mt $DOTNET build -c Release \
      /bl:./bench/rar-mt$mt-i$i.binlog "$ASSET/Net8ConsoleApp.csproj"
  done
done
```

Use the binlog MCP `binlog_expensive_tasks` / `binlog_task_details` to extract the per-call
RAR breakdown and compare cache hits/misses.

## Expected outcome

- Confirmed source of the +95% regression on `console-rebuild`.
- If cache-lifetime issue: a PR that extends `SystemState` lifetime across the MT
  node-handoff, restoring Δ < +20%.
- If downstream: a follow-up issue with the next narrowed hypothesis.
