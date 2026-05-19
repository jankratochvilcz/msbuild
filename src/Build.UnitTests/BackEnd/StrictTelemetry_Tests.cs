// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Strict;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.BackEnd
{
    public class StrictTelemetry_Tests : IDisposable
    {
        private const string EnvFile = "MSBUILDSTRICTTELEMETRYFILE";
        private const string EnvIter = "MSBUILDSTRICTTELEMETRYITER";
        private readonly string? _originalFile;
        private readonly string? _originalIter;

        public StrictTelemetry_Tests()
        {
            _originalFile = Environment.GetEnvironmentVariable(EnvFile);
            _originalIter = Environment.GetEnvironmentVariable(EnvIter);
            Environment.SetEnvironmentVariable(EnvFile, null);
            Environment.SetEnvironmentVariable(EnvIter, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvFile, _originalFile);
            Environment.SetEnvironmentVariable(EnvIter, _originalIter);
        }

        [Fact]
        public void IsEnabled_RereadsEnvVar_TurnsOnAfterStartup()
        {
            StrictTelemetry.IsEnabled.ShouldBeFalse();

            string path = Path.Combine(Path.GetTempPath(), $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            try
            {
                Environment.SetEnvironmentVariable(EnvFile, path);
                StrictTelemetry.IsEnabled.ShouldBeTrue();

                Environment.SetEnvironmentVariable(EnvFile, null);
                StrictTelemetry.IsEnabled.ShouldBeFalse();
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void Emit_PicksUpEnvVarChangeBetweenCalls()
        {
            string path = Path.Combine(Path.GetTempPath(), $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            try
            {
                Environment.SetEnvironmentVariable(EnvFile, path);
                StrictTelemetry.Emit(layer: "test-layer", outcome: "hit", project: "alpha");

                File.Exists(path).ShouldBeTrue();
                string content = File.ReadAllText(path);
                content.ShouldContain("\"layer\":\"test-layer\"");
                content.ShouldContain("\"project\":\"alpha\"");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void Emit_HonoursIterChange_WithinSameProcess()
        {
            string path = Path.Combine(Path.GetTempPath(), $"strict-telemetry-{Guid.NewGuid():N}.jsonl");
            try
            {
                Environment.SetEnvironmentVariable(EnvFile, path);

                Environment.SetEnvironmentVariable(EnvIter, "1");
                StrictTelemetry.Emit(layer: "L", outcome: "miss");

                Environment.SetEnvironmentVariable(EnvIter, "2");
                StrictTelemetry.Emit(layer: "L", outcome: "miss");

                string[] lines = File.ReadAllLines(path);
                lines.Length.ShouldBe(2);
                lines[0].ShouldContain("\"iteration\":1");
                lines[1].ShouldContain("\"iteration\":2");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
