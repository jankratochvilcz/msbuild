# MTP + CTS hang demo (Linux vs Windows)

This self-contained demo proves the platform-specific failure that keeps
Clever Test Selection (CTS) pinned to VSTest mode in this repo, as documented in
[`scripts/cts/README.md`](../scripts/cts/README.md):

> CTS runs in VSTest mode ... which avoids the MTP↔CTS JsonRpc hang we hit
> during the initial adoption attempt.

## What it shows

xUnit v3 running under the **Microsoft.Testing.Platform (MTP)** runner, driven by
a CTS-style harness:

- **Linux/macOS:** completes normally.
- **Windows:** hangs.

## Why

An MSBuild-style unit test spawns a child process (as countless real MSBuild
tests do). The test host is launched by the driver (CTS / `mini-cts`) with its
standard streams **redirected** so their console output can be captured
(`cts ... --print-console-output`).

- On **Windows**, `Process.Start` with redirected streams sets
  `bInheritHandles=TRUE`. The grandchild inherits the host→driver output pipe
  handle. The driver's read of that pipe never reaches EOF while the grandchild
  lives → **hang**.
- On **Linux/macOS**, those handles are close-on-exec, so the grandchild does
  not inherit them, the pipe reaches EOF, and the driver returns → **completes**.

This is the same handle-inheritance class of bug MSBuild hit before with
`ToolTask` grandchild pipe handles (dotnet/msbuild#13351).

## Layout

| Path | Role |
| --- | --- |
| `Repro.Tests/` | xUnit v3 + MTP test host (`OutputType=Exe`), same shape as the repo's `*.UnitTests`. Contains a test that spawns a lingering, handle-inheriting child. |
| `Harness/` (`mini-cts`) | Faithful stand-in for CTS's `testingplatform` backend: launches the host with redirected streams, reads output to EOF, bounded by a watchdog so a hang becomes a clear `HANG DETECTED` verdict instead of an infinite block. |
| `Directory.Build.props` / `Directory.Packages.props` / `nuget.config` | Isolate the demo from the repo's Arcade-based build so it builds standalone on public runners. |

`mini-cts` stands in for the real `cts` tool because `cts` ships from an internal
DevDiv feed that public GitHub Actions runners cannot reach. It reproduces the
two behaviors that matter for the hang: (1) launch the MTP host with redirected
streams, (2) read that output to completion before reporting results.

## Run locally

```bash
cd .mtp-cts-demo
dotnet build Repro.Tests/Repro.Tests.csproj -c Release
dotnet build Harness/Harness.csproj -c Release

# With the lingering child (default): COMPLETED on Unix, HANG on Windows
dotnet Harness/bin/Release/net10.0/mini-cts.dll Repro.Tests/bin/Release/net10.0/Repro.Tests.dll

# Control (no child): COMPLETED everywhere
REPRO_SPAWN_CHILD=0 dotnet Harness/bin/Release/net10.0/mini-cts.dll Repro.Tests/bin/Release/net10.0/Repro.Tests.dll
```

## Run on CI

The [`mtp-cts-demo`](../.github/workflows/mtp-cts-demo.yml) workflow
(`workflow_dispatch`) runs the experiment on `ubuntu-latest` and
`windows-latest`. A **green** run means the hypothesis is confirmed: Linux
completes, Windows hangs.
