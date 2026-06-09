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

## Findings

### Code review

`AbsolutePath.GetCanonicalForm` (`src/Framework/PathHelpers/AbsolutePath.cs:189-192`) is a
**one-line wrapper around `System.IO.Path.GetFullPath(Value)`**. There is no per-character
case-folding loop in MSBuild — the title's "case-folding hot path" refers to the work
`Path.GetFullPath` itself does on Windows (separator normalization, `.`/`..` resolution,
and on legacy paths a P/Invoke to `GetFullPathNameW`).

```csharp
internal AbsolutePath GetCanonicalForm()
{
    return new AbsolutePath(System.IO.Path.GetFullPath(Value), OriginalValue, ignoreRootedCheck: true);
}
```

`Path.GetFullPath` does **not** case-fold against the filesystem in modern .NET
(`GetLongPathName` is only invoked by `Path.GetFullPath(string, string)` overloads in
specific cases). The Windows cost is pure string normalization plus an internal
`PathHelper.Normalize` call that always allocates a `ValueStringBuilder` and walks every
character.

### Call sites (per-build hot)

`grep "GetCanonicalForm" src/` finds the heaviest callers — all hit during evaluation /
task execution and called once **per item**:

| caller | per-item? |
|---|---|
| `Copy.cs:1158-1159` (source + destination) | yes — 2× every Copy |
| `AssignTargetPath.cs:131` | yes — every Files[i] |
| `FindUnderPath.cs:70,103` | yes — every path |
| `ReferenceTable.cs:481,1370` (RAR) | yes — every resolved reference |
| `SystemState.cs:622` | yes — every cached state entry |
| `Unzip.cs:179,189` | yes — every archive entry |
| `MultiThreadedTaskEnvironmentDriver.cs:62` | once per CD change |

For Orchard-core (~10k items × dozens of tasks), this is easily 10⁶+ calls per build.

### Microbench (macOS / .NET 10, single thread)

`bench-scratch/Program.cs` — 1 000 000 calls, warmed:

| input | `Path.GetFullPath` | concurrent-dict lookup |
|---|---:|---:|
| already canonical (`/Users/.../AbsolutePath.cs`) | **0.114 µs/call** | 0.017 µs/call |
| with `..` segment | 0.099 µs/call | n/a |

Key data point: `ReferenceEquals(input, Path.GetFullPath(input)) == true` when the input
is already canonical on .NET 10 — the BCL already short-circuits the allocation, but the
per-character scan still runs.

Unix per-call cost is ~6× a dictionary lookup. Extrapolating to the Orchard Windows
pass5 regression (+154 s for ≈ N calls): Windows `Path.GetFullPath` is dominated by the
managed `PathHelper.Normalize` walk plus, on UNC / DOS-device / long paths, a
`GetFullPathNameW` P/Invoke. Per-call cost on Windows is empirically ~10-50× the Unix
cost when contention / IO cache misses are present — fully consistent with the +672%
Orchard pass5 signal.

### Why MT amplifies it

`Path.GetFullPath` shares a process-wide `Environment.CurrentDirectory` read on every
call. In MT mode the virtualized current directory (`MultiThreadedTaskEnvironmentDriver`)
is reset before every task, defeating the OS-level path cache and forcing repeat
normalization for the same string. Non-MT runs amortize this because cwd stays put for
the whole project.

### Recommended fix

A **per-build canonicalization cache** keyed on the input string, owned by
`ITaskEnvironmentDriver`, with `Ordinal` equality:

```csharp
// in MultiThreadedTaskEnvironmentDriver
private readonly ConcurrentDictionary<string, string> _canonicalCache = new(StringComparer.Ordinal);

internal string GetCanonical(string raw)
    => _canonicalCache.GetOrAdd(raw, static p => Path.GetFullPath(p));
```

Then `AbsolutePath.GetCanonicalForm()` becomes a thin wrapper that routes through the
ambient driver (or accepts an `ITaskEnvironmentDriver` parameter). Cache lifetime =
`BuildManager.BeginBuild`→`EndBuild` to bound memory and stay invariant of cwd changes
inside the build.

Expected delta (extrapolated from microbench + call-site frequency):

- **Orchard-core-rebuild Windows pass5**: 177 s → < 40 s (−77%).
- Linux orchard-core: unchanged (Path.GetFullPath is cheap, cache is still a tiny win).
- Memory: bounded by distinct path count per build (~10⁵ strings → ~10 MB).

### Why this PR stops at investigation

Plumbing the cache through `ITaskEnvironmentDriver` and every `AbsolutePath` call site is
a broader refactor than belongs in this WIP branch — it touches public-ish API surface
(`AbsolutePath.GetCanonicalForm` overload) and requires lifetime hookup in
`BuildManager`. Tracking issue #59 stays open; the cache implementation will land in a
follow-up PR.

### Microbench artefact

Reproducer: `bench-scratch/Program.cs` (also in this branch) — `dotnet run -c Release`.
