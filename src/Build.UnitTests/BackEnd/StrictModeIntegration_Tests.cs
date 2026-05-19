// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;

using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    public sealed class StrictModeIntegration_Tests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly TestEnvironment _env;

        public StrictModeIntegration_Tests(ITestOutputHelper output)
        {
            _output = output;
            _env = TestEnvironment.Create(_output);
            _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "1");
            _env.SetEnvironmentVariable("MSBUILDSTRICTNOPROJECTCACHE", null);
            _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", null);
            StrictProjectCache.ResetCachesForTest();
        }

        public void Dispose()
        {
            StrictProjectCache.ResetCachesForTest();
            _env.Dispose();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BuildManagerSecondBuildHitsProjectCacheWithinSameSession(bool disableInProcNode)
        {
            if (disableInProcNode)
            {
                _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");
            }

            var (projectPath, outputPath, messageText) = WriteStrictProject("same-session");
            using var session = new Helpers.BuildManagerSession(_env, CreateBuildParameters(disableInProcNode));

            BuildResult first = session.BuildProjectFile(projectPath, ["Build"]);
            first.OverallResult.ShouldBe(BuildResultCode.Success);
            File.Exists(outputPath).ShouldBeTrue();
            session.Logger.AssertLogContains(messageText);

            BuildResult second = session.BuildProjectFile(projectPath, ["Build"]);
            second.OverallResult.ShouldBe(BuildResultCode.Success);
            CountOccurrences(session.Logger.FullLog, messageText).ShouldBe(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BuildManagerSecondBuildHitsProjectCacheAfterRestart(bool disableInProcNode)
        {
            if (disableInProcNode)
            {
                _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");
            }

            var (projectPath, outputPath, messageText) = WriteStrictProject("restart");
            using (var firstSession = new Helpers.BuildManagerSession(_env, CreateBuildParameters(disableInProcNode)))
            {
                BuildResult first = firstSession.BuildProjectFile(projectPath, ["Build"]);
                first.OverallResult.ShouldBe(BuildResultCode.Success);
                File.Exists(outputPath).ShouldBeTrue();
                firstSession.Logger.AssertLogContains(messageText);
            }

            using var secondSession = new Helpers.BuildManagerSession(_env, CreateBuildParameters(disableInProcNode));
            BuildResult second = secondSession.BuildProjectFile(projectPath, ["Build"]);
            second.OverallResult.ShouldBe(BuildResultCode.Success);
            secondSession.Logger.AssertLogDoesntContain(messageText);
        }

        [Fact]
        public async Task ConcurrentManifestStoresLeaveAReadableManifest()
        {
            var folder = _env.CreateFolder().Path;
            string projectPath = Path.Combine(folder, "stress.proj");
            string outputPath = Path.Combine(folder, "obj", "stress.out");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(projectPath, "<Project DefaultTargets=\"Build\"><PropertyGroup><MSBuildStrictMode>warn</MSBuildStrictMode></PropertyGroup></Project>");
            File.WriteAllText(outputPath, "stress");

            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = "Debug",
            };
            string[] targets = ["Build"];

            StrictProjectCache.TryHit(projectPath, globals, targets, BuildRequestDataFlags.None, out string cacheKey, out _).ShouldBeNull();
            BuildResult result = MakeSuccessResult(("Build", [(outputPath, Array.Empty<(string, string)>())]));

            await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(_ => Task.Run(() => StrictProjectCache.MaybeStoreResult(projectPath, globals, targets, cacheKey, result))));

            StrictProjectCache.TryHit(projectPath, globals, targets, BuildRequestDataFlags.None, out _, out string reason).ShouldNotBeNull();
            reason.ShouldBe("hit");
        }

        private static BuildParameters CreateBuildParameters(bool disableInProcNode)
        {
            return new BuildParameters
            {
                DisableInProcNode = disableInProcNode,
                MaxNodeCount = disableInProcNode ? 2 : 1,
                EnableNodeReuse = false,
                ShutdownInProcNodeOnBuildFinish = true,
            };
        }

        private (string projectPath, string outputPath, string messageText) WriteStrictProject(string name)
        {
            string folder = _env.CreateFolder().Path;
            string outputPath = Path.Combine(folder, "obj", name + ".out");
            string messageText = "STRICT_INTEGRATION_" + name.ToUpperInvariant();
            string projectPath = Path.Combine(folder, name + ".proj");

            string projectContents = $$"""
                <Project DefaultTargets="Build">
                  <PropertyGroup>
                    <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
                    <IntermediateOutputPath>obj\</IntermediateOutputPath>
                    <MSBuildStrictMode>warn</MSBuildStrictMode>
                  </PropertyGroup>
                  <Target Name="Build" Inputs="{{projectPath}}" Outputs="{{outputPath}}">
                    <MakeDir Directories="obj" />
                    <Message Importance="High" Text="{{messageText}}" />
                    <WriteLinesToFile File="{{outputPath}}" Lines="{{name}}" Overwrite="true" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(projectPath, projectContents);
            return (projectPath, outputPath, messageText);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
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
    }
}
