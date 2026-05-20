---
name: customer-patterns-code-coverage
description: Find real-world MSBuild usage patterns in popular .NET open-source repos (dotnet/sdk, dotnet/roslyn, NuGet, dotnet/aspnetcore, dotnet/runtime, etc.) and turn them into focused guard tests that document what customer scenario each test protects. Use when expanding MSBuild test coverage based on actual customer usage, filing or working tickets under the "Customer Patterns Code Coverage" epic, or moving items on the associated project board.
argument-hint: Mine customer MSBuild patterns from OSS repos and add guard tests with citations.
---

# Customer Patterns Code Coverage

This skill turns **real-world MSBuild usage** observed in popular .NET open-source repos into **focused guard tests** in this repository. The goal is not to chase 100% line coverage — it is to pin down the exotic, hard-to-discover behaviors that real authors rely on, so that future refactors don't regress them silently.

For the motivation and scope, see the epic: `jankratochvilcz/msbuild#31`.

## Where things live

- **Fork (everything for now):** `jankratochvilcz/msbuild`
- **Project board:** [`jankratochvilcz/projects/2`](https://github.com/users/jankratochvilcz/projects/2) — "Customer Patterns Code Coverage"
- **Branch / worktree:** `jankratochvilcz/customer-patterns-code-coverage` at `D:\src\microsoft\personal\msbuild-customer-patterns`

### Stable identifiers for the project board

| Identifier | Value |
|---|---|
| Project node ID | `PVT_kwHOACGbd84BYTUB` |
| Project number | `2` |
| Owner | `jankratochvilcz` (user, not org) |
| Status field ID | `PVTSSF_lAHOACGbd84BYTUBzhTaHQc` |
| Status: Todo | `f75ad846` |
| Status: In Progress | `47fc9ee4` |
| Status: Done | `98236657` |

Re-derive if needed:

```powershell
gh project field-list 2 --owner jankratochvilcz --format json
```

## Workflow at a glance

1. **Pick a survey target** — a feature area of MSBuild (e.g., item batching, item function chains, target ordering, `MSBuild` task call patterns, intrinsic tasks, property functions, condition evaluation, SDK resolution, custom task factories, logger event ordering).
2. **Mine real usage** of that area on GitHub across popular .NET OSS repos.
3. **Cross-reference our tests** — find the existing test file(s) for that area, audit what is and isn't already exercised.
4. **File a guard-test ticket** per coverage gap, with the customer citation in the body, and add it to the project board in **Todo**.
5. **Implement** — move the ticket to **In Progress**, write the test in this worktree with a citation comment in the test code itself, build, run the test, commit.
6. **Move to Done** on the project board once merged (or, while we're working in the fork only, once the test is pushed to `jankratochvilcz/customer-patterns-code-coverage`).

Every guard test must answer one question in its top comment: **"What real-world author behavior would break if this test failed?"**

## Step 1: Pick a survey target

Bias toward MSBuild areas that are:

- **Behaviorally flexible** (lots of valid shapes the author can use). Examples: item transforms (`@(Foo->'...')`), item function chains, conditions on metadata, target dependency ordering with `BeforeTargets`/`AfterTargets`, the `MSBuild` task with `Properties`/`RemoveProperties`/`Targets`/`Projects`, `CallTarget`, batching (`%(...)`), `Returns`/`Outputs` on targets, `Inputs`/`Outputs` incrementality.
- **Historically a source of incompatibility incidents** (look at git log / closed issues with "regression", "breaking change", or change waves).
- **Underrepresented in current tests** — many areas have happy-path tests but no test for unusual-but-valid shapes (e.g., conditions referencing unset metadata, empty item batches, recursive `MSBuild` task calls, mixed `Returns` + `Outputs`).

## Step 2: Mine real usage on GitHub

Use the GitHub code search API via `gh`. Always restrict to popular .NET OSS repos so the citation has weight. A good starting set:

```
repo:dotnet/sdk repo:dotnet/roslyn repo:dotnet/aspnetcore repo:dotnet/runtime
repo:NuGet/NuGet.Client repo:dotnet/msbuild repo:dotnet/arcade
repo:AvaloniaUI/Avalonia repo:cake-build/cake repo:xunit/xunit
repo:dotnet/maui repo:dotnet/wpf repo:dotnet/winforms
```

Search examples (run via `gh api search/code` or `gh search code`):

```powershell
# Items with metadata-conditioned transforms
gh search code --owner dotnet --filename "*.targets" "Condition=`"'%(`" "->'"

# Calls to the MSBuild task with RemoveProperties
gh search code "<MSBuild" "RemoveProperties=" --extension targets

# Item function chains
gh search code "@(.*->Distinct" --owner dotnet --owner NuGet
```

`gh search code` is rate-limited and pages slowly. For deeper mining, prefer raw `gh api` calls:

```powershell
gh api -X GET "search/code" -f q="RemoveProperties extension:targets repo:dotnet/sdk" --jq ".items[].html_url"
```

**Record each interesting hit** with: repo, file path, permalink (commit SHA, not `main`), short snippet, and the MSBuild feature it depends on. Permalink form: `https://github.com/<owner>/<repo>/blob/<sha>/<path>#L<n>-L<m>`.

> Note: as a sub-agent you may not have unrestricted internet access. If `gh search code` fails or is heavily rate-limited, fall back to (a) reading dependencies already vendored under `eng/` or `global.json`-pinned SDKs, (b) inspecting well-known SDK targets that ship inside the dotnet SDK on the local machine, or (c) asking the user to provide search results.

## Step 3: Cross-reference our test coverage

For each pattern you found, locate the existing test file(s) in `src/Build.UnitTests/`, `src/Tasks.UnitTests/`, `src/Evaluation/...UnitTests/`, etc.

Cheap mappings:

| Pattern area | Likely test home |
|---|---|
| Item transforms / item functions | `src/Build.UnitTests/Evaluation/Expander_Tests.cs`, `ItemFunctions_Tests.cs` |
| Batching `%(...)` | `src/Build.UnitTests/BackEnd/TaskExecutionHost_Tests.cs`, `Batching_Tests.cs` |
| Conditions | `src/Build.UnitTests/Evaluation/Conditionals_Tests.cs` |
| `MSBuild` task / `CallTarget` | `src/Tasks.UnitTests/MSBuild_Tests.cs`, `CallTarget_Tests.cs` |
| Target ordering | `src/Build.UnitTests/BackEnd/TargetEntry_Tests.cs`, `BuildRequestEngine_Tests.cs` |
| Property functions | `src/Build.UnitTests/Evaluation/Expander_Tests.cs` (search for `PropertyFunction`) |
| Item incrementality `Inputs/Outputs` | `src/Build.UnitTests/BackEnd/IntrinsicTask_Tests.cs`, `TargetUpToDateChecker_Tests.cs` |

Inside the candidate file, search for keywords near your pattern (`grep` on the method names) to confirm whether the unusual shape is exercised. The gap is real if you can describe an input shape that has no analog in the existing tests.

## Step 4: File a guard-test ticket

Issue title format: `[Guard test] <area>: <one-line pattern description>` — e.g. `[Guard test] MSBuild task: RemoveProperties combined with Properties on the same call`.

Issue body template:

```markdown
## Customer pattern

<one short paragraph describing what real authors do>

Observed in:
- <repo>/<path>@<sha> — <permalink> — <snippet, 5–15 lines>
- (additional citations if found in multiple repos)

## MSBuild feature exercised

<which MSBuild feature(s) — e.g. "MSBuild task `RemoveProperties` interaction with `Properties` on the same call">

## Coverage gap

<which test file looks like the natural home, and what it currently does NOT exercise>

## Proposed guard test

<bullet list of inputs / expected behaviors the test should pin down>

## Parent

#31
```

Then add the issue to the project board in **Todo**:

```powershell
$issue = "https://github.com/jankratochvilcz/msbuild/issues/<N>"
gh project item-add 2 --owner jankratochvilcz --url $issue --format json
```

The item lands in **Todo** by default for new issues; if you need to set it explicitly, see "Moving items on the board" below.

## Step 5: Implement the guard test

Before starting work on a ticket, move it to **In Progress** (see below).

Test authoring rules:

1. **Make the test hermetic** — use the existing test harness helpers (`ObjectModelHelpers.CreateInMemoryProject`, `MockLogger`, `TestEnvironment`), no network, no external SDK lookups beyond what the test infrastructure already brings.
2. **Put the citation in the test code, not just the ticket.** A future engineer reading the test should be able to tell what they would break if they deleted it:

   ```csharp
   /// <summary>
   /// Guards the pattern from dotnet/sdk's GenerateBuildDependencyFile.targets where
   /// `RemoveProperties` is used on a recursive MSBuild task call to drop the parent's
   /// `TargetFramework` before reentering. If our task ever stopped honoring this combo,
   /// every multi-targeted build in the .NET SDK would silently regress.
   ///
   /// Source: https://github.com/dotnet/sdk/blob/&lt;sha&gt;/&lt;path&gt;#L&lt;n&gt;-L&lt;m&gt;
   /// Tracked by: https://github.com/jankratochvilcz/msbuild/issues/&lt;N&gt;
   /// </summary>
   [Fact]
   public void MSBuildTask_RemoveProperties_With_Properties_Drops_Then_Sets()
   { ... }
   ```

3. **Pin a behavior, not an implementation.** Assert on observable outputs (target results, properties returned, logged events, ItemGroups produced), not on internal call counts.
4. **One pattern per test.** Easier to triage when something regresses.
5. **Run it.** Build the test project first, then run via MTP filter; see `running-unit-tests` skill for the exact incantations.

Commit message format:

```
Guard test: <area> — <pattern>

Guards customer pattern observed in <repo>@<sha>. See #<N>.
```

Then `git push` to your fork branch.

## Moving items on the board

Find the item's project-item ID once you've added it:

```powershell
gh project item-list 2 --owner jankratochvilcz --format json |
  ConvertFrom-Json |
  ForEach-Object { $_.items } |
  Where-Object { $_.content.url -eq "https://github.com/jankratochvilcz/msbuild/issues/<N>" } |
  Select-Object -ExpandProperty id
```

Update status:

```powershell
# Move to In Progress
gh project item-edit `
  --id <PVTI_...> `
  --project-id PVT_kwHOACGbd84BYTUB `
  --field-id PVTSSF_lAHOACGbd84BYTUBzhTaHQc `
  --single-select-option-id 47fc9ee4

# Move to Done (use 98236657)
# Move back to Todo (use f75ad846)
```

**Always update the board as you transition:**

| When | Action |
|---|---|
| Filing a new ticket | Add to project (auto-lands in Todo) |
| Starting work on a ticket | Move to **In Progress** |
| Test is written, built, run, and pushed | Move to **Done** *and* close the issue (or leave open if waiting on user review) |
| Blocked or pivoted | Comment why, move back to **Todo** |

The board status should always match reality. If you've written the test but haven't pushed, the item stays in **In Progress**.

## Closing the loop on the epic

When all initial guard-test tickets generated in the first run are Done, comment on the epic (#31) summarizing what was added, link the merged commits, and propose the next survey target.

## Useful one-liners

```powershell
# All items in the project, with status
gh project item-list 2 --owner jankratochvilcz --format json | ConvertFrom-Json |
  ForEach-Object { $_.items } |
  Select-Object @{n="num";e={$_.content.number}}, @{n="title";e={$_.content.title}}, status

# Open issues in the fork tagged to this epic
gh issue list --repo jankratochvilcz/msbuild --search "is:open #31 in:body"
```
