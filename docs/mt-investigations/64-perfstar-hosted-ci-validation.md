# Investigation note: Run E2/E3 patches on hosted CI

Tracks https://github.com/jankratochvilcz/msbuild/issues/64

> **Note**: The `dotnet-perfstar` repository (which owns the perf pipeline YAML) lives in
> Azure DevOps (DevDiv) and is not mirrored to GitHub. No fork could be created on
> github.com, so this sketch lives in the `jankratochvilcz/msbuild` GitHub fork as the
> planning/tracking artifact for the CI-pipeline change that will be implemented against
> the Azure DevOps `dotnet-perfstar` repo.

## Hypothesis

The local A/B benchmark on Apple-Silicon hardware showed E2 (`TaskEnvironment` passthrough)
and E3 (skip `TaskHost`) recover the entire `WarnForInvalidProjectsTask` regression and
bring wall-time to −2.5% vs MT baseline. **But the local signal under-states the production
regression by ≥5× on net8-console-app and ≥20× on orchard-core.** Every proposed fix must be
validated on the hosted pipeline before claiming victory.

| source | hardware | scenario | non-MT ms | MT ms | Δ % |
|---|---|---|---:|---:|---:|
| bench-baseline (local) | Apple M-series | Net8ConsoleApp | 1 107 | 1 142 | **+3.1%** |
| prod-14281586 hosted | Linux VM | net8-console-app-rebuild | 3 265 | 3 535 | +8.3% |
| prod-14281586 hosted | Windows VM | net8-console-app-rebuild | 3 358 | 3 697 | +10.1% |
| prod-14281586 hosted | Linux VM | net8-console-app-inc-build | 3 099 | 3 562 | **+14.9%** |
| prod-14281471 hosted | Linux VM | **orchard-core-rebuild** | 420 638 | 1 276 955 | **+204%** |
| prod-14281471 hosted | Windows VM | **orchard-core-rebuild** | 685 204 | 1 904 029 | **+178%** |

## Proposed change

Add a new pipeline step in `pipeline/performance-ci-hosted.yml` (or the appropriate
dotnet-perfstar YAML) that:

1. Pulls the experimental bootstrap branch (`users/copilot/hosted-perf-pipeline` or a
   per-experiment branch such as `exp/no-callback-lock`, `exp/task-env-passthrough`,
   `exp/skip-task-host`).
2. Builds the bootstrap with `./build.sh -c Release /p:CreateBootstrap=true`.
3. Runs MT mode (`MSBUILDFORCEMULTITHREADED=1`) against the full scenario suite:
   `net8-console-app-*`, `orchard-core-*`, `blazorwasm-*`, `eshop-*` on BOTH Windows and
   Linux agents.
4. Ingests results into the analytics store tagged with the experiment branch name in
   `run_key` so they can be diffed against the baseline MT run.
5. Posts a comment on the tracking issue with the diff summary.

YAML sketch (intent only — actual schema depends on the dotnet-perfstar pipeline format):

```yaml
- job: hosted_mt_validation
  strategy:
    matrix:
      linux:
        image: ubuntu-latest
        rid: linux-x64
      windows:
        image: windows-latest
        rid: win-x64
  variables:
    expBranch: $(experimentBranch)        # e.g. exp/no-callback-lock
    bootstrapRoot: $(Agent.TempDirectory)/msbuild-bootstrap
  steps:
  - checkout: dotnet/msbuild
    fetchDepth: 1
    submodules: false
    ref: $(expBranch)
    path: msbuild-bootstrap
  - script: ./build.sh -c Release /p:CreateBootstrap=true
    workingDirectory: $(bootstrapRoot)
    displayName: Build experimental bootstrap
  - template: templates/run-perfstar.yml
    parameters:
      dotnet: $(bootstrapRoot)/artifacts/bin/bootstrap/core/dotnet
      mtFlag: true
      scenarios:
        - net8-console-app-rebuild
        - net8-console-app-inc-build
        - orchard-core-rebuild
        - orchard-core-inc-build
        - blazorwasm-rebuild
        - blazorwasm-inc-build
        - eshop-rebuild
        - eshop-inc-build
      iterations: 5
      runKey: 'exp-$(expBranch)-$(Build.BuildId)'
```

## Investigation plan

1. Document the validation protocol in `documentation/validate-mt-fix.md` in the
   dotnet-perfstar repo.
2. Wire the YAML step above into `pipeline/performance-ci-hosted.yml`.
3. Run E2 patch end-to-end as proof-of-protocol (re #7).
4. Run E3 patch end-to-end (re #7).
5. Establish a "validated MT fix" label in the msbuild repo; gate merges of MT-relevant PRs
   on a green hosted run.
6. For every subsequent issue (#57, #59, #60, #61, #62), wire the validation step into
   their "Validate on hosted CI" checklist item.
7. Add `iter ≥ 3` median reporting to the performance pipeline (warmup effect).

## Ship criterion

≥50% drop in `build-time-jsonlist` delta on `orchard-core-rebuild` Linux (cleanest signal —
large solution, fast non-MT baseline, no Windows FS-case-folding confound).

## Reproducer

```kusto
scenario_metrics
| where metric_name == "build-time-jsonlist" and run_key in ("prod-14281586","prod-14281471")
| summarize p50 = percentile(value, 50) by scenario_pair, os, mt_flag
| evaluate pivot(mt_flag, max(p50))
| project scenario_pair, os, nonmt=False, mt=True
```

## Expected outcome

`0 ms — process change, not a perf change.` Prevents shipping fixes that "work locally" and
leave multi-minute regressions on production builds. The local Mac signal under-states the
production regression by 5× to 20×; without this protocol, the entire optimization
workstream risks chasing false positives.

## Where this PR will actually land

Once the YAML / pipeline change is finalized, the real PR will be opened against the
Azure DevOps `dotnet-perfstar` repo (DevDiv organization). This GitHub PR remains as the
public planning record so the issue has a linked tracking artifact.
