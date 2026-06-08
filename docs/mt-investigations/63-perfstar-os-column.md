# Investigation note: Add `os` column to perfstar analytics schema

Tracks https://github.com/jankratochvilcz/msbuild/issues/63

> **Note**: The `dotnet-perfstar` repository lives in Azure DevOps (DevDiv) and is not
> mirrored to GitHub. No fork could be created on github.com, so this sketch lives in the
> `jankratochvilcz/msbuild` GitHub fork as the planning/tracking artifact for the schema
> change that will be implemented against the Azure DevOps `dotnet-perfstar` repo.

## Hypothesis

The analytics schema lacks an `os` column on `task_runs`, `target_runs`, `project_runs`,
`project_evaluations`, and `scenario_metrics`. Ingestions are recorded per
`(scenario_pair, run_key, os, mt_flag)` in the `scenarios` table, but no foreign key links
metric / task rows to the ingestion — **per-OS attribution at task or metric grain is
structurally impossible**. The Windows-specific orchard pass5 +672% regression (#59) could
only be inferred via a min/max proxy.

| scenario | mt | med(min) | med(max) | proxy Δ (max-min) |
|---|---|---:|---:|---:|
| console-inc-build | non-MT | 2 750 | 2 863 | +113 |
| console-inc-build | MT | 3 260 | 4 251 | **+991** |
| blazorwasm-rebuild | MT | 7 687 | 10 793 | **+3 106** |

## Proposed change

Add `os: string` column to:

- `task_runs`
- `target_runs`
- `project_runs`
- `project_evaluations`
- `scenario_metrics`

Populated at ingestion time from the agent / scenario context already known.

Schema diff (Kusto control commands, in the style of the existing
`src/RunResultsProcessor/KustoSchema.kql`):

```kql
.alter-merge table task_runs           (os: string)
.alter-merge table target_runs         (os: string)
.alter-merge table project_runs        (os: string)
.alter-merge table project_evaluations (os: string)
.alter-merge table scenario_metrics    (os: string)
```

C# row record changes in `src/RunResultsProcessor/Kusto/AnalyticsData.cs`:

```csharp
public sealed record TaskRunRow(
    string RunKey,
    string ScenarioPair,
    string Os,                 // NEW
    bool MtFlag,
    int Iteration,
    string TaskName,
    double DurationMs);
// (same Os addition on TargetRunRow, ProjectRunRow, ProjectEvaluationRow, ScenarioMetricRow)
```

Ingestion path in `src/RunResultsProcessor/Binlog/BinlogComparer.cs`: populate `Os` from
the scenario context (already available as `ScenarioDescriptor.Os`).

## Investigation plan

1. Identify ingestion code writing to each affected table (likely
   `src/PerformanceMonitoringRunner/` or `src/RunResultsProcessor/`).
2. Make `.alter-merge` change in `KustoSchema.kql`.
3. Add `Os` to each row record + ingestion mapper.
4. Backfill `os` for historical runs if possible, otherwise document the cutover `run_key`.
5. Build via `dotnet build PerformanceTests.sln` and verify ingestion still runs.
6. Update example queries in `findings*.md` and the issue templates to use the authoritative
   `os` column.
7. Re-run the analyses in #59 and #62 against the new column.

## Reproducer

```kusto
// Today: cannot answer "which OS regressed more on this task?"
task_runs | where run_key == "14309939" | getschema | project ColumnName
// (no `os` in the schema)
```

After the change:

```kusto
task_runs
| where run_key == "14309939" and task_name == "ResolveAssemblyReference"
| summarize p50 = percentile(duration_ms, 50) by os, mt_flag
```

## Expected outcome

`0 ms — schema change, not a perf change.` Unblocks authoritative root-causing of every
OS-specific finding (notably #59 / Windows GetCanonicalForm). Without it, those
investigations are bottlenecked on proxy heuristics.

## Where this PR will actually land

Once the schema change is implemented, the real PR will be opened against the Azure DevOps
`dotnet-perfstar` repo (DevDiv organization). This GitHub PR remains as the public
planning record so the issue has a linked tracking artifact.
