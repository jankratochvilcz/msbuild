// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Build.Strict;
using Microsoft.Build.UnitTests.Shared;

using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public sealed class StrictSolutionFastSkip_Tests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TestEnvironment _env;

    public StrictSolutionFastSkip_Tests(ITestOutputHelper output)
    {
        _output = output;
        _env = TestEnvironment.Create(_output);
        _env.SetEnvironmentVariable("MSBUILDSTRICTMODE", "warn");
        _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableSolutionFastSkip, null);
    }

    public void Dispose() => _env.Dispose();

    [Fact]
    public void RecordSuccess_ReclassifiesBuildWrittenInputLikeFilesAsOutputs()
    {
        var (projectPath, sourcePath, generatedPath) = CreateProject();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        File.WriteAllText(generatedPath, "// generated\nclass Generated {}\n");
        StrictSolutionFastSkip.RecordSuccess(projectPath, ["Build"], globals, snapshot);

        ManifestData manifest = ReadManifest(projectPath);
        manifest.Inputs.ShouldContain(sourcePath);
        manifest.Inputs.ShouldNotContain(generatedPath);
        manifest.Outputs.ShouldContain(generatedPath);
    }

    [Fact]
    public void TryFastSkip_TreatsBuildWrittenInputLikeFilesAsOutputs()
    {
        var (projectPath, _, generatedPath) = CreateProject();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[] { "Build" };
        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        File.WriteAllText(generatedPath, "// generated\nclass Generated {}\n");
        StrictSolutionFastSkip.RecordSuccess(projectPath, targets, globals, snapshot);

        File.Delete(generatedPath);

        StrictSolutionFastSkip.TryFastSkip(projectPath, targets, globals, out string reason).ShouldBeFalse();
        reason.ShouldStartWith("output-missing-or-changed ");
    }

    private (string projectPath, string sourcePath, string generatedPath) CreateProject()
    {
        string root = _env.CreateFolder().Path;
        string projectPath = Path.Combine(root, "demo.csproj");
        string sourcePath = Path.Combine(root, "Program.cs");
        string generatedPath = Path.Combine(root, "Generated.g.cs");

        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(sourcePath, "class Program {}\n");
        return (projectPath, sourcePath, generatedPath);
    }

    private static ManifestData ReadManifest(string projectPath)
    {
        string cacheDir = Path.Combine(Path.GetDirectoryName(projectPath)!, ".strict-fastskip");
        string manifestPath = Directory.GetFiles(cacheDir, "*.manifest", SearchOption.TopDirectoryOnly).Single();

        using var stream = File.OpenRead(manifestPath);
        using var reader = new BinaryReader(stream);
        reader.ReadInt32();
        reader.ReadString();
        reader.ReadInt64();

        HashSet<string> inputs = ReadStatDictionaryKeys(reader);
        HashSet<string> outputs = ReadStatDictionaryKeys(reader);
        _ = ReadDirectoryDictionary(reader);

        return new ManifestData(inputs, outputs);
    }

    private static HashSet<string> ReadStatDictionaryKeys(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            result.Add(reader.ReadString());
            reader.ReadInt64();
            reader.ReadInt64();
        }

        return result;
    }

    private static Dictionary<string, long> ReadDirectoryDictionary(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        Dictionary<string, long> result = new(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            result[reader.ReadString()] = reader.ReadInt64();
        }

        return result;
    }

    private sealed record ManifestData(HashSet<string> Inputs, HashSet<string> Outputs);
}
