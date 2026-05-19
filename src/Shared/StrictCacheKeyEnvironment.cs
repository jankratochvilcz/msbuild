// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Build.Strict;

internal static class StrictCacheKeyEnvironment
{
    internal const string PropertyName = "StrictModeCacheKeyEnvVars";
    internal const string DefaultValue = "BUILD_*;DOTNET_*;MSBUILD_*;NUGET_*;RID;LANG;LC_ALL;LC_MESSAGES;LANGUAGE";

    private static readonly StringComparison s_environmentNameComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static string GetConfiguredValue(string? configuredValue)
        => configuredValue is null || string.IsNullOrWhiteSpace(configuredValue) ? DefaultValue : configuredValue;

    internal static string? GetConfiguredValue(IReadOnlyDictionary<string, string>? globalProperties)
        => TryGetConfiguredValue(globalProperties);

    internal static string? GetConfiguredValue(IDictionary<string, string>? globalProperties)
        => TryGetConfiguredValue(globalProperties);

    private static string? TryGetConfiguredValue(IEnumerable<KeyValuePair<string, string>>? globalProperties)
    {
        if (globalProperties is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, string> pair in globalProperties)
        {
            if (string.Equals(pair.Key, PropertyName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    internal static void AppendFingerprint(StringBuilder builder, string? configuredValue)
    {
        List<string> patterns = ParsePatterns(GetConfiguredValue(configuredValue));
        if (patterns.Count == 0)
        {
            return;
        }

        var resolved = new List<KeyValuePair<string, string>>();
        IDictionary environmentVariables = Environment.GetEnvironmentVariables();
        foreach (DictionaryEntry entry in environmentVariables)
        {
            if (entry.Key is not string name || IsExcludedEnvironmentVariable(name) || !MatchesAny(name, patterns))
            {
                continue;
            }

            resolved.Add(new KeyValuePair<string, string>(name, entry.Value as string ?? string.Empty));
        }

        resolved.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        foreach (KeyValuePair<string, string> pair in resolved)
        {
            builder.Append("e:").Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        }
    }

    private static bool IsExcludedEnvironmentVariable(string name)
        => name.StartsWith("MSBUILDSTRICT", s_environmentNameComparison)
            || string.Equals(name, "DOTNET_CLI_TELEMETRY_SESSIONID", s_environmentNameComparison);

    private static bool MatchesAny(string name, List<string> patterns)
    {
        for (int i = 0; i < patterns.Count; i++)
        {
            string pattern = patterns[i];
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                string prefix = pattern.Substring(0, pattern.Length - 1);
                if (name.StartsWith(prefix, s_environmentNameComparison))
                {
                    return true;
                }
            }
            else if (string.Equals(name, pattern, s_environmentNameComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ParsePatterns(string configuredValue)
    {
        var patterns = new List<string>();
        foreach (string entry in configuredValue.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length > 0)
            {
                patterns.Add(trimmed);
            }
        }

        return patterns;
    }
}
