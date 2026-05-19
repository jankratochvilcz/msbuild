// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;

using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    /// <summary>
    /// Unit tests for <see cref="StrictProjectCache"/>, the Tier-3 project-level fast-skip cache.
    ///
    /// These tests exercise the public surface directly (no BuildManager round-trip): a synthetic
    /// project directory is laid out on disk, a hand-built <see cref="BuildResult"/> is fed into
    /// the "miss → store" path, and a follow-up <see cref="StrictProjectCache.TryHit"/> verifies
    /// the key-invalidation behaviour for each cache-key input documented in the spec.
    /// </summary>
    public sealed class StrictProjectCache_Tests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly TestEnvironment _env;
        private readonly string _projectDir;
        private readonly string _projectPath;

        public StrictProjectCache_Tests(ITestOutputHelper output)
        {
            _output = output;
            _env = TestEnvironment.Create(_output);
            // Default to ENABLED for these tests; individual tests may override.
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "1");
            _env.SetEnvironmentVariable("MSBUILDSTRICTNOPROJECTCACHE", null);

            // Use a short root path under %TEMP% to keep the total file path under net472's
            // MAX_PATH (260). The manifest path is
            //   <projectDir>\obj\.strict-project\<sha256-64char>.manifest.tmp.<pid>.<guid>
            // which alone consumes ~150 chars beyond projectDir; nested TestEnvironment paths
            // can push us over the limit on net472 (no long-path support by default).
            _projectDir = Path.Combine(Path.GetTempPath(), "spc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_projectDir);
            _projectPath = Path.Combine(_projectDir, "P.csproj");
            File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(_projectDir, "Class1.cs"), "namespace N { class Class1 { } }");

            StrictProjectCache.ResetCachesForTest();
        }

        public void Dispose()
        {
            StrictProjectCache.ResetCachesForTest();
            try
            {
                if (Directory.Exists(_projectDir))
                {
                    Directory.Delete(_projectDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
            _env.Dispose();
        }

        // ---------------------------------------------------------------------
        // IsEnabled / TargetsAreCacheable
        // ---------------------------------------------------------------------

        [Fact]
        public void IsEnabled_DefaultsToFalse_WhenEnvVarNotSet()
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", null);
            StrictProjectCache.IsEnabled().ShouldBeFalse();
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("on")]
        [InlineData("warn")]
        [InlineData("enforce")]
        public void IsEnabled_TrueForTruthyValues(string value)
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", value);
            StrictProjectCache.IsEnabled().ShouldBeTrue();
        }

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("off")]
        public void IsEnabled_FalseForFalseyValues(string value)
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", value);
            StrictProjectCache.IsEnabled().ShouldBeFalse();
        }

        [Fact]
        public void IsEnabled_FalseWhenDisableEnvVarSet()
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTNOPROJECTCACHE", "1");
            StrictProjectCache.IsEnabled().ShouldBeFalse();
        }

        [Fact]
        public void TargetsAreCacheable_NullOrEmpty_IsAllowed()
        {
            StrictProjectCache.TargetsAreCacheable(null, out _).ShouldBeTrue();
            StrictProjectCache.TargetsAreCacheable(Array.Empty<string>(), out _).ShouldBeTrue();
        }

        [Theory]
        [InlineData("Build")]
        [InlineData("GetTargetPath")]
        [InlineData("GetCopyToOutputDirectoryItems")]
        [InlineData("ResolveProjectReferences")]
        public void TargetsAreCacheable_TrueForCacheableTargets(string target)
        {
            StrictProjectCache.TargetsAreCacheable(new[] { target }, out _).ShouldBeTrue();
        }

        [Theory]
        [InlineData("Restore")]
        [InlineData("Clean")]
        [InlineData("Rebuild")]
        [InlineData("Pack")]
        [InlineData("Publish")]
        [InlineData("VSTest")]
        public void TargetsAreCacheable_FalseForSkipTargets(string target)
        {
            StrictProjectCache.TargetsAreCacheable(new[] { target }, out string reason).ShouldBeFalse();
            reason.ShouldStartWith("skip-target:");
        }

        [Fact]
        public void TargetsAreCacheable_FalseForUnknownTarget()
        {
            StrictProjectCache.TargetsAreCacheable(new[] { "MyCustomTarget" }, out string reason).ShouldBeFalse();
            reason.ShouldStartWith("non-cacheable-target:");
        }

        // ---------------------------------------------------------------------
        // TryHit / RegisterMiss / MaybeStoreOnCompletion roundtrip
        // ---------------------------------------------------------------------

        [Fact]
        public void TryHit_ColdMiss_ReturnsNullAndPopulatesCacheKey()
        {
            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Debug" };
            var targets = new[] { "Build" };

            StrictProjectCache.CachedBuild hit = StrictProjectCache.TryHit(
                _projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out string reason);

            hit.ShouldBeNull();
            cacheKey.ShouldNotBeNullOrEmpty();
            reason.ShouldBe("no-manifest");
        }

        [Fact]
        public void Roundtrip_StoreThenHit_ReturnsCachedTargetResult()
        {
            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Debug" };
            var targets = new[] { "Build" };

            // Cold miss.
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _).ShouldBeNull();

            // Register the miss and synthesise a successful BuildResult with a Build target.
            const int subId = 4242;
            StrictProjectCache.RegisterMiss(subId, _projectPath, globals, targets, cacheKey);

            BuildResult result = MakeSuccessResult(("Build", new[] { ("out.dll", new[] { ("Configuration", "Debug") }) }));
            StrictProjectCache.MaybeStoreOnCompletion(subId, result);

            // Warm hit.
            StrictProjectCache.CachedBuild hit = StrictProjectCache.TryHit(
                _projectPath, globals, targets, BuildRequestDataFlags.None, out string key2, out string reason);
            hit.ShouldNotBeNull();
            key2.ShouldBe(cacheKey);
            reason.ShouldBe("hit");

            // Materialise into a fresh BuildResult.
            var fresh = new BuildResult();
            hit.PopulateBuildResult(fresh, targets);

            fresh.ResultsByTarget.ShouldContainKey("Build");
            TargetResult tr = fresh.ResultsByTarget["Build"];
            tr.ResultCode.ShouldBe(TargetResultCode.Success);
            tr.Items.Length.ShouldBe(1);
            tr.Items[0].ItemSpec.ShouldBe("out.dll");
            tr.Items[0].GetMetadata("Configuration").ShouldBe("Debug");
        }

        [Fact]
        public void Hit_FiltersOutNonRequestedTargets()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build", "GetTargetPath" };

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _).ShouldBeNull();
            StrictProjectCache.RegisterMiss(1, _projectPath, globals, targets, cacheKey);

            BuildResult stored = MakeSuccessResult(
                ("Build",          new[] { ("a.dll", Array.Empty<(string, string)>()) }),
                ("GetTargetPath",  new[] { ("a.dll", Array.Empty<(string, string)>()) }));
            StrictProjectCache.MaybeStoreOnCompletion(1, stored);

            // Hit with the same targets, then ask PopulateBuildResult to project only one.
            StrictProjectCache.CachedBuild hit = StrictProjectCache.TryHit(
                _projectPath, globals, targets, BuildRequestDataFlags.None, out _, out _);
            hit.ShouldNotBeNull();

            var fresh = new BuildResult();
            hit.PopulateBuildResult(fresh, new[] { "Build" });
            fresh.ResultsByTarget.ShouldContainKey("Build");
            fresh.ResultsByTarget.ShouldNotContainKey("GetTargetPath");
        }

        [Fact]
        public void NonSuccessBuild_IsNotStored()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _);
            StrictProjectCache.RegisterMiss(99, _projectPath, globals, targets, cacheKey);

            BuildResult result = MakeFailureResult();
            StrictProjectCache.MaybeStoreOnCompletion(99, result);

            // Still a miss the second time.
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldBeNull();
            reason.ShouldBe("no-manifest");
        }

        [Fact]
        public void Store_SweepsOnlyStaleManifestTemps()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _).ShouldBeNull();

            string manifestDir = Path.Combine(_projectDir, "obj", ".strict-project");
            string manifestPath = Path.Combine(manifestDir, cacheKey + ".manifest");
            string staleTemp = manifestPath + ".tmp.123.old";
            string freshTemp = manifestPath + ".tmp.456.new";

            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(staleTemp, "stale");
            File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(freshTemp, "fresh");
            File.SetLastWriteTimeUtc(freshTemp, DateTime.UtcNow.AddMinutes(-5));

            StrictProjectCache.RegisterMiss(101, _projectPath, globals, targets, cacheKey);
            StrictProjectCache.MaybeStoreOnCompletion(101, MakeSuccessResult(("Build", new[] { ("out.txt", Array.Empty<(string, string)>()) })));

            File.Exists(staleTemp).ShouldBeFalse();
            File.Exists(freshTemp).ShouldBeTrue();
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldNotBeNull();
            reason.ShouldBe("hit");
        }

        [Fact]
        public void MissingRequestedTarget_IsNotStored()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _);
            StrictProjectCache.RegisterMiss(77, _projectPath, globals, targets, cacheKey);

            // Result has a different target than the one requested.
            BuildResult result = MakeSuccessResult(("GetTargetPath", new[] { ("x", Array.Empty<(string, string)>()) }));
            StrictProjectCache.MaybeStoreOnCompletion(77, result);

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldBeNull();
            reason.ShouldBe("no-manifest");
        }

        // ---------------------------------------------------------------------
        // Cache-key invalidation
        // ---------------------------------------------------------------------

        [Fact]
        public void SourceFileChange_InvalidatesCache()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };

            SeedHit(globals, targets);
            // Confirm hit.
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r1).ShouldNotBeNull();
            r1.ShouldBe("hit");

            // Mutate source file -> key changes -> miss.
            File.WriteAllText(Path.Combine(_projectDir, "Class1.cs"), "// changed\nnamespace N { class Class1 { } }");

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r2).ShouldBeNull();
            r2.ShouldBe("no-manifest");
        }

        [Fact]
        public void ProjectFileChange_InvalidatesCache()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };

            SeedHit(globals, targets);
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r1).ShouldNotBeNull();
            r1.ShouldBe("hit");

            File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>X</DefineConstants></PropertyGroup></Project>");

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r2).ShouldBeNull();
            r2.ShouldBe("no-manifest");
        }

        [Fact]
        public void GlobalPropertyChange_InvalidatesCache()
        {
            var targets = new[] { "Build" };
            var debug   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Debug" };
            var release = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Release" };

            SeedHit(debug, targets);
            StrictProjectCache.TryHit(_projectPath, debug, targets, BuildRequestDataFlags.None, out _, out string r1).ShouldNotBeNull();
            r1.ShouldBe("hit");

            // Different global property value => different cache key => miss.
            StrictProjectCache.TryHit(_projectPath, release, targets, BuildRequestDataFlags.None, out _, out string r2).ShouldBeNull();
            r2.ShouldBe("no-manifest");
        }

        [Fact]
        public void TargetListChange_InvalidatesCache()
        {
            var globals = new Dictionary<string, string>();
            SeedHit(globals, new[] { "Build" });

            StrictProjectCache.TryHit(_projectPath, globals, new[] { "Build" }, BuildRequestDataFlags.None, out _, out string r1).ShouldNotBeNull();
            r1.ShouldBe("hit");

            // Different requested targets => different cache key => miss.
            StrictProjectCache.TryHit(_projectPath, globals, new[] { "GetTargetPath" }, BuildRequestDataFlags.None, out _, out string r2).ShouldBeNull();
            r2.ShouldBe("no-manifest");
        }

        [Fact]
        public void AllowListedEnvironmentVariableChange_InvalidatesCache()
        {
            const string envVarName = "MSBUILD_CACHEKEY_TEST_ALLOW";
            _env.SetEnvironmentVariable(envVarName, "one");

            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };
            SeedHit(globals, targets);

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r1).ShouldNotBeNull();
            r1.ShouldBe("hit");

            _env.SetEnvironmentVariable(envVarName, "two");

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string r2).ShouldBeNull();
            r2.ShouldBe("no-manifest");
        }

        [Fact]
        public void NonListedEnvironmentVariableChange_DoesNotInvalidateCache()
        {
            const string envVarName = "STRICTCACHEKEY_TEST_IGNORE";
            _env.SetEnvironmentVariable(envVarName, "one");

            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };
            SeedHit(globals, targets);

            _env.SetEnvironmentVariable(envVarName, "two");

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldNotBeNull();
            reason.ShouldBe("hit");
        }

        [Fact]
        public void VolatileAllowListedEnvironmentVariableChange_DoesNotInvalidateCache()
        {
            const string envVarName = "DOTNET_CLI_TELEMETRY_SESSIONID";
            _env.SetEnvironmentVariable(envVarName, "one");

            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };
            SeedHit(globals, targets);

            _env.SetEnvironmentVariable(envVarName, "two");

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldNotBeNull();
            reason.ShouldBe("hit");
        }

        [Fact]
        public void RestoreTarget_IsNotCached_TryHitReturnsSkipReason()
        {
            var globals = new Dictionary<string, string>();
            StrictProjectCache.TryHit(_projectPath, globals, new[] { "Restore" }, BuildRequestDataFlags.None, out _, out string reason).ShouldBeNull();
            reason.ShouldBe("skip-target:Restore");
        }

        [Fact]
        public void ReplaceExistingProjectInstanceFlag_BypassesCache()
        {
            var globals = new Dictionary<string, string>();
            var targets = new[] { "Build" };
            SeedHit(globals, targets);

            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.ReplaceExistingProjectInstance, out _, out string reason).ShouldBeNull();
            reason.ShouldBe("replace-instance");
        }

        [Fact]
        public void Disabled_ReturnsNullWithDisabledReason()
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", null);
            StrictProjectCache.TryHit(_projectPath, new Dictionary<string, string>(), new[] { "Build" }, BuildRequestDataFlags.None, out _, out string reason).ShouldBeNull();
            reason.ShouldBe("disabled");
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private void SeedHit(Dictionary<string, string> globals, string[] targets)
        {
            StrictProjectCache.TryHit(_projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _);
            int subId = Environment.TickCount;
            StrictProjectCache.RegisterMiss(subId, _projectPath, globals, targets, cacheKey);

            var perTarget = new (string, (string, (string, string)[])[])[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                perTarget[i] = (targets[i], new[] { ($"out_{i}.txt", Array.Empty<(string, string)>()) });
            }
            BuildResult result = MakeSuccessResult(perTarget);
            StrictProjectCache.MaybeStoreOnCompletion(subId, result);
        }

        private static BuildResult MakeSuccessResult(params (string TargetName, (string ItemSpec, (string Key, string Value)[] Metadata)[] Items)[] targets)
        {
            var result = new BuildResult();
            var work = new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null);
            foreach (var (targetName, items) in targets)
            {
                var taskItems = new ProjectItemInstance.TaskItem[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    var ti = new ProjectItemInstance.TaskItem(items[i].ItemSpec, definingFileEscaped: null);
                    foreach (var (k, v) in items[i].Metadata)
                    {
                        ti.SetMetadata(k, v);
                    }
                    taskItems[i] = ti;
                }
                result.AddResultsForTarget(targetName, new TargetResult(taskItems, work));
            }
            return result;
        }

        private static BuildResult MakeFailureResult()
        {
            var result = new BuildResult();
            var work = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            result.AddResultsForTarget("Build", new TargetResult(Array.Empty<ProjectItemInstance.TaskItem>(), work));
            return result;
        }
    }
}
