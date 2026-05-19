// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

#nullable disable

namespace Microsoft.Build.Strict
{
    /// <summary>
    /// Effective level of MSBuild Strict Mode as parsed from a single canonical source
    /// (the <c>MSBUILDSTRICTMODE</c> environment variable, optionally falling back to the
    /// <c>MSBuildStrictMode</c> project property).
    /// </summary>
    internal enum StrictModeLevel
    {
        /// <summary>Strict mode is not enabled.</summary>
        Off = 0,

        /// <summary>Strict mode is enabled and violations are logged as warnings.</summary>
        Warn = 1,

        /// <summary>Strict mode is enabled and violations are logged as errors / refuse to persist.</summary>
        Enforce = 2,
    }

    /// <summary>
    /// Centralised opt-in parsing for MSBuild Strict Mode. Every strict-mode layer
    /// (<see cref="object"/> equivalents in <c>StrictProjectCache</c>, <c>StrictTargetCache</c>,
    /// <c>StrictSolutionFastSkip</c>, and <c>TargetUpToDateChecker</c>) MUST go through this
    /// helper so that a single <c>MSBUILDSTRICTMODE</c> value enables (or disables) every layer
    /// consistently.
    /// <para>
    /// Truth table for <c>MSBUILDSTRICTMODE</c> (and, where applicable, <c>$(MSBuildStrictMode)</c>):
    /// </para>
    /// <list type="table">
    ///   <listheader><term>Value</term><description>Resulting <see cref="StrictModeLevel"/></description></listheader>
    ///   <item><term><c>(unset)</c>, <c>""</c>, <c>0</c>, <c>false</c>, <c>off</c>, <c>no</c></term><description><see cref="StrictModeLevel.Off"/></description></item>
    ///   <item><term><c>1</c>, <c>true</c>, <c>on</c>, <c>yes</c>, <c>warn</c></term><description><see cref="StrictModeLevel.Warn"/></description></item>
    ///   <item><term><c>enforce</c>, <c>strict</c>, <c>error</c></term><description><see cref="StrictModeLevel.Enforce"/></description></item>
    ///   <item><term>any other value</term><description><see cref="StrictModeLevel.Off"/> (unknown values are NOT silently treated as enabled)</description></item>
    /// </list>
    /// <para>
    /// The environment variable takes precedence over the project property. The environment
    /// variable is re-read on every call so that MSBuild Server / long-lived worker nodes pick
    /// up changes between submissions.
    /// </para>
    /// </summary>
    internal static class StrictModeSettings
    {
        /// <summary>Canonical opt-in env var; recognised by every strict-mode layer.</summary>
        internal const string EnvVarName = "MSBUILDSTRICTMODE";

        /// <summary>Canonical opt-in project property; recognised by per-project layers (target cache, up-to-date checker).</summary>
        internal const string ProjectPropertyName = "MSBuildStrictMode";

        /// <summary>Per-layer disable gate for <c>StrictProjectCache</c>.</summary>
        internal const string EnvDisableProjectCache = "MSBUILDSTRICTNOPROJECTCACHE";

        /// <summary>Per-layer disable gate for <c>StrictSolutionFastSkip</c>.</summary>
        internal const string EnvDisableSolutionFastSkip = "MSBUILDSTRICTNOFASTSKIP";

        /// <summary>
        /// Resolves the effective strict-mode level. The environment variable takes precedence;
        /// if it is unset OR explicitly <see cref="StrictModeLevel.Off"/>, the (optional) project
        /// property value is consulted.
        /// </summary>
        /// <param name="projectPropertyValue">
        /// Value of the <c>MSBuildStrictMode</c> project property, or <c>null</c> if the caller is
        /// not in a per-project context (e.g. the solution-level fast-skip cache).
        /// </param>
        internal static StrictModeLevel ResolveLevel(string projectPropertyValue = null)
        {
            // Re-read on every call so MSBuild Server / long-lived worker nodes see changes.
            StrictModeLevel envLevel = ParseLevel(Environment.GetEnvironmentVariable(EnvVarName));
            if (envLevel != StrictModeLevel.Off)
            {
                return envLevel;
            }
            return ParseLevel(projectPropertyValue);
        }

        /// <summary>
        /// Returns true iff the resolved level is non-Off AND the optional per-layer disable
        /// environment variable is unset. This is the standard predicate for layers that
        /// expose a "kill switch" env var.
        /// </summary>
        internal static bool IsLayerEnabled(string projectPropertyValue, string layerDisableEnvVar)
        {
            if (ResolveLevel(projectPropertyValue) == StrictModeLevel.Off)
            {
                return false;
            }
            if (string.IsNullOrEmpty(layerDisableEnvVar))
            {
                return true;
            }
            string disable = Environment.GetEnvironmentVariable(layerDisableEnvVar);
            return string.IsNullOrEmpty(disable);
        }

        /// <summary>
        /// Parses a single value (env var or project property) into a <see cref="StrictModeLevel"/>.
        /// Unknown values resolve to <see cref="StrictModeLevel.Off"/> so a typo never silently
        /// enables strict mode for a build that did not opt in.
        /// </summary>
        internal static StrictModeLevel ParseLevel(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return StrictModeLevel.Off;
            }

            // Explicit "off" synonyms first.
            if (string.Equals(value, "0", StringComparison.Ordinal)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                return StrictModeLevel.Off;
            }

            if (string.Equals(value, "enforce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "strict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
            {
                return StrictModeLevel.Enforce;
            }

            if (string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "warn", StringComparison.OrdinalIgnoreCase))
            {
                return StrictModeLevel.Warn;
            }

            // Unknown values are treated as Off to avoid silently enabling strict mode.
            return StrictModeLevel.Off;
        }
    }
}
