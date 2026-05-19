// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    /// <summary>
    /// Unit tests for the engine-side strict-mode target cache (<see cref="StrictTargetCache"/>).
    /// These tests are kept tightly scoped to (a) the static mode/exempt parsers and (b) a small
    /// end-to-end roundtrip that exercises the HIT/MISS persistence path through TargetEntry.
    /// </summary>
    public sealed class StrictTargetCache_Tests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly TestEnvironment _env;

        public StrictTargetCache_Tests(ITestOutputHelper output)
        {
            _output = output;
            _env = TestEnvironment.Create(_output);
            // Make sure no inherited env var leaks into these tests.
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", null);
            _env.SetEnvironmentVariable("MSBUILDSTRICTCACHEMAXBYTES", null);
        }

        public void Dispose() => _env.Dispose();

        // ---------------------------------------------------------------------
        // Static helpers: GetMode / IsTargetExempt
        // ---------------------------------------------------------------------

        [Fact]
        public void GetMode_IsOff_WhenNeitherSet()
        {
            ProjectInstance proj = MakeInstance("<Project />");
            StrictTargetCache.GetMode(proj).ToString().ShouldBe("Off");
            StrictTargetCache.IsEnabled(proj).ShouldBeFalse();
        }

        [Theory]
        [InlineData("true",    "Warn")]
        [InlineData("1",       "Warn")]
        [InlineData("warn",    "Warn")]
        [InlineData("on",      "Warn")]
        [InlineData("enforce", "Enforce")]
        [InlineData("strict",  "Enforce")]
        [InlineData("error",   "Enforce")]
        [InlineData("nope",    "Off")]
        [InlineData("",        "Off")]
        public void GetMode_ParsesProjectProperty(string value, string expectedName)
        {
            ProjectInstance proj = MakeInstance(
                $"<Project><PropertyGroup><MSBuildStrictMode>{value}</MSBuildStrictMode></PropertyGroup></Project>");
            StrictTargetCache.GetMode(proj).ToString().ShouldBe(expectedName);
        }

        [Fact]
        public void GetMode_EnvVarTakesPrecedenceOverProperty()
        {
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "enforce");

            ProjectInstance proj = MakeInstance(
                "<Project><PropertyGroup><MSBuildStrictMode>warn</MSBuildStrictMode></PropertyGroup></Project>");

            StrictTargetCache.GetMode(proj).ToString().ShouldBe("Enforce");
        }

        [Fact]
        public void GetMode_EnvVarReadPerCall_NotCached()
        {
            ProjectInstance proj = MakeInstance("<Project />");

            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "warn");
            StrictTargetCache.GetMode(proj).ToString().ShouldBe("Warn");

            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", null);
            StrictTargetCache.GetMode(proj).ToString().ShouldBe("Off");

            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "enforce");
            StrictTargetCache.GetMode(proj).ToString().ShouldBe("Enforce");
        }

        [Theory]
        [InlineData("CoreCompile", "CoreCompile", true)]
        [InlineData("CoreCompile;Pack", "Pack", true)]
        [InlineData("Foo, Bar , Baz", "bar", true)] // commas and whitespace tolerated; case-insensitive
        [InlineData("CoreCompile", "Build", false)]
        [InlineData("", "CoreCompile", false)]
        public void IsTargetExempt_HonorsList(string list, string target, bool expected)
        {
            ProjectInstance proj = MakeInstance(
                $"<Project><PropertyGroup><MSBuildStrictExemptTargets>{list}</MSBuildStrictExemptTargets></PropertyGroup></Project>");
            StrictTargetCache.IsTargetExempt(proj, target).ShouldBe(expected);
        }

        [Fact]
        public void IsTargetExempt_NullInputs_ReturnsFalse()
        {
            StrictTargetCache.IsTargetExempt(null, "X").ShouldBeFalse();
            ProjectInstance proj = MakeInstance("<Project />");
            StrictTargetCache.IsTargetExempt(proj, null).ShouldBeFalse();
            StrictTargetCache.IsTargetExempt(proj, "").ShouldBeFalse();
        }

        // ---------------------------------------------------------------------
        // End-to-end: cache HIT, MISS, and disabled-by-default behavior
        // ---------------------------------------------------------------------

        /// <summary>
        /// With strict mode ON: build a project whose only target declares Inputs/Outputs.
        /// First build = MISS (target body runs). Second build = HIT (target skipped, body does not run).
        /// </summary>
        [Fact]
        public void E2E_SecondBuild_HitsCache_AndSkipsTargetBody()
        {
            var (projectPath, inputPath, outputPath) = WriteDemoProject(strictMode: "warn", writeUndeclared: false);

            // Build #1: cache miss, target runs, output produced.
            MockLogger logger1 = BuildOnce(projectPath);
            logger1.AssertLogContains("INSIDE_TARGET_BODY");
            File.Exists(outputPath).ShouldBeTrue();

            // Build #2: identical inputs -> cache hit -> target body NOT executed this build.
            // Delete the output first so we can also assert it was *restored* from cache.
            File.Delete(outputPath);
            MockLogger logger2 = BuildOnce(projectPath);
            logger2.AssertLogDoesntContain("INSIDE_TARGET_BODY");
            File.Exists(outputPath).ShouldBeTrue("strict-mode cache HIT should restore declared outputs");
        }

        /// <summary>
        /// With strict mode OFF (default), the strict cache must NOT engage:
        /// the target body must run on every build regardless of input changes.
        /// </summary>
        [Fact]
        public void E2E_StrictModeOff_IsNoop()
        {
            var (projectPath, _, outputPath) = WriteDemoProject(strictMode: null, writeUndeclared: false);

            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
            File.Delete(outputPath);
            // Without strict mode the second build also runs the body (because we deleted the output, the
            // built-in timestamp check correctly schedules it). Even if not, the body would still run
            // because strict-mode caching is off. Either way: no INSIDE_TARGET_BODY suppression.
            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
        }

        /// <summary>
        /// Enforce mode: a target that writes a file it did NOT declare in Outputs must FAIL the build
        /// with MSBSTRICT001.
        /// </summary>
        [Fact]
        public void E2E_EnforceMode_UndeclaredWrite_FailsTheBuild()
        {
            var (projectPath, _, _) = WriteDemoProject(strictMode: "enforce", writeUndeclared: true);

            MockLogger logger = BuildOnce(projectPath, expectSuccess: false);
            logger.AssertLogContains("MSBSTRICT001");
        }

        /// <summary>
        /// Warn mode: same project as above (undeclared write) must still succeed but log a warning
        /// mentioning MSBSTRICT001.
        /// </summary>
        [Fact]
        public void E2E_WarnMode_UndeclaredWrite_WarnsButSucceeds()
        {
            var (projectPath, _, _) = WriteDemoProject(strictMode: "warn", writeUndeclared: true);

            MockLogger logger = BuildOnce(projectPath, expectSuccess: true);
            logger.AssertLogContains("MSBSTRICT001");
        }

        /// <summary>
        /// An exempted target must behave exactly as if strict mode were off for that target:
        /// the strict cache does not engage, so the body runs on every build.
        /// </summary>
        [Fact]
        public void E2E_ExemptTarget_BypassesStrictCache()
        {
            var (projectPath, _, outputPath) = WriteDemoProject(
                strictMode: "warn",
                writeUndeclared: false,
                exemptTargets: "DoWork");

            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
            File.Delete(outputPath);
            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
        }

        [Fact]
        public void E2E_AllowListedEnvironmentVariableChange_InvalidatesCache()
        {
            const string envVarName = "STRICT_TARGET_CACHE_ENV";
            _env.SetEnvironmentVariable(envVarName, "one");

            var (projectPath, _, outputPath) = WriteDemoProject(
                strictMode: "warn",
                writeUndeclared: false,
                cacheKeyEnvVars: envVarName,
                consumedEnvironmentVariable: envVarName);

            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
            File.ReadAllText(outputPath).Trim().ShouldBe("one");

            File.Delete(outputPath);
            _env.SetEnvironmentVariable(envVarName, "two");

            MockLogger logger = BuildOnce(projectPath);
            logger.AssertLogContains("INSIDE_TARGET_BODY");
            File.ReadAllText(outputPath).Trim().ShouldBe("two");
        }

        [Fact]
        public void E2E_NonListedEnvironmentVariableChange_DoesNotInvalidateCache()
        {
            const string envVarName = "STRICT_TARGET_CACHE_ENV_IGNORE";
            _env.SetEnvironmentVariable(envVarName, "one");

            var (projectPath, _, outputPath) = WriteDemoProject(strictMode: "warn", writeUndeclared: false);

            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");

            File.Delete(outputPath);
            _env.SetEnvironmentVariable(envVarName, "two");

            MockLogger logger = BuildOnce(projectPath);
            logger.AssertLogDoesntContain("INSIDE_TARGET_BODY");
            File.Exists(outputPath).ShouldBeTrue();
        }

        [Fact]
        public void E2E_VolatileAllowListedEnvironmentVariableChange_DoesNotInvalidateCache()
        {
            const string envVarName = "DOTNET_CLI_TELEMETRY_SESSIONID";
            _env.SetEnvironmentVariable(envVarName, "one");

            var (projectPath, _, outputPath) = WriteDemoProject(
                strictMode: "warn",
                writeUndeclared: false,
                cacheKeyEnvVars: "DOTNET_*",
                consumedEnvironmentVariable: "DOTNET_ROOT");

            BuildOnce(projectPath).AssertLogContains("INSIDE_TARGET_BODY");
            File.ReadAllText(outputPath).Trim().ShouldBe(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty);

            File.Delete(outputPath);
            _env.SetEnvironmentVariable(envVarName, "two");

            MockLogger logger = BuildOnce(projectPath);
            logger.AssertLogDoesntContain("INSIDE_TARGET_BODY");
            File.Exists(outputPath).ShouldBeTrue();
            File.ReadAllText(outputPath).Trim().ShouldBe(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private ProjectInstance MakeInstance(string xml)
        {
            using var stringReader = new StringReader(xml);
            using var xmlReader = System.Xml.XmlReader.Create(stringReader);
            return new ProjectInstance(Microsoft.Build.Construction.ProjectRootElement.Create(xmlReader));
        }

        private (string projectPath, string inputPath, string outputPath) WriteDemoProject(
            string strictMode,
            bool writeUndeclared,
            string exemptTargets = null,
            string cacheKeyEnvVars = null,
            string consumedEnvironmentVariable = null)
        {
            var folder = _env.CreateFolder().Path;
            string inputPath = Path.Combine(folder, "input.txt");
            string outputPath = Path.Combine(folder, "obj", "declared.out");
            string undeclaredPath = Path.Combine(folder, "obj", "undeclared.out");
            File.WriteAllText(inputPath, "hello-strict-mode");

            string modeProp = strictMode is null
                ? string.Empty
                : $"<MSBuildStrictMode>{strictMode}</MSBuildStrictMode>";
            string exemptProp = exemptTargets is null
                ? string.Empty
                : $"<MSBuildStrictExemptTargets>{exemptTargets}</MSBuildStrictExemptTargets>";
            string cacheKeyEnvVarsProp = cacheKeyEnvVars is null
                ? string.Empty
                : $"<StrictModeCacheKeyEnvVars>{cacheKeyEnvVars}</StrictModeCacheKeyEnvVars>";
            string consumedEnvProp = consumedEnvironmentVariable is null
                ? string.Empty
                : $"<ConsumedEnvironmentValue>$([System.Environment]::GetEnvironmentVariable('{consumedEnvironmentVariable}'))</ConsumedEnvironmentValue>";
            string outputLines = consumedEnvironmentVariable is null
                ? "produced"
                : "$(ConsumedEnvironmentValue)";

            // The body writes the declared output unconditionally and, optionally, an undeclared file
            // in the same intermediate dir (which strict mode should observe via the obj/ snapshot).
            string extraWrite = writeUndeclared
                ? $@"<WriteLinesToFile File=""{undeclaredPath}"" Lines=""sneaky"" Overwrite=""true"" />"
                : string.Empty;

            string content = $@"<Project DefaultTargets=""DoWork"">
  <PropertyGroup>
    <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
    <IntermediateOutputPath>obj\</IntermediateOutputPath>
    {modeProp}
    {exemptProp}
    {cacheKeyEnvVarsProp}
    {consumedEnvProp}
  </PropertyGroup>
  <Target Name=""DoWork"" Inputs=""{inputPath}"" Outputs=""{outputPath}"">
    <MakeDir Directories=""obj"" />
    <Message Importance=""High"" Text=""INSIDE_TARGET_BODY"" />
    <WriteLinesToFile File=""{outputPath}"" Lines=""{outputLines}"" Overwrite=""true"" />
    {extraWrite}
  </Target>
</Project>";

            string projectPath = Path.Combine(folder, "demo.proj");
            File.WriteAllText(projectPath, content);
            return (projectPath, inputPath, outputPath);
        }

        private MockLogger BuildOnce(string projectPath, bool expectSuccess = true)
        {
            using var session = new Helpers.BuildManagerSession(_env);
            BuildResult result = session.BuildProjectFile(projectPath);
            if (expectSuccess)
            {
                result.OverallResult.ShouldBe(BuildResultCode.Success);
            }
            else
            {
                result.OverallResult.ShouldBe(BuildResultCode.Failure);
            }
            return session.Logger;
        }
    }
}
