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
| Status: Todo | `6454483e` |
| Status: In Progress | `67a5530b` |
| Status: Review | `536246fe` |
| Status: Done | `ad446cde` |

Re-derive if needed:

```powershell
gh project field-list 2 --owner jankratochvilcz --format json
```

> Note: option IDs are regenerated whenever you re-run `updateProjectV2Field` with a new options list. If you change the columns, refresh this table.

## Workflow at a glance

1. **Pick a survey target** — a feature area of MSBuild (e.g., item batching, item function chains, target ordering, `MSBuild` task call patterns, intrinsic tasks, property functions, condition evaluation, SDK resolution, custom task factories, logger event ordering).
2. **Mine real usage** of that area on GitHub across popular .NET OSS repos.
3. **Cross-reference our tests** — find the existing test file(s) for that area, audit what is and isn't already exercised.
4. **Locate the relevant implementation** — identify the file(s), classes, and methods that would have to break for the customer pattern to regress. These pointers go in the ticket *and* in the test's doc comment.
5. **File a guard-test ticket** per coverage gap. Body must include the customer citation, a **minimal but complete repro XML**, the implementation pointers, and the proposed assertions. Add to the project board in **Todo**.
6. **Implement** — move the ticket to **In Progress**, branch off `upstream/main` (one branch per ticket), write the test, build, run the test, commit, push.
7. **Open a PR** against `jankratochvilcz/msbuild:main` (one PR per ticket). Move the ticket to **Review**. The PR closes the issue on merge; the ticket moves to **Done** automatically.

Every guard test must answer one question in its top comment: **what real-world author behavior would break if this test failed?** That comment should *not* contain issue or PR numbers — those drift; the customer-source reference is what stays meaningful long-term.

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

## Step 4: Locate the relevant implementation

For the gap you found, identify **which code would have to break** for the customer pattern to regress. The goal is a short list of files/classes/methods (and ideally line ranges) that future engineers can scan when the test fails or when refactoring nearby.

Cheap mappings:

| Pattern area | Likely implementation home |
|---|---|
| Item functions (`Distinct`, `WithMetadataValue`, `AnyHaveMetadataValue`, `Reverse`, `Metadata`, `DirectoryName`, etc.) | `Expander.ItemTransformFunctions.*` in `src/Build/Evaluation/Expander.cs` (look at the function dispatch table near the top of the class, then the static `internal static void <Name>` methods further down) |
| Property functions and quoted-expression functions | `Expander.cs` (`ExpandQuotedExpressionFunction`, `ExpandPropertyBody`, friends) |
| Condition evaluation | `src/Build/Evaluation/Conditionals/` (Scanner/Parser/AndExpressionNode/etc.) |
| `MSBuild` *task* (`Properties`, `RemoveProperties`, `Projects`, `Targets`, `BuildInParallel`) | `src/Tasks/MSBuild.cs` |
| `MSBuild` *intrinsic task* (the engine-side counterpart, including `SkipNonexistentTargets`) | `src/Build/BackEnd/Components/RequestBuilder/IntrinsicTasks/MSBuild.cs` |
| Target skipping / `SkipNonexistentTargets` flag plumbing | `src/Build/BackEnd/Components/RequestBuilder/TargetBuilder.cs`, `RequestBuilder.cs`, `TaskBuilder.cs`, `src/Build/BackEnd/Components/Scheduler/Scheduler.cs` |
| Batching loop | `src/Build/BackEnd/Components/RequestBuilder/Batch/`, `TaskExecutionHost` |
| Evaluation phases / property and item evaluation order | `src/Build/Evaluation/Evaluator.cs` |

Use `grep` and `lsp` to confirm. A good list cites **3–6 concrete spots**, no more. Avoid listing every file that mentions the feature name.

## Step 5: File a guard-test ticket

Issue title format: `[Guard test] <area>: <one-line pattern description>` — e.g. `[Guard test] MSBuild task: RemoveProperties combined with Properties on the same call`.

Issue body must include:

1. **Customer pattern** — one short paragraph and a permalinked citation (commit SHA, not `main`).
2. **Minimal repro** — a *complete* MSBuild project (or pair of projects) the reader can paste into two files and run. It must reproduce the shape we're guarding in the smallest form possible: literal item identities and metadata where they matter, no SDK dependency, only the targets needed. Annotate the repro with the expected output.
3. **MSBuild feature exercised** — the bullet list of behaviors the pattern depends on.
4. **Relevant implementation** — the 3–6 files/classes/methods identified in Step 4.
5. **Coverage gap** — which existing test file is the natural home and what shape is missing.
6. **Proposed guard test** — the assertions, in plain English.
7. **Parent** — `#<epic number>`.

Add the issue to the project board in **Todo**:

```powershell
$issue = "https://github.com/jankratochvilcz/msbuild/issues/<N>"
gh project item-add 2 --owner jankratochvilcz --url $issue --format json
```

The item lands in **Todo** by default for new issues; if you need to set it explicitly, see "Moving items on the board" below.

## Step 6: Implement the guard test

Before starting work on a ticket, move it to **In Progress**, then branch off `upstream/main`:

```powershell
cd D:\src\microsoft\personal\msbuild-customer-patterns
git fetch upstream
git checkout -b jankratochvilcz/guard-test-issue-<N> upstream/main
```

> **One branch and one PR per ticket.** The long-lived `jankratochvilcz/customer-patterns-code-coverage` branch holds only this skill and other shared assets; *do not* land tests on it. Each PR rebases cleanly on `upstream/main` and can be reviewed and merged in isolation.

Test authoring rules:

1. **Make the test hermetic** — use the existing test harness helpers (`ObjectModelHelpers.CreateInMemoryProject`, `MockLogger`, `TestEnvironment`), no network, no external SDK lookups beyond what the test infrastructure already brings.
2. **Doc comment is the long-term record.** Keep it compact and answer two questions:
   * What customer pattern does this guard? Name the repo + file (no commit SHA needed in the comment — that drifts; the issue tracks the SHA).
   * What relevant implementation does it pin down? Name the class and method(s) inside `Expander.cs` / `MSBuild.cs` / etc.

   **Do not** put issue, PR, or epic numbers in the test's doc comment. They drift over forks and rebases; the customer-source citation is what stays meaningful.

   Template:

   ```csharp
   /// <summary>
   /// Customer-pattern guard: &lt;one-line description of the shape&gt;. Matches the shape
   /// &lt;repo&gt;/&lt;file&gt; uses to &lt;what it does&gt;:
   /// <code>&lt;tightened single-line XML or expression&gt;</code>
   /// &lt;one-line description of the branches the test pins down&gt;
   ///
   /// Relevant impl: &lt;class.method(s)&gt; in &lt;source file&gt;.
   /// </summary>
   [Fact]
   public void Foo_Bar_Baz() { ... }
   ```

3. **Pin a behavior, not an implementation.** Assert on observable outputs (target results, properties returned, logged events, ItemGroups produced), not on internal call counts.
4. **One pattern per test.** Easier to triage when something regresses.
5. **Run it.** Build the test project first, then run via MTP filter; see `running-unit-tests` skill for the exact incantations.

Commit message format:

```
Guard test: <area> — <pattern>

<2–3 sentences describing the customer pattern and what the test pins down>
```

Then `git push -u origin jankratochvilcz/guard-test-issue-<N>`.

## Step 7: Open a PR and move to Review

One PR per ticket, against `jankratochvilcz/msbuild:main`:

```powershell
gh pr create `
  --repo jankratochvilcz/msbuild `
  --base main `
  --head jankratochvilcz:jankratochvilcz/guard-test-issue-<N> `
  --title "Guard test: <area> — <pattern>" `
  --body-file pr-body.md
```

The PR body should briefly describe:

- the customer pattern (repo + permalinked file),
- what the test pins down (the assertions in plain English),
- the relevant implementation (mirror the issue's pointers),
- `Closes #<N>.`

Then move the ticket to **Review** on the project board. Leave it there until the PR merges — at which point GitHub auto-closes the issue and you move it to **Done**.

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
  --single-select-option-id 67a5530b

# Move to Review (use 536246fe)
# Move to Done   (use ad446cde)
# Move back to Todo (use 6454483e)
```

**Always update the board as you transition:**

| When | Action |
|---|---|
| Filing a new ticket | Add to project (auto-lands in Todo) |
| Starting work on a ticket | Move to **In Progress** |
| PR opened against `jankratochvilcz/msbuild:main` | Move to **Review** |
| PR merged | Move to **Done** (GitHub will have auto-closed the issue via `Closes #<N>`) |
| Blocked or pivoted | Comment why, move back to **Todo** |

The board status should always match reality. If you've written the test but haven't opened a PR, the item stays in **In Progress**.

## Adjacent work that lives on the same board

The "Customer Patterns Code Coverage" board is also the home for other **personal-triage tickets the contributor is tracking against MSBuild** — most commonly **flaky-test follow-ups** discovered while scanning upstream CI. The same "in the fork, on the board" rule applies:

1. **File the issue in `jankratochvilcz/msbuild`**, not `dotnet/msbuild`. Body: symptom, exact failing build link, suspected location (file + class + method), root-cause hypothesis, suggested fix, reproduction sketch — same template depth as a guard-test ticket.
2. **Label** with `flaky-test` (the upstream repo's label; mirror it in the fork). For guard-test tickets, use `[Guard test]` title prefix; for flaky-test tickets, use `Flaky test:` title prefix.
3. **Add to the project board** via `gh project item-add 2 --owner jankratochvilcz --url <url>` (lands in Todo).
4. **If you discovered the flake by scanning `dotnet/msbuild` CI** and accidentally filed in the upstream repo first, close the upstream issue with a one-line comment redirecting to the fork issue. Do not lose the upstream reference — paste the upstream build/run links into the fork issue body.
5. **Adding more evidence to an existing upstream flaky-test issue** (e.g., a second test hitting the same root cause) **is fine to do directly upstream as a comment** — that is sharing data, not filing a new tracker.

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
