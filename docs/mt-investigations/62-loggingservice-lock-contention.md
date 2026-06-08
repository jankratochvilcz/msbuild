# Investigation: `LoggingService` central-lock contention

Tracks https://github.com/jankratochvilcz/msbuild/issues/62

## Hypothesis

`LoggingService` has 9+ `lock(_lockObject)` sites on the central dispatch path. On
Orchard-core MT (70+ concurrent in-proc projects logging from many threads), this is the
strongest candidate for the **+178% to +204% wall regression** not explained by per-task
setup:

| scenario | os | non-MT ms | MT ms | Δ % |
|---|---|---:|---:|---:|
| orchard-core-rebuild | Windows | 685 204 | 1 904 029 | **+178%** |
| orchard-core-rebuild | Linux | 420 638 | 1 276 955 | **+204%** |
| orchard-core-inc-build | Windows | 244 154 | 623 875 | +155% |
| orchard-core-inc-build | Linux | 158 907 | 361 506 | +127% |

Lock sites:
`src/Build/BackEnd/Components/Logging/LoggingService.cs` lines 786, 808, 834, 892, 931, 1026,
1088, 1115, 1182, 1338.

Local A/B E1 (remove `lock(_callbackMonitor)` from `TaskHost.Log*Event`) disconfirmed at
peak concurrency = 2, but does NOT generalize to multi-project builds.

## Investigation plan / microbench plan

1. **Re-run E1 (no-callback-lock) on orchard-core** at production scale. Capture wall delta.
2. **Contention trace**: `dotnet-trace collect --providers
   Microsoft-Windows-DotNETRuntime:0x4000:5` (Contention keyword) on an orchard-core MT
   build. Sum contention time on `LoggingService._lockObject`.
   - contention > 30% of wall → hypothesis confirmed.
   - contention 10-30% → partial; deeper decomposition.
   - contention < 10% → disconfirmed at scale too; file follow-up.
3. **Microbench (BenchmarkDotNet)**: drive `LoggingService.LogBuildEvent` from N = 1, 4, 16,
   64 producer threads against a no-op sink. Measure throughput and per-call latency at each
   concurrency level. Target file (TODO follow-up commit):
   `src/Build.UnitTests/Logging/LoggingServiceContentionBench.cs`.
4. **Mitigation candidates**:
   - Per-node logger queue: each `BuildRequestEngine` node owns its own ring buffer; a
     single drain thread merges into the central forwarder.
   - `Channel<BuildEventArgs>` with single-writer-per-node, single-reader-on-forwarder.
     Lock-free hot path.

## Reproducer

### Kusto query

```kusto
scenario_metrics
| where run_key in ("prod-14281471","prod-14282288") and metric_name == "build-time-jsonlist"
| summarize p50 = percentile(value, 50) by scenario_pair, os, mt_flag
| order by scenario_pair, os, mt_flag
```

### Bench harness

```bash
# Build E1 experimental bootstrap
cd /Users/jan/src/microsoft/msbuild-exp-no-callback-lock
./build.sh -c Release /p:CreateBootstrap=true
DOTNET_E1=$(pwd)/artifacts/bin/bootstrap/core/dotnet

# Build baseline MT bootstrap
cd /Users/jan/src/microsoft/msbuild
./build.sh -c Release /p:CreateBootstrap=true
DOTNET_BASE=$(pwd)/artifacts/bin/bootstrap/core/dotnet

ASSET=/path/to/orchard-core
for DOTNET in $DOTNET_BASE $DOTNET_E1; do
  for i in $(seq 0 4); do
    find "$ASSET" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
    MSBUILDFORCEMULTITHREADED=1 $DOTNET build -c Release \
      /bl:"./bench/orchard-$(basename $(dirname $(dirname $(dirname $DOTNET))))-i$i.binlog" \
      "$ASSET/OrchardCore.sln"
  done
done
```

### Contention trace

```bash
dotnet-trace collect \
  --providers 'Microsoft-Windows-DotNETRuntime:0x4000:5' \
  --process-id <pid> \
  --duration 00:02:00 \
  -o ./bench/contention.nettrace
```

Open with PerfView; group by `Caller > Callee`; filter on `LoggingService`.

## Expected outcome

- A measured % of wall spent in contention on `LoggingService._lockObject` for
  orchard-core-rebuild MT on both OSes.
- A go/no-go decision on the per-node queue prototype.
- If confirmed: ~200 000-500 000 ms (3-8 minutes) per build on orchard-core-rebuild /
  30-50% of MT wall regression recovered.
