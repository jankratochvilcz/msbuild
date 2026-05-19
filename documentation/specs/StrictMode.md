# Strict Mode

## Environment variables in cache keys

Strict Mode hashes a configurable allow-list of environment variables into its solution fast-skip, project cache, and target cache keys.

Use `$(StrictModeCacheKeyEnvVars)` to override the list for a project. The default value is:

`BUILD_*;DOTNET_*;MSBUILD_*;NUGET_*;RID;LANG;LC_ALL;LC_MESSAGES;LANGUAGE`

Each cache key includes the resolved `name=value` pairs for matching variables, sorted ordinal by variable name. Strict Mode also excludes a small set of known volatile variables even when they match the allow-list, currently `MSBUILDSTRICT*` telemetry variables and `DOTNET_CLI_TELEMETRY_SESSIONID`. Environment variables that are not matched by `$(StrictModeCacheKeyEnvVars)` are assumed not to affect build outputs.
