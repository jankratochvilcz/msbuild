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

    [Fact]
    public void Strict_RecordSuccess_TracksStaticCustomOutputPathAsOutputs()
    {
        var (projectPath, _, generatedPath) = CreateProject(outputPath: "artifacts-output");
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, "assembly-bytes");
        StrictSolutionFastSkip.RecordSuccess(projectPath, ["Build"], globals, snapshot);

        ManifestData manifest = ReadManifest(projectPath);
        manifest.Outputs.ShouldContain(generatedPath);
        manifest.Inputs.ShouldNotContain(generatedPath);
    }

    [Fact]
    public void Strict_TryFastSkip_DetectsDeletedStaticCustomOutputPathOutput()
    {
        var (projectPath, _, generatedPath) = CreateProject(outputPath: "artifacts-output");
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[] { "Build" };
        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, "assembly-bytes");
        StrictSolutionFastSkip.RecordSuccess(projectPath, targets, globals, snapshot);

        File.Delete(generatedPath);

        StrictSolutionFastSkip.TryFastSkip(projectPath, targets, globals, out string reason).ShouldBeFalse();
        reason.ShouldStartWith("output-missing-or-changed ");
    }

    [Fact]
    public void Strict_TryFastSkip_DetectsDeletedArtifactsOutputPathOutput()
    {
        var (projectPath, _, generatedPath) = CreateProject(useArtifactsOutput: true, artifactsPath: Path.Combine("..", "repo-artifacts"));
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[] { "Build" };
        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, "assembly-bytes");
        StrictSolutionFastSkip.RecordSuccess(projectPath, targets, globals, snapshot);

        File.Delete(generatedPath);

        StrictSolutionFastSkip.TryFastSkip(projectPath, targets, globals, out string reason).ShouldBeFalse();
        reason.ShouldStartWith("output-missing-or-changed ");
    }

    [Fact]
    public void Strict_CapturePreBuildInputSnapshot_DoesNotSkipConditionedOutputPathDirectories()
    {
        var (projectPath, _, _) = CreateProject();
        string root = Path.GetDirectoryName(projectPath)!;
        string checkedInFile = Path.Combine(root, "artifacts-output", "CheckedIn.cs");

        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)'=='Release'\"><OutputPath>artifacts-output</OutputPath></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.GetDirectoryName(checkedInFile)!);
        File.WriteAllText(checkedInFile, "class CheckedIn {}\n");

        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);

        snapshot!.Inputs.ContainsKey(checkedInFile).ShouldBeTrue();
    }

    [Fact]
    public void Strict_TryFastSkip_HonorsArtifactsOutputConfiguredAcrossImportedFiles()
    {
        var (projectPath, _, _) = CreateProject();
        string root = Path.GetDirectoryName(projectPath)!;
        string generatedPath = Path.GetFullPath(Path.Combine(root, "..", "repo-artifacts", "bin", "demo", "debug", "demo.dll"));
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[] { "Build" };

        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><PropertyGroup><UseArtifactsOutput>\n  true\n</UseArtifactsOutput></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), $"<Project><PropertyGroup><ArtifactsPath>{Path.Combine("..", "repo-artifacts")}</ArtifactsPath></PropertyGroup></Project>");

        var snapshot = StrictSolutionFastSkip.CapturePreBuildInputSnapshot(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, "assembly-bytes");
        StrictSolutionFastSkip.RecordSuccess(projectPath, targets, globals, snapshot);

        File.Delete(generatedPath);

        StrictSolutionFastSkip.TryFastSkip(projectPath, targets, globals, out string reason).ShouldBeFalse();
        reason.ShouldStartWith("output-missing-or-changed ");
    }

    private (string projectPath, string sourcePath, string generatedPath) CreateProject(string? outputPath = null, bool useArtifactsOutput = false, string? artifactsPath = null)
    {
        string root = _env.CreateFolder().Path;
        string projectPath = Path.Combine(root, "demo.csproj");
        string sourcePath = Path.Combine(root, "Program.cs");
        string generatedPath = useArtifactsOutput
            ? Path.GetFullPath(Path.Combine(root, artifactsPath ?? "artifacts", "bin", "demo", "debug", "demo.dll"))
            : outputPath is null
                ? Path.Combine(root, "Generated.g.cs")
                : Path.Combine(root, outputPath, "demo.dll");
        string outputPathProperty = outputPath is null
            ? string.Empty
            : $"<OutputPath>{outputPath}</OutputPath>";
        string artifactsPathProperty = artifactsPath is null
            ? string.Empty
            : $"<ArtifactsPath>{artifactsPath}</ArtifactsPath>";
        string useArtifactsOutputProperty = useArtifactsOutput ? "<UseArtifactsOutput>true</UseArtifactsOutput>" : string.Empty;

        File.WriteAllText(projectPath, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{outputPathProperty}{artifactsPathProperty}{useArtifactsOutputProperty}</PropertyGroup></Project>");
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
