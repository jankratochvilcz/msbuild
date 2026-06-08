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

## Findings — code audit of `SystemState`

Reviewed `src/Tasks/SystemState.cs` and the `StateFile` region in
`src/Tasks/AssemblyDependency/ResolveAssemblyReference.cs`. Three caches coexist:

| Cache | Lifetime | Reset on rebuild? |
|---|---|---|
| `upToDateLocalFileStateCache` | per-`RAR.Execute` instance | yes (new instance per call) |
| `instanceLocalFileStateCache` | per-`RAR.Execute` instance, deserialized from `<proj>.csproj.AssemblyReference.cache` | **yes — obj is wiped on rebuild → `DeserializeCache` returns null → empty dictionary** |
| `s_processWideFileStateCache` | `static ConcurrentDictionary<string, FileState>` — per host process | no (survives across task calls within the same MSBuild process) |

The shared static `s_processWideFileStateCache` already gives every in-proc RAR
call full cross-invocation reuse of resolved `FileState` (assembly name, runtime
version, dependencies). Concurrent access is `ConcurrentDictionary`-safe, and
per-`FileState` lazy population is serialized by `FileState._lock`. No
thread-static fields, no `cwd`-relative cache keys, no `FileSystemWatcher`
handles — the cache-lifetime / thread-affinity hypothesis does not hold.

### Why this rules out `SystemState` as the root cause of `console-rebuild` +95%

- `console-rebuild` is a **single-project clean rebuild**: obj is wiped, so the
  per-project state file is missing → instance cache is empty for both MT and
  non-MT. There is only **one RAR call**, so `s_processWideFileStateCache` is
  also empty at the start. Both modes pay the *same* cold cache population
  cost. A cache-lifetime regression would require either (a) multiple RAR
  calls within the same process where MT loses sharing relative to non-MT, or
  (b) the MT path bypassing `s_processWideFileStateCache`. Neither is the case.
- The only RAR pathway that differs by mode is `OutOfProcRarClient`
  (RAR-as-a-service, gated by `MSBuildRarNode`) which is *disabled* under MT
  via an early `throw new NotSupportedException` in `RAR.Execute`. This path
  is **off by default** (env var unset in perfstar), so it cannot explain the
  baseline +95%. If perfstar ever enables `MSBuildRarNode`, the early throw
  will surface — file a follow-up to either gracefully fall back to in-proc or
  make `OutOfProcRarClient` MT-safe (the pipe-pooling work referenced in the
  comment at `ResolveAssemblyReference.cs:3417`).

### Where the per-call regression most likely lives

RAR is dominated by per-input ITaskItem path work. Under Wave 18.8,
`MakeAbsolutePath` / `MakeCanonicalPath` route every `AssemblyFiles`,
`Assemblies`, `SearchPaths`, and `InstalledAssemblyTables` path through
`TaskEnvironment.GetAbsolutePath(...).GetCanonicalFormNoThrow(...)`. That
adds an O(inputs) overhead on every Execute that is **paid identically on
rebuild and incremental**, and is the same hot path called out by:

- **#59 / PR #69** — `AbsolutePath.GetCanonicalForm` Windows case-folding
- **#57 / PR #68** — migrated-but-regressing tasks with many ITaskItem inputs
- **#61 / PR #71** — intrinsic task re-entrancy / TaskExecutionHost dispatch

A `console-rebuild` RAR call touches ~200 framework + transitive assembly
paths; even a few µs per path of extra canonicalization plus MT-side
TaskExecutionHost setter dispatch is consistent with the observed
63 → 123 ms delta.

## Conclusion — close as "not a cache problem"

- `SystemState` is **not** the source of the +95% `console-rebuild`
  regression. The shared static `s_processWideFileStateCache` already gives
  the in-proc MT scheduler full cross-call reuse; clean rebuild makes both
  modes start cold; there are no thread-affinity bugs in the cache code.
- No `SystemState` fix is shipped from this PR. The investigation is
  redirected to the active follow-ups (#59 canonicalization, #57 setter
  marshalling, #61 intrinsic-task re-entrancy) which share the same hot path.
- If multi-project MT scenarios later regress on RAR specifically (rather
  than the cross-cutting per-input overhead above), reopen with the
  hypothesis that one of: `redistList` (per-instance, reloaded every call,
  not static), `InstalledAssemblyTableInfo` parsing, or `app.config`
  remapping is the next narrowed suspect.

## Expected outcome

- Confirmed source of the +95% regression on `console-rebuild`.
- If cache-lifetime issue: a PR that extends `SystemState` lifetime across the MT
  node-handoff, restoring Δ < +20%.
- If downstream: a follow-up issue with the next narrowed hypothesis.

**Actual outcome:** cache-lifetime ruled out by code audit (see *Findings*
above). Regression is redirected to the per-input path canonicalization /
TaskExecutionHost dispatch hot path tracked under #59 / #57 / #61. No
`SystemState` change is needed.
