# MSBuild Strict Mode

> Status: experimental, opt-in only. Not enabled by default in any SDK or VS workload.

Strict Mode is a set of layered, content-addressed caches that turn declared `<Target Inputs="..." Outputs="...">`
metadata into a verifiable up-to-date contract. The feature is gated entirely behind a single canonical opt-in.

## Opt-in: the canonical truth table

A single environment variable, `MSBUILDSTRICTMODE`, controls every strict-mode layer (and only that variable; do
not introduce per-layer opt-in variables for new code). The same value is also accepted as the project property
`$(MSBuildStrictMode)` on the per-project layers (target cache, target up-to-date checker). The environment
variable takes precedence; an explicit "off" env value falls back to the project property.

Parsing is centralised in `Microsoft.Build.Strict.StrictModeSettings.ParseLevel` (file
`src/Shared/StrictModeSettings.cs`). The truth table below is the **only** sanctioned mapping; new layers MUST
route through `StrictModeSettings.ResolveLevel` / `StrictModeSettings.IsLayerEnabled` rather than rolling their
own parser.

| Value (case-insensitive)                       | Level                  |
| ---------------------------------------------- | ---------------------- |
| *(unset)*, `""`, `0`, `false`, `off`, `no`     | `Off`                  |
| `1`, `true`, `on`, `yes`, `warn`               | `Warn`                 |
| `enforce`, `strict`, `error`                   | `Enforce`              |
| *anything else (e.g. `garbage`, `yep`, `2`)*   | `Off` (typos do **not** silently enable strict mode) |

`Warn` means strict-mode violations are reported as warnings and the cache continues to populate. `Enforce` means
violations are reported as errors and the manifest is not persisted. Both levels are otherwise equivalent for
cache-hit behaviour.

## Per-layer disable gates

Each layer also honours a "kill-switch" environment variable that turns it off even when strict mode is otherwise
enabled. These are intended for production rollbacks; do **not** rely on them in test code (toggle
`MSBUILDSTRICTMODE` instead).

| Layer                                                  | Disable env var                |
| ------------------------------------------------------ | ------------------------------ |
| `StrictProjectCache`                                   | `MSBUILDSTRICTNOPROJECTCACHE`  |
| `StrictSolutionFastSkip`                               | `MSBUILDSTRICTNOFASTSKIP`      |
| `StrictTargetCache`                                    | *(none — uses opt-in only)*    |
| `TargetUpToDateChecker` (strict content-hash override) | *(none — uses opt-in only)*    |

## Behaviour changes from the pre-consolidation parser

Before issue #3, each strict-mode layer parsed `MSBUILDSTRICTMODE` independently with subtly different rules. The
consolidation introduced the following intentional behaviour changes:

- `TargetUpToDateChecker` previously required the literal value `1` (env var) or `true`/`1` (property) to enable
  the content-hash override. It now accepts every value in the truth table above. Code paths that relied on the
  override being off for `MSBUILDSTRICTMODE=true` will now see it on.
- `StrictProjectCache` and `StrictSolutionFastSkip` previously treated *every* non-empty, non-`0`/`false`/`off`
  value as enabled (including unknown values such as `MSBUILDSTRICTMODE=garbage`). They now treat unknown values
  as `Off`. Builds that depended on the lenient behaviour must use one of the canonical truthy values above.

Both changes are gated behind the strict-mode opt-in itself, so they cannot affect a build with
`MSBUILDSTRICTMODE` unset.

## Environment variables in cache keys

Strict Mode hashes a configurable allow-list of environment variables into its solution fast-skip, project cache,
and target cache keys. Use `$(StrictModeCacheKeyEnvVars)` to override the list for a project. The default value is:

`BUILD_*;DOTNET_*;MSBUILD_*;NUGET_*;RID;LANG;LC_ALL;LC_MESSAGES;LANGUAGE`

Each cache key includes the resolved `name=value` pairs for matching variables, sorted ordinal by variable name.
Strict Mode also excludes a small set of known volatile variables even when they match the allow-list, currently
`MSBUILDSTRICT*` telemetry variables and `DOTNET_CLI_TELEMETRY_SESSIONID`. Environment variables that are not
matched by `$(StrictModeCacheKeyEnvVars)` are assumed not to affect build outputs.

## Layers and on-disk layout

Strict Mode is composed of three independent cache layers, each gated by `MSBUILDSTRICTMODE` and each owning its
own on-disk directory. The layout below describes what lands on disk when strict mode is on; nothing here is
written when strict mode is off.

| Layer | Cache root | Manifest filename | Schema version | Source |
| ----- | ---------- | ----------------- | -------------- | ------ |
| `StrictSolutionFastSkip` | `<workload-root>\.strict-fastskip\` | `<sha256(args)>.manifest` | 2 | `src/MSBuild/StrictSolutionFastSkip.cs` |
| `StrictProjectCache` | `<ProjectDir>\obj\.strict-project\` | `<sha256(key)>.manifest` | 1 | `src/Build/BackEnd/BuildManager/StrictProjectCache.cs` |
| `StrictTargetCache` | `$(BaseIntermediateOutputPath)\.strict-cache\<target>\<sha256(key)>\` | `out\decl\`, `out\obs\`, `observed.list`, `.ok`, `inputs.stamp` (per project, alongside) | n/a (per-file format) | `src/Build/BackEnd/Components/RequestBuilder/StrictTargetCache.cs` |

Notes:

- All three formats are versioned with a single integer at the head of the file (`SchemaVersion`). A manifest
  with the wrong schema version is treated as a miss and is silently overwritten on the next successful build.
  There is **no** migration code today; bumping the schema version is the only supported "invalidate the world"
  operation.
- Manifest writes are atomic: every layer writes to `<final>.tmp.<pid>.<guid>` and then `File.Move(overwrite:true)`
  to publish. A crashed build leaves a `.tmp.<pid>.<guid>` orphan that no future build reads. (Orphan cleanup is
  tracked by issue #10.)
- The target cache uses a content-addressed directory tree per `(target, key)` rather than a single manifest file;
  the `.ok` marker is the commit signal — if it is absent, the directory is treated as a partial write and
  ignored.
- A per-project `inputs.stamp` sidecar (under `.strict-cache\inputs.stamp`) memoises file-content hashes across
  targets to avoid re-hashing the same source file N times per build. Sidecars are loaded once per process and
  flushed at the end of the build.

The cache layers can be selectively disabled (see the kill-switch table above). If the project cache and the
fast-skip cache are both off, target-level caching still works; the converse does not hold (target cache
hits do not bypass project-level build orchestration).

## Cache eviction

When the on-disk total under a target cache root exceeds `MSBUILDSTRICTCACHEMAXBYTES` (default `1073741824`, i.e.
1 GiB), `StrictTargetCache.EvictIfOversized` deletes entries in oldest-`.ok`-first order until the total is back
under budget. Eviction runs opportunistically on every successful persist. The size budget can also be set per
project via `$(MSBuildStrictCacheMaxBytes)`; the env var wins if both are set.

The solution fast-skip and project caches have no size cap today — they are intended to be much smaller (one
file per `(project, target-set, globals)` tuple). Issue #26 tracks better behaviour when the cache directory is
read-only or the disk is full for any layer.

## Exempt targets

Targets named in the project property `$(MSBuildStrictExemptTargets)` (semicolon-separated, case-insensitive)
are excluded from the target cache entirely: they are neither served from the cache nor written to it. This is
the supported escape hatch for targets that the user knows are not deterministic in their declared inputs and
outputs (e.g. ones that probe a network resource, read the system clock, or invoke a tool whose output is
non-stable).

Exempting a target does **not** disable the project-cache or the solution-fast-skip layer for the project; those
layers operate on the (project, target-set, globals) tuple and do not see individual target execution. If you
need to disable strict mode for an entire project, set `<MSBuildStrictMode>off</MSBuildStrictMode>` on the
project, or set the env var `MSBUILDSTRICTNOPROJECTCACHE=1` (project cache off) /
`MSBUILDSTRICTNOFASTSKIP=1` (fast-skip off) for a single MSBuild invocation.

## Allowed output directories

The target cache snapshots `$(IntermediateOutputPath)` and the parent directories of every declared
`Outputs="..."` path before and after target execution, and treats anything new or modified as a cache output.
Writes outside those directories trigger diagnostic `MSBSTRICT001` (warning in `warn` mode, error in `enforce`
mode).

The project property `$(StrictAllowedOutputDirs)` (semicolon-separated, project-relative paths) extends the set
of permitted write roots. Writes that fall under one of these roots are captured into the cache for replay and
do **not** trigger `MSBSTRICT001`. This is intended for code-generators that write to a stable location outside
`obj\`.

## Telemetry

When the environment variable `MSBUILDSTRICTTELEMETRYFILE` is set to a writable path, every cache event is
appended as a single JSON object (one per line) to that file. Multiple MSBuild nodes (entry + workers) share
the file safely (the writer opens with `FileMode.Append` + `FileShare.ReadWrite` and writes one line at a time).

Schema (every field except `ts`, `layer`, and `outcome` is optional):

```json
{
  "ts": "ISO 8601 UTC, e.g. 2026-05-19T16:29:42.0123456Z",
  "iteration": 7,
  "layer": "solution-fastskip | project-fastskip | target-cache",
  "outcome": "hit | miss | store",
  "project": "<absolute project path>",
  "target": "<target name(s)>",
  "reason": "<short diagnostic string>",
  "duration_us": 12345,
  "bytes_in": 0,
  "bytes_out": 0,
  "file_count": 0,
  "cache_key": "<sha256 hex>"
}
```

The optional `MSBUILDSTRICTTELEMETRYITER` environment variable is included verbatim on every line so a single
JSONL file can host multiple iterations of the same scenario (e.g. for the bench harness). Telemetry is a
best-effort sink: it must never break a build, and every exception inside `StrictTelemetry.Emit` is swallowed.

`reason` strings are currently heterogeneous and not part of the contract — issue #15 tracks introducing a
structured set of cache-miss reason codes and a binlog-friendly event.

## Configuration reference

### Environment variables

| Name                              | Purpose |
| --------------------------------- | ------- |
| `MSBUILDSTRICTMODE`               | Canonical opt-in (see truth table). Re-read on every call; safe to flip between submissions on MSBuild Server. |
| `MSBUILDSTRICTNOPROJECTCACHE`     | Kill-switch for `StrictProjectCache`. Any non-empty value disables the project layer even when strict mode is otherwise on. |
| `MSBUILDSTRICTNOFASTSKIP`         | Kill-switch for `StrictSolutionFastSkip`. Any non-empty value disables the solution layer. |
| `MSBUILDSTRICTCACHEMAXBYTES`      | Size budget for the per-project target cache root before eviction triggers. Integer bytes; default 1 GiB. Overrides `$(MSBuildStrictCacheMaxBytes)` when both set. |
| `MSBUILDSTRICTTELEMETRYFILE`      | If set, every cache event is appended to this file as JSONL. See the telemetry schema above. |
| `MSBUILDSTRICTTELEMETRYITER`      | Included verbatim in the `iteration` field of every telemetry line. |

### Project properties

| Name                              | Purpose |
| --------------------------------- | ------- |
| `MSBuildStrictMode`               | Per-project opt-in. Same value space as `MSBUILDSTRICTMODE`; the env var wins. Honoured by the target cache and the strict up-to-date checker. |
| `MSBuildStrictExemptTargets`      | Semicolon list of target names to exclude from the target cache (no read, no write). Case-insensitive. |
| `MSBuildStrictCacheMaxBytes`      | Per-project target-cache size budget; overridden by `MSBUILDSTRICTCACHEMAXBYTES` env var if both set. |
| `StrictAllowedOutputDirs`         | Semicolon list of project-relative directories outside `obj\` where targets may write without tripping `MSBSTRICT001`. |
| `StrictModeCacheKeyEnvVars`       | Semicolon list of env-var name patterns (with `*` wildcards) to include in every strict cache key. Default is set in `Microsoft.Common.props`. |

### CLI

There is currently **no** CLI switch for strict mode. The intended invocation is to set the env var, or to
forward the project property via `-p:MSBuildStrictMode=warn`. Issue #12 tracks adding a top-level
`/strict[:warn|enforce]` switch wired through `XMake.cs`.

## Diagnostics

| Code         | Severity (mode) | Meaning |
| ------------ | --------------- | ------- |
| `MSBSTRICT001` | warning (warn) / error (enforce) | A target wrote to a path outside its declared `Outputs="..."`, `$(IntermediateOutputPath)`, and `$(StrictAllowedOutputDirs)`. In `enforce` mode the target's cache entry is discarded. |

`MSBSTRICT002` (unsanctioned reads, requires file-access reporting) is reserved for the work in issue #5.

When investigating a miss, set `MSBUILDSTRICTTELEMETRYFILE=<path>` and re-run; the `reason` field will name the
specific check that fired (`no-manifest`, `manifest-corrupt`, `manifest-mismatch`, `output-missing-or-changed:<path>`,
`exception:<type> <message>`).

## Known limitations

- **No CLI switch.** Strict mode is reachable only via env vars and project properties today (#12).
- **No filesystem/network sandbox.** Strict mode trusts task authors to declare every read and write
  accurately; it does not enforce that they did (#5).
- **Source-file extension whitelist is incomplete.** Project-level cache keys hash only files matching a fixed
  extension list and so miss changes to e.g. `.tt`, `.proto`, `.cpp`, `.tsx` (#6).
- **Manifests embed absolute paths.** Strict-mode manifests are not portable between machines / users / OneDrive
  syncs (#8).
- **No first-class cache-miss diagnostics.** Reason strings are heterogeneous and there is no structured event
  (#15).
- **No concurrency / restart / multi-node test coverage** beyond happy paths (#17).
- **Outer multi-TF build can hit cache while inner outputs are stale.** Synthetic-output guard only checks
  file existence, not freshness (#20).
- **Hot-path overhead when feature is off** has not been measured (#18).
- **Solution fast-skip is CLI-only.** Visual Studio and direct `BuildManager` API consumers do not get it (#19).

See the [Strict Mode epic](https://github.com/jankratochvilcz/msbuild/issues/2) for the full list.

## Supported scenarios

Strict Mode is exercised end-to-end by the bench harness in `msbuild-perf-bench` against the following workload
shape (see the per-scenario JSON files in `msbuild-perf-bench/scenarios/`):

- `cold-clean` — full rebuild after `dotnet clean`; strict mode populates every cache from scratch.
- `cold-cached` — full build with caches already populated from a prior run.
- `noop` — re-run with nothing changed; should be the flagship win for strict mode.
- `touch-leaf` — touch a single source file in a leaf project.
- `touch-root` — touch a single source file in the root project.
- `edit-package-ref` — change a `<PackageReference>` version (forces restore; checks env-var fingerprinting).
- `edit-env-var` — flip a `<Setting>$(MyEnv)</Setting>` consumer (added by issue #4).

Any scenario outside this set is unsupported in the sense that the bench harness does not validate it; the
feature is expected to be correct for arbitrary projects, but performance has not been measured.

## See also

- [`src/Shared/StrictModeSettings.cs`](../../src/Shared/StrictModeSettings.cs) — canonical opt-in parser.
- [`src/Shared/StrictTelemetry.cs`](../../src/Shared/StrictTelemetry.cs) — telemetry sink.
- [`src/Shared/StrictCacheKeyEnvironment.cs`](../../src/Shared/StrictCacheKeyEnvironment.cs) — env-var
  fingerprint helper.
- [`src/Build/BackEnd/BuildManager/StrictProjectCache.cs`](../../src/Build/BackEnd/BuildManager/StrictProjectCache.cs)
- [`src/Build/BackEnd/Components/RequestBuilder/StrictTargetCache.cs`](../../src/Build/BackEnd/Components/RequestBuilder/StrictTargetCache.cs)
- [`src/MSBuild/StrictSolutionFastSkip.cs`](../../src/MSBuild/StrictSolutionFastSkip.cs)
- Strict Mode epic: [`jankratochvilcz/msbuild#2`](https://github.com/jankratochvilcz/msbuild/issues/2)
- MSBuild environment variables: [`MSBuild-Environment-Variables.md`](../wiki/MSBuild-Environment-Variables.md)
