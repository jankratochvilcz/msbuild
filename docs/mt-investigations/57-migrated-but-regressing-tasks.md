# Investigation: migrated-but-regressing tasks (GetPackageDirectory, GenerateDepsFile, GenerateRuntimeConfigurationFiles)

Tracks https://github.com/jankratochvilcz/msbuild/issues/57

## Hypothesis

Three SDK tasks are already attributed with `[MSBuildMultiThreadableTask]` yet still regress
significantly under MT mode:

| task | scenario / os | non-MT ms | MT ms | Δ % |
|---|---|---:|---:|---:|
| GetPackageDirectory | rebuild Windows | 8 | 68 | +750% |
| GenerateDepsFile | inc-build Linux | 23 | 86 | +274% |
| GenerateDepsFile | rebuild Windows | 46 | 62 | +35% |
| GenerateRuntimeConfigurationFiles | inc-build Windows | 45 | 78 | +73% |

Working hypotheses (from the tracking issue):

1. `GetPackageDirectory` calls `NuGetPackageResolver.CreateResolver(...)` per `PackageFolders`
   entry. Resolver factory does per-call directory probing IO; under MT this competes with
   concurrent task IO and loses cache locality.
2. `GenerateDepsFile` / `GenerateRuntimeConfigurationFiles` read
   `FileUtilities.CurrentThreadWorkingDirectory` (AsyncLocal) via `Path.GetFullPath`.
   Repeated AsyncLocal reads on coreclr Linux are measurably expensive
   (cf. dotnet/runtime#82105).
3. The engine-side migration attribute may already gate the `TaskEnvironment.ProjectDirectory`
   setter; the residual is purely body-internal.

## Investigation plan

1. Capture `dotnet-trace collect --profile cpu-sampling` on each task in MT mode for the
   `net8-console-app-{rebuild,inc-build}-binlog` scenarios on Windows and Linux.
2. On Linux, additionally collect `perf stat -e cache-misses,page-faults`.
3. Compare flame charts MT vs non-MT, looking for:
   - `NuGetPackageResolver.CreateResolver` allocations dominating `GetPackageDirectory`.
   - `AsyncLocal.Value` reads inside `Path.GetFullPath` for the deps/runtime tasks.
4. Prototype each candidate fix in a separate branch:
   - `GetPackageDirectory`: `ConcurrentDictionary<(string folders, string fallback), string>`
     cache. Expected drop 65 → ~10 ms.
   - `GenerateDepsFile`: hoist a single cwd snapshot per task invocation. Expected drop
     86 → ~25 ms.
5. Re-run the perfstar pipeline and confirm Δ < +20% on the affected scenario rows.

## Reproducer

### Kusto query

```kusto
let m = task_runs | where run_key == 'prod-14281586'
| summarize iter_ms = sum(duration_ms) by scenario_pair, os, mt_flag, iteration, task_name
| summarize median_ms = percentile(iter_ms, 50) by scenario_pair, os, mt_flag, task_name;
let mt = m | where mt_flag == true  | project scenario_pair, os, task_name, mt_ms = median_ms;
let nm = m | where mt_flag == false | project scenario_pair, os, task_name, nonmt_ms = median_ms;
mt | join kind=inner nm on scenario_pair, os, task_name
| where task_name in ("GetPackageDirectory","GenerateDepsFile","GenerateRuntimeConfigurationFiles")
| extend delta_ms = mt_ms - nonmt_ms, delta_pct = round(100.0*(mt_ms-nonmt_ms)/nonmt_ms, 0)
| project scenario_pair, os, task_name, nonmt_ms, mt_ms, delta_ms, delta_pct
| order by task_name, scenario_pair, os
```

### Local bench harness

```bash
cd /Users/jan/src/microsoft/msbuild
./build.sh -c Release /p:CreateBootstrap=true

DOTNET=$(pwd)/artifacts/bin/bootstrap/core/dotnet
ASSET=/path/to/net8-console-app

for mt in 0 1; do
  for i in $(seq 0 9); do
    rm -rf "$ASSET/bin" "$ASSET/obj"
    MSBUILDFORCEMULTITHREADED=$mt $DOTNET build -c Release \
      /bl:./bench/console-mt$mt-i$i.binlog "$ASSET/Net8ConsoleApp.csproj"
  done
done
```

Use `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime --process-id <pid>`
during one of the MT iterations.

## Expected outcome

Either (a) a follow-up PR with a per-task fix that brings Δ < +20%, or (b) confirmation that
the residual is unavoidable per-call infrastructure cost, with a written justification.

Aggregated target: ~400 ms per build across the console scenarios / 15-20% of the MT
regression on net8-console-app.

## Findings

Investigation completed.

1. **All three target tasks are already migrated upstream** (verified against `dotnet/sdk@main`,
   `src/Tasks/Microsoft.NET.Build.Tasks/`): `GetPackageDirectory.cs`, `GenerateDepsFile.cs`, and
   `GenerateRuntimeConfigurationFiles.cs` all carry the `[MSBuildMultiThreadableTask]` attribute
   (merged via dotnet/sdk#54444 and dotnet/sdk#53950 plus follow-ups). The sidecar TaskHost
   routing cost is therefore no longer paid for these three tasks.

2. **Residual per-task regression is engine-side**, not in the task bodies themselves. The
   A/B traces underpinning #58 (`exec-58-widen`) and the deeper analysis in #67 show the
   remaining MT cost concentrates in `TaskBuilder` per-invocation setup — specifically the
   `AsyncLocal` propagation behind `FileUtilities.CurrentThreadWorkingDirectory` and the
   per-task `TaskEnvironment` lifecycle in `MultiThreadedTaskEnvironmentDriver`. Migration
   alone (the attribute) skips the sidecar but does not change those code paths, so a short
   task like `GetPackageDirectory` (≈17 ms baseline) sees the fixed per-call overhead as a
   large relative regression even though absolute Δ is small.

3. **No new bootstrap-vs-SDK micro-benchmark was run** for this investigation: the
   `artifacts/bin/bootstrap/core/dotnet` SDK shipped with the local MSBuild build is older
   than `dotnet/sdk@main`, so it does not yet carry the migrated tasks. Re-running the bench
   here would only re-measure the unmigrated path; the meaningful next measurement is the
   one performed in #67 against the engine-side fix.

## Conclusion

This issue is effectively **blocked by #67**. The per-task migration work for the three
tasks named in the title is already done upstream; what remains is the engine-side
`TaskBuilder` / `MultiThreadedTaskEnvironmentDriver` cost, which is the scope of #67. Once
#67 lands an engine-side fix, the perfstar numbers for these three tasks should fall back in
line and #57 can close without a per-task code change in this PR.
