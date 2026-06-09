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

## Source-level analysis (2026-06-09)

Re-read of `src/Build/BackEnd/Components/Logging/LoggingService.cs` on `main`
sharpens the hypothesis. The original framing ("9+ lock sites on the central
dispatch path") conflates rare init/registration locks with the actual hot
path. Per-event central-lock contention is structurally smaller than the doc
suggested, but a real serialization point still exists — the single drain
thread.

### Inventory of `lock (_lockObject)` sites

`_lockObject` is declared at line 89. The 10 lock-acquire sites partition
cleanly by call frequency:

| Line | Method | Frequency per build | On per-event hot path? |
|---:|---|---|---|
| 786 | `LogIncludeWarningCodes` | once per `(InstanceId, ContextId)` first warning-config init | partial (per project, not per event) |
| 808 | `RegisteredLoggerTypeNames` getter | diagnostic only | no |
| 834 | `RegisteredSinkNames` getter | diagnostic only | no |
| 892 | `InitializeComponent` | once per build | no |
| 931 | `ShutdownComponent` | once per build | no |
| 1026 | `RegisterLogger` | once per logger registration | no |
| 1088 | `UnregisterAllLoggers` | once per build | no |
| 1115 | `RegisterDistributedLogger` | once per distributed logger | no |
| 1182 | `InitializeNodeLoggers` | once per node | no |
| 1338 | `ProcessLoggingEvent` — sync-mode fallback only | **per event, but only when `_logMode == Synchronous`** | sync mode only |

The per-event entry point `LogBuildEvent` (line 1237) → `ProcessLoggingEvent`
(line 1291) branches on `_logMode`:

```csharp
if (_logMode == LoggerMode.Asynchronous)
{
    // enqueue to ConcurrentQueue<object> _eventQueue, signal _enqueueEvent
}
else
{
    lock (_lockObject) { RouteBuildEvent(buildEvent); }   // line 1338
}
```

`BuildManager` (line 3173) sets `LoggerMode.Synchronous` only when
`cpuCount == 1 && _buildParameters.UseSynchronousLogging`. Any multi-cpu
build — which includes every MT-mode (`MSBUILDFORCEMULTITHREADED=1`) build
and effectively all production CI builds — runs `LoggerMode.Asynchronous`.
**So the central-lock per-event site at line 1338 is dead code for the MT
regression we're chasing.** That fact alone substantially weakens the
original hypothesis.

### What actually serializes in async mode

In async mode the producer side is lock-free (`ConcurrentQueue.Enqueue` +
`AutoResetEvent.Set`), but everything downstream is single-threaded by
construction:

- `StartLoggingEventProcessing` (line 1410) spawns **one** `Thread`
  (`MSBuild LoggingService events queue pump`) running `LoggingEventProc`.
- The loop dequeues sequentially and calls `LoggingEventProcessor`
  (line 1560) → `RouteBuildEvent` → forwarder sinks → registered loggers
  (binary logger, console logger, VS logger). All of these run on this one
  drain thread.

This is the real bottleneck:

1. **Single consumer**: per-event work (binlog write, console formatting,
   sink fan-out) cannot scale past one core regardless of producer count.
2. **Backpressure stall**: when `eventQueue.Count >= _queueCapacity`
   (line 1320), producers block on `dequeueEvent.WaitOne()`. Under high
   event rates with a slow consumer (e.g. binary logger flushing) every
   task thread that logs an event can stall on the consumer.
3. **AutoResetEvent ping-pong**: high-rate producers churn
   `enqueueEvent`/`dequeueEvent` — kernel transitions, not user-space
   contention, but visible in `dotnet-trace`.

So the corrected hypothesis is:

> The dispatch hot path is not blocked by `lock (_lockObject)` in production
> async mode; it is blocked by the **single drain thread** and its
> bounded-queue backpressure. The cost scales with total event volume
> (events/sec across all projects × per-event sink work), not with project
> count directly.

### Why E1 disconfirmed at Net8ConsoleApp scale

E1 removed `lock (_callbackMonitor)` from `TaskHost.LogMessageEvent` /
`LogWarningEvent` / `LogErrorEvent` / `LogCustomEvent` — a different lock
on the `TaskHost` callback path, not `LoggingService._lockObject`. Per
`/tmp/mt-bench/results/SUMMARY.md`, on a 1-project Net8ConsoleApp build
with peak 2 concurrent tasks:

- `baseline-mt` wall p50: 1142 ms
- `E1-no-cb-lock` wall p50: 1278 ms (**+136 ms / +11.9% regression**)
- `WarnForInvalidProjectsTask`: 30 → 25 ms (−17%, within noise)

At that concurrency the `_callbackMonitor` lock was uncontended; removing
it traded one fast uncontended `Monitor.Enter` for whatever cache /
ordering effects the new code path introduced, and the build regressed.
E2 (`TaskEnvironment` AsyncLocal short-circuit) recovered the
`WarnForInvalidProjectsTask` cost cleanly (30 → 4 ms), which pinpointed
**per-task `TaskEnvironment` setup** — not logging — as the dominant
overhead at small scale.

E1 says nothing about behavior at orchard-core scale (~70 in-proc
projects × peak ~30+ concurrent tasks each), and the central LoggingService
lock is a different lock altogether.

### Where contention should and should not show up at scale

| Workload | Peak concurrent tasks | Total event rate | Expected dominant cost |
|---|---:|---|---|
| Net8ConsoleApp (E1 disconfirmation scale) | ~2 | low | per-task TaskEnvironment setup (E2 confirmed) |
| eshop / blazorwasm | ~16 | medium | per-task setup + un-migrated SWA / NuGet tasks (see `/tmp/mt-analysis/findings-prod.md` §A) |
| orchard-core MT (~70 projects, 8+ concurrent tasks/project) | 200+ | high | single-consumer drain thread saturation; producer stalls on `dequeueEvent` when binlog sink flushes |

At Net8ConsoleApp scale the total events/sec is well below what one drain
thread can handle, so neither the central lock nor the queue-drain
saturates — consistent with the E1 null result. The doc's original
prediction that the central lock matters at orchard-core scale is partially
wrong (wrong lock) but partially right (a serialization point does exist
on the central dispatch path; it's just the drain thread, not
`_lockObject`).

### Cross-reference: pipeline analysis on non-orchard scenarios

`/tmp/mt-analysis/findings-prod.md` (production run_key 14309939) analyses
the 6 scenario pairs that did run in that pipeline (eshop, blazorwasm,
console, plugin variants). Orchard-core was not in that run_key. The top
MT regressions there are dominated by **un-migrated tasks** (Static Web
Assets pipeline, NuGet `WarnForInvalidProjectsTask`, `GetRestoreSolutionProjectsTask`)
and the in-process `MSBuild` task, not by logging-service overhead. That's
exactly what we'd expect if logging contention only kicks in past the
single-drain-thread saturation point — which these mid-scale workloads
don't reach.

This means orchard-core remains the only workload where the logging
serialization story is even testable. The pipeline data is null evidence
on this hypothesis, not contradicting evidence.

### Recommended fix shape

Stop trying to widen the central lock; replace the drain topology.

1. **Sharded MPSC queues (per-node / per-engine)**. Each
   `BuildRequestEngine` (or each in-proc node id) owns its own
   `Channel<BuildEventArgs>` (single-producer-bounded). Producers never
   contend with each other.
2. **Parallel drain with ordered merge** at the central forwarder.
   - Cheap path: round-robin / per-shard worker threads each draining one
     channel into the existing sink fan-out. Sinks that require global
     order (console logger) consume from a final merge stage; sinks that
     don't (binary logger) can be sharded end-to-end with a post-build
     reorder by event sequence id.
   - Binary logger already writes monotonically; assigning a global
     monotonic sequence id at enqueue-time keeps deterministic ordering
     without a single consumer.
3. **Cheaper backpressure**. `AutoResetEvent` + `WaitOne` round-trip per
   stall is expensive. `Channel<T>` with a bounded capacity uses
   `ValueTask`-based async backpressure and avoids the
   kernel-event ping-pong.
4. **Out of scope but related**: `LogIncludeWarningCodes` (line 786)
   takes the central lock on the warning-config-init path. It only fires
   once per project context and uses `ConcurrentDictionary`
   (`warningsByProject`) for storage — the `lock` is only needed because
   the field is lazily allocated. Replace with `LazyInitializer.EnsureInitialized`
   to drop the last semi-hot lock site on `_lockObject` entirely.

### Validation plan (when orchard-core capacity is available)

This investigation is now bounded; the next experiment is a measurement,
not another guess.

1. Pipeline run of orchard-core-rebuild MT on Windows + Linux, baseline
   only (no patch). Confirms reproducer at production scale.
2. Add the existing `dotnet-trace` contention recipe (see "Contention
   trace" above), but also collect **CPU sampling** for the
   `MSBuild LoggingService events queue pump` thread. If that thread is
   pinned at ~100% CPU for a substantial fraction of build wall, the
   drain-thread saturation story is confirmed without needing a code
   change.
3. Only if (2) confirms: prototype the sharded-channel topology behind a
   feature flag (`MSBUILDLOGGINGSHARDED=1`), run the same orchard-core
   pair, and compare wall.

Until (1) and (2) run, this issue stays in "analyzed, not actionable" —
all the cheap data we can extract from the source has been extracted, and
the original BenchmarkDotNet microbench in section "Investigation plan"
above would still be useful as a unit-level sanity check but won't
substitute for the orchard-core measurement.
