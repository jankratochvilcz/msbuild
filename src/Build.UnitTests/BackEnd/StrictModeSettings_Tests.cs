// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Strict;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    /// <summary>
    /// Unit tests for the consolidated <see cref="StrictModeSettings"/> opt-in parser. Every
    /// strict-mode layer (project cache, target cache, solution fast-skip, up-to-date checker)
    /// is required to route through this helper so that a single <c>MSBUILDSTRICTMODE</c> value
    /// enables (or disables) every layer consistently.
    /// </summary>
    public sealed class StrictModeSettings_Tests : IDisposable
    {
        private readonly TestEnvironment _env;

        public StrictModeSettings_Tests(ITestOutputHelper output)
        {
            _env = TestEnvironment.Create(output);
            // Defensive: make sure no inherited env state leaks into these tests.
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, null);
            _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableProjectCache, null);
            _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableSolutionFastSkip, null);
            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, null);
        }

        public void Dispose() => _env.Dispose();

        // ---------------------------------------------------------------------
        // ParseLevel: explicit truth table
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData(null, "Off")]
        [InlineData("", "Off")]
        [InlineData("0", "Off")]
        [InlineData("false", "Off")]
        [InlineData("FALSE", "Off")]
        [InlineData("off", "Off")]
        [InlineData("no", "Off")]
        [InlineData("1", "Warn")]
        [InlineData("true", "Warn")]
        [InlineData("True", "Warn")]
        [InlineData("on", "Warn")]
        [InlineData("yes", "Warn")]
        [InlineData("warn", "Warn")]
        [InlineData("WARN", "Warn")]
        [InlineData("enforce", "Enforce")]
        [InlineData("Enforce", "Enforce")]
        [InlineData("strict", "Enforce")]
        [InlineData("error", "Enforce")]
        // Unknown values are NOT silently treated as "enabled".
        [InlineData("nope", "Off")]
        [InlineData("garbage", "Off")]
        [InlineData("2", "Off")]
        public void ParseLevel_HonorsCanonicalTruthTable(string value, string expected)
        {
            StrictModeSettings.ParseLevel(value).ToString().ShouldBe(expected);
        }

        // ---------------------------------------------------------------------
        // ResolveLevel: env var precedence over project property
        // ---------------------------------------------------------------------

        [Fact]
        public void ResolveLevel_DefaultsToOff_WhenNeitherSet()
        {
            StrictModeSettings.ResolveLevel(projectPropertyValue: null).ToString().ShouldBe("Off");
            StrictModeSettings.ResolveLevel(projectPropertyValue: "").ToString().ShouldBe("Off");
        }

        [Fact]
        public void ResolveLevel_UsesProjectProperty_WhenEnvVarUnset()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, null);

            StrictModeSettings.ResolveLevel("warn").ToString().ShouldBe("Warn");
            StrictModeSettings.ResolveLevel("enforce").ToString().ShouldBe("Enforce");
            StrictModeSettings.ResolveLevel("off").ToString().ShouldBe("Off");
        }

        [Fact]
        public void ResolveLevel_EnvVarTakesPrecedenceOverProperty()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "enforce");
            StrictModeSettings.ResolveLevel(projectPropertyValue: "warn").ToString().ShouldBe("Enforce");

            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "warn");
            StrictModeSettings.ResolveLevel(projectPropertyValue: "enforce").ToString().ShouldBe("Warn");
        }

        [Fact]
        public void ResolveLevel_EnvVarExplicitOff_FallsBackToProperty()
        {
            // An explicit "off" env value should not mask a property-level opt-in; the property
            // wins because the env layer effectively expressed "no opinion / off".
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "off");
            StrictModeSettings.ResolveLevel(projectPropertyValue: "warn").ToString().ShouldBe("Warn");
        }

        [Fact]
        public void ResolveLevel_RereadsEnvVar_OnEveryCall()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "warn");
            StrictModeSettings.ResolveLevel().ToString().ShouldBe("Warn");

            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, null);
            StrictModeSettings.ResolveLevel().ToString().ShouldBe("Off");

            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "enforce");
            StrictModeSettings.ResolveLevel().ToString().ShouldBe("Enforce");
        }

        // ---------------------------------------------------------------------
        // IsLayerEnabled: per-layer disable gate
        // ---------------------------------------------------------------------

        [Fact]
        public void IsLayerEnabled_FalseWhenLevelOff()
        {
            StrictModeSettings.IsLayerEnabled(
                projectPropertyValue: null,
                layerDisableEnvVar: StrictModeSettings.EnvDisableProjectCache).ShouldBeFalse();
        }

        [Fact]
        public void IsLayerEnabled_TrueWhenLevelOnAndLayerNotDisabled()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "1");
            StrictModeSettings.IsLayerEnabled(
                projectPropertyValue: null,
                layerDisableEnvVar: StrictModeSettings.EnvDisableProjectCache).ShouldBeTrue();
        }

        [Fact]
        public void IsLayerEnabled_FalseWhenLayerDisableEnvVarSet()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "1");
            _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableProjectCache, "1");
            StrictModeSettings.IsLayerEnabled(
                projectPropertyValue: null,
                layerDisableEnvVar: StrictModeSettings.EnvDisableProjectCache).ShouldBeFalse();
        }

        [Fact]
        public void IsLayerEnabled_DisableGatesAreIndependentPerLayer()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, "1");
            _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableProjectCache, "1");
            _env.SetEnvironmentVariable(StrictModeSettings.EnvDisableSolutionFastSkip, null);

            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableProjectCache).ShouldBeFalse();
            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableSolutionFastSkip).ShouldBeTrue();
        }

        // ---------------------------------------------------------------------
        // Cross-layer consistency: one MSBUILDSTRICTMODE value enables every layer
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("on")]
        [InlineData("warn")]
        [InlineData("enforce")]
        [InlineData("strict")]
        public void OneCanonicalEnvValue_EnablesEveryLayer(string envValue)
        {
            // This guards against the original bug: MSBUILDSTRICTMODE=true enabled the target
            // cache but the up-to-date checker required literal "1"; MSBUILDSTRICTMODE=enforce
            // was silently ignored by the project-cache layer's truthy/falsy parse. After the
            // consolidation, every truthy value enables every layer.
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, envValue);

            StrictModeSettings.ResolveLevel().ToString().ShouldNotBe("Off");
            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableProjectCache).ShouldBeTrue();
            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableSolutionFastSkip).ShouldBeTrue();
        }

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("off")]
        [InlineData("no")]
        public void OneCanonicalEnvValue_DisablesEveryLayer(string envValue)
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvVarName, envValue);

            StrictModeSettings.ResolveLevel().ToString().ShouldBe("Off");
            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableProjectCache).ShouldBeFalse();
            StrictModeSettings.IsLayerEnabled(null, StrictModeSettings.EnvDisableSolutionFastSkip).ShouldBeFalse();
        }

        // ---------------------------------------------------------------------
        // GetExtraInputExtensions: MSBUILDSTRICTEXTRAINPUTEXTENSIONS escape hatch
        // ---------------------------------------------------------------------

        [Fact]
        public void GetExtraInputExtensions_EmptyWhenEnvVarUnset()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, null);
            StrictModeSettings.GetExtraInputExtensions().Count.ShouldBe(0);

            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, "");
            StrictModeSettings.GetExtraInputExtensions().Count.ShouldBe(0);
        }

        [Fact]
        public void GetExtraInputExtensions_ParsesSemicolonAndCommaSeparatedValues()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, ".tt;proto,.tsx; .yaml ");
            var ext = StrictModeSettings.GetExtraInputExtensions();
            ext.ShouldContain(".tt");
            ext.ShouldContain(".proto");
            ext.ShouldContain(".tsx");
            ext.ShouldContain(".yaml");
            ext.Count.ShouldBe(4);
        }

        [Fact]
        public void GetExtraInputExtensions_NormalisesDotPrefixAndCaseInsensitive()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, "TT;.Proto");
            var ext = StrictModeSettings.GetExtraInputExtensions();
            ext.ShouldContain(".tt");   // lower-case lookup via case-insensitive comparer
            ext.ShouldContain(".TT");   // case-insensitive
            ext.ShouldContain(".proto");
            ext.ShouldContain(".PROTO");
        }

        [Fact]
        public void GetExtraInputExtensions_PicksUpRuntimeChanges_ViaCacheKey()
        {
            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, ".tt");
            StrictModeSettings.GetExtraInputExtensions().ShouldContain(".tt");

            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, ".proto");
            var ext = StrictModeSettings.GetExtraInputExtensions();
            ext.ShouldContain(".proto");
            ext.Contains(".tt").ShouldBeFalse();

            _env.SetEnvironmentVariable(StrictModeSettings.EnvExtraInputExtensions, null);
            StrictModeSettings.GetExtraInputExtensions().Count.ShouldBe(0);
        }

        // ---------------------------------------------------------------------
        // IsForeignManifest: portability guard
        // ---------------------------------------------------------------------

        [Fact]
        public void IsForeignManifest_NullOrEmpty_ReturnsFalse()
        {
            // Pre-portability-check manifests have empty recorded path; treat as not-foreign
            // (downstream existence checks catch real staleness).
            StrictModeSettings.IsForeignManifest(null, @"C:\src\P.csproj").ShouldBeFalse();
            StrictModeSettings.IsForeignManifest("", @"C:\src\P.csproj").ShouldBeFalse();
            StrictModeSettings.IsForeignManifest(@"C:\src\P.csproj", null).ShouldBeFalse();
        }

        [Fact]
        public void IsForeignManifest_SamePath_ReturnsFalse()
        {
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x", "P.csproj");
            StrictModeSettings.IsForeignManifest(p, p).ShouldBeFalse();
        }

        [Fact]
        public void IsForeignManifest_DifferentPath_ReturnsTrue()
        {
            string a = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alice", "P.csproj");
            string b = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bob", "P.csproj");
            StrictModeSettings.IsForeignManifest(a, b).ShouldBeTrue();
        }

        [Fact]
        public void IsForeignManifest_CaseDifferenceOnly_ReturnsFalse()
        {
            // Windows paths are case-insensitive; same path with different casing is the same
            // working tree, not a foreign one. (Match the OrdinalIgnoreCase comparer the
            // strict-mode cache key normalizer already uses.)
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mixed", "P.csproj");
            StrictModeSettings.IsForeignManifest(p, p.ToUpperInvariant()).ShouldBeFalse();
        }

        [Fact]
        public void IsForeignManifest_RelativeVsAbsolute_NormalisesBeforeCompare()
        {
            // GetFullPath turns a relative path into an absolute one using cwd, which should
            // still be considered "current" if it resolves to the same file as the recorded
            // absolute. (Defensive: avoid a foreign-flag for the same on-disk project just
            // because one side was written relative.)
            string absPath = System.IO.Path.GetFullPath("P.csproj");
            StrictModeSettings.IsForeignManifest(absPath, "P.csproj").ShouldBeFalse();
        }
    }
}
