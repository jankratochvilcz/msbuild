// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Strict;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.BackEnd
{
    public sealed class StrictTelemetry_Tests : IDisposable
    {
        private const string EnvFile = "MSBUILDSTRICTTELEMETRYFILE";
        private const string EnvIter = "MSBUILDSTRICTTELEMETRYITER";

        private readonly ITestOutputHelper _output;
        private readonly TestEnvironment _env;

        public StrictTelemetry_Tests(ITestOutputHelper output)
        {
            _output = output;
            _env = TestEnvironment.Create(_output);
            _env.SetEnvironmentVariable(EnvFile, null);
            _env.SetEnvironmentVariable(EnvIter, null);
            StrictTelemetry.ResetForTests();
        }

        public void Dispose()
        {
            StrictTelemetry.ResetForTests();
            _env.Dispose();
        }

        [Fact]
        public void IsEnabled_RereadsEnvVar_TurnsOnAfterStartup()
        {
            StrictTelemetry.IsEnabled.ShouldBeFalse();

            string path = Path.Combine(_env.CreateFolder().Path, $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            _env.SetEnvironmentVariable(EnvFile, path);
            StrictTelemetry.IsEnabled.ShouldBeTrue();

            _env.SetEnvironmentVariable(EnvFile, null);
            StrictTelemetry.IsEnabled.ShouldBeFalse();
        }

        [Fact]
        public void Emit_PicksUpEnvVarChangeBetweenCalls()
        {
            string path = Path.Combine(_env.CreateFolder().Path, $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            _env.SetEnvironmentVariable(EnvFile, path);
            StrictTelemetry.Emit(layer: "test-layer", outcome: "hit", project: "alpha");

            File.Exists(path).ShouldBeTrue();
            string content = File.ReadAllText(path);
            content.ShouldContain("\"layer\":\"test-layer\"");
            content.ShouldContain("\"project\":\"alpha\"");
        }

        [Fact]
        public void Emit_HonoursIterChange_WithinSameProcess()
        {
            string path = Path.Combine(_env.CreateFolder().Path, $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            _env.SetEnvironmentVariable(EnvFile, path);

            _env.SetEnvironmentVariable(EnvIter, "1");
            StrictTelemetry.Emit(layer: "L", outcome: "miss");

            _env.SetEnvironmentVariable(EnvIter, "2");
            StrictTelemetry.Emit(layer: "L", outcome: "miss");

            string[] lines = File.ReadAllLines(path);
            lines.Length.ShouldBe(2);
            lines[0].ShouldContain("\"iteration\":1");
            lines[1].ShouldContain("\"iteration\":2");
        }

        [Fact]
        public void Emit_WritesReasonCode_AndFlushesPerReasonSummary()
        {
            string path = Path.Combine(_env.CreateFolder().Path, $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            _env.SetEnvironmentVariable(EnvFile, path);

            StrictTelemetry.Emit(layer: "project-fastskip", outcome: "miss", reason: "no-manifest");
            StrictTelemetry.Emit(layer: "project-fastskip", outcome: "miss", reason: "no-manifest");
            StrictTelemetry.Emit(layer: "project-fastskip", outcome: "hit", reason: "hit");
            StrictTelemetry.FlushSummaryForTests();

            string[] lines = File.ReadAllLines(path);
            lines.Length.ShouldBe(6);
            lines[0].ShouldContain("\"reason_code\":\"NoManifest\"");
            lines[1].ShouldContain("\"reason_code\":\"NoManifest\"");
            lines[2].ShouldNotContain("\"reason_code\"");
            lines.ShouldContain(line => line.Contains("\"summary_kind\":\"outcome\"") && line.Contains("\"summary_outcome\":\"miss\"") && line.Contains("\"count\":2"));
            lines.ShouldContain(line => line.Contains("\"summary_kind\":\"reason\"") && line.Contains("\"summary_outcome\":\"miss\"") && line.Contains("\"reason_code\":\"NoManifest\"") && line.Contains("\"count\":2"));
        }

        [Fact]
        public void DiagnosticsSetting_UsesBooleanPropertyTruthTable()
        {
            StrictTelemetry.IsDiagnosticsEnabled(null).ShouldBeFalse();
            StrictTelemetry.IsDiagnosticsEnabled("true").ShouldBeTrue();
            StrictTelemetry.IsDiagnosticsEnabled("  yes  ").ShouldBeTrue();
            StrictTelemetry.IsDiagnosticsEnabled("off").ShouldBeFalse();
            StrictTelemetry.IsDiagnosticsEnabled("garbage").ShouldBeFalse();
        }

        [Fact]
        public void FormatDiagnosticMessage_IncludesStableReasonCode()
        {
            string message = StrictTelemetry.FormatDiagnosticMessage(
                layer: "project-fastskip",
                outcome: "miss",
                reason: "dependent-no-manifest:abc123",
                project: "demo.csproj",
                target: "Build",
                cacheKey: "abcdef");

            message.ShouldContain("[strict-diagnostics]");
            message.ShouldContain("layer=\"project-fastskip\"");
            message.ShouldContain("outcome=\"miss\"");
            message.ShouldContain("reason_code=\"DependentNoManifest\"");
            message.ShouldContain("reason=\"dependent-no-manifest:abc123\"");
            message.ShouldContain("project=\"demo.csproj\"");
            message.ShouldContain("target=\"Build\"");
            message.ShouldContain("cache_key=\"abcdef\"");
        }

        [Fact]
        public void FormatDiagnosticMessage_EscapesControlCharacters()
        {
            string message = StrictTelemetry.FormatDiagnosticMessage(
                layer: "target-cache",
                outcome: "error",
                reason: "exception: IOException line1\r\nline2\tquoted\"text\"");

            message.ShouldContain("reason=\"exception: IOException line1\\r\\nline2\\tquoted\\\"text\\\"\"");
            message.ShouldNotContain("\r\nline2");
        }
    }
}
