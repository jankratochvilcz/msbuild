# MSBuild environment variables list

This document describes the environment variables that are respected in MSBuild, its purpose and usage.

Some of the env variables listed here are unsupported, meaning there is no guarantee that variable or a specific combination of multiple variables will be respected in upcoming release, so please use at your own risk.

- `MSBuildDebugEngine=1` & `MSBUILDDEBUGPATH=<DIRECTORY>`
  - Set this to cause any MSBuild invocation launched within this environment to emit binary logs and additional debugging information to `<DIRECTORY>`. Useful when debugging build or evaluation issues when you can't directly influence the MSBuild invocation, such as in Visual Studio. More details on [capturing binary logs](./Providing-Binary-Logs.md)
- `MSBUILDTARGETOUTPUTLOGGING=1`
  - Set this to enable [printing all target outputs to the log](https://learn.microsoft.com/archive/blogs/msbuild/displaying-target-output-items-using-the-console-logger).
- `MSBUILDLOGTASKINPUTS=1`
  - Log task inputs (not needed if there are any diagnostic loggers already).
- `MSBUILDEMITSOLUTION=1`
  - Save the generated .proj file for the .sln that is used to build the solution. The generated files are emitted into a binary log by default and their presence on disk can break subsequent builds.
- `MSBUILDENABLEALLPROPERTYFUNCTIONS=1`
  - Enable [additional property functions](https://devblogs.microsoft.com/visualstudio/msbuild-property-functions/). If you need this level of detail you are generally served better with a binary log than the text log.
- `MSBUILDLOGVERBOSERARSEARCHRESULTS=1`
  - In ResolveAssemblyReference task, log verbose search results.
- `MSBUILDLOGCODETASKFACTORYOUTPUT=1`
  - Dump generated code for task to a `<GUID>.txt` file in the TEMP directory
- `MSBUILDDISABLENODEREUSE=1`
  - Set this to not leave MSBuild processes behind (see `/nr:false`, but the environment variable is useful to also set this for Visual Studio for example).
- `MSBUILDLOGASYNC=1`
  - Enable asynchronous logging.
- `MSBUILDDEBUGONSTART=1`
  - Launches debugger on build start. Works on Windows operating systems only.
  - Setting the value of 2 allows for manually attaching a debugger to a process ID. This works on Windows and non-Windows operating systems.
- `MSBUILDDEBUGSCHEDULER=1` & `MSBUILDDEBUGPATH=<DIRECTORY>`
  - Dumps scheduler state at specified directory.
- `MsBuildSkipEagerWildCardEvaluationRegexes`
  - If specified, overrides the default behavior of glob expansion. During glob expansion, if the path with wildcards that is being processed matches one of the regular expressions provided in the [environment variable](#msbuildskipeagerwildcardevaluationregexes), the path is not processed (expanded).
  - The value of the environment variable is a list of regular expressions, separated by semicolon (;).
- `MSBUILDFORCEALLTASKSOUTOFPROCESS`
  - Set this to force all tasks to run out of process (except inline tasks).
- `MSBUILDFORCEINLINETASKFACTORIESOUTOFPROC`
  - Set this to force all inline tasks to run out of process. It is not compatible with custom TaskFactories.
- `MSBUILDFORCEMULTITHREADED=1`
  - Set this to force MSBuild to run in multi-threaded mode (using in-proc nodes for parallel build), equivalent to passing `-multiThreaded` / `-mt` on the command line. Useful for opting in without modifying command lines.
- `MSBUILD_CONSOLE_USE_DEFAULT_ENCODING`
  - It opts out automatic console encoding UTF-8. Make Console use default encoding in the system.

## Strict Mode (experimental, opt-in)

The Strict Mode feature (see [`documentation/specs/StrictMode.md`](../specs/StrictMode.md)) is gated entirely behind the following environment variables. All are experimental and may change without notice.

- `MSBUILDSTRICTMODE`
  - Canonical opt-in for every Strict Mode layer. Accepted values (case-insensitive): `0`/`false`/`off`/`no` and unknown values → off; `1`/`true`/`on`/`yes`/`warn` → warn-mode; `enforce`/`strict`/`error` → enforce-mode. Unknown values are treated as off so a typo does not silently enable the feature. Re-read on every call so MSBuild Server / long-lived worker nodes pick up changes between submissions. The `$(MSBuildStrictMode)` project property is also honoured by per-project layers when the env var is unset.
- `MSBUILDSTRICTNOPROJECTCACHE`
  - Kill-switch for the project-level Strict cache (`StrictProjectCache`). Set to any non-empty value to disable that layer even when `MSBUILDSTRICTMODE` is otherwise on. Intended for production rollbacks; tests should toggle `MSBUILDSTRICTMODE` instead.
- `MSBUILDSTRICTNOFASTSKIP`
  - Kill-switch for the solution-level Strict fast-skip cache (`StrictSolutionFastSkip`). Same semantics as `MSBUILDSTRICTNOPROJECTCACHE`.
- `MSBUILDSTRICTCACHEMAXBYTES`
  - Size budget (integer bytes) for the per-project target cache root before opportunistic eviction triggers. Default is 1 GiB (`1073741824`). Overrides the `$(MSBuildStrictCacheMaxBytes)` project property if both are set.
- `MSBUILDSTRICTTELEMETRYFILE`
  - If set to a writable path, every Strict Mode cache event (hit / miss / store) is appended as one JSON object per line. Schema is documented in `documentation/specs/StrictMode.md`. Multiple MSBuild nodes (entry + workers) share the file safely. Telemetry must never break a build; all errors inside the sink are swallowed.
- `MSBUILDSTRICTTELEMETRYITER`
  - Included verbatim as the `iteration` field on every telemetry line so a single JSONL file can host multiple iterations of the same scenario (e.g. for the bench harness).