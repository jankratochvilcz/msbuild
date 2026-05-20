// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    internal static class StrictPath
    {
        internal static StringComparer PathComparer { get; } = NativeMethodsShared.IsWindows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        internal static StringComparison PathComparison { get; } = NativeMethodsShared.IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        internal static string Canonicalize(string path)
        {
            string full = FileUtilities.NormalizePath(path);
            return NativeMethodsShared.IsWindows ? full.ToUpperInvariant() : full;
        }

        internal static string CanonicalizeRelativeTo(string baseDirectory, string path)
        {
            return Path.IsPathRooted(path)
                ? Canonicalize(path)
                : Canonicalize(Path.Combine(baseDirectory, path));
        }

        internal static string ToCacheRelativePath(string baseDirectory, string path)
        {
            string full = CanonicalizeRelativeTo(baseDirectory, path);
            string root = EnsureTrailingSeparator(Canonicalize(baseDirectory));
            if (full.StartsWith(root, PathComparison))
            {
                return full.Substring(root.Length).Replace('\\', '/');
            }

            return full.Replace('\\', '/');
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path) || path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar)
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
