// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text;

#nullable disable

namespace Microsoft.Build.Strict
{
    /// <summary>
    /// Lightweight JSONL telemetry sink for MSBuild Strict Mode components.
    ///
    /// When the environment variable <c>MSBUILDSTRICTTELEMETRYFILE</c> is set, every
    /// call to <see cref="Emit"/> appends one JSON object (single line) describing a
    /// single cache event. The optional environment variable
    /// <c>MSBUILDSTRICTTELEMETRYITER</c> is included on every line so that a single
    /// JSONL file can host multiple iterations of the same scenario.
    ///
    /// Schema (every field except layer/outcome is optional):
    ///   { "ts": ISO8601, "iteration": int, "layer": str, "outcome": str,
    ///     "project": str, "target": str, "reason": str,
    ///     "duration_us": long, "bytes_in": long, "bytes_out": long,
    ///     "file_count": int, "cache_key": str }
    ///
    /// Output is appended with O_APPEND-equivalent semantics so multiple processes
    /// (entry node + worker nodes) can write concurrently without interleaving
    /// individual lines.
    /// </summary>
    internal static class StrictTelemetry
    {
        private const string EnvFile = "MSBUILDSTRICTTELEMETRYFILE";
        private const string EnvIter = "MSBUILDSTRICTTELEMETRYITER";

        private static readonly string s_file = Environment.GetEnvironmentVariable(EnvFile);
        private static readonly string s_iter = Environment.GetEnvironmentVariable(EnvIter);
        private static readonly bool s_enabled = !string.IsNullOrEmpty(s_file);

        public static bool IsEnabled => s_enabled;

        public static void Emit(
            string layer,
            string outcome,
            string project = null,
            string target = null,
            string reason = null,
            long? durationUs = null,
            long? bytesIn = null,
            long? bytesOut = null,
            int? fileCount = null,
            string cacheKey = null)
        {
            if (!s_enabled)
            {
                return;
            }

            try
            {
                var sb = new StringBuilder(256);
                sb.Append('{');
                AppendStr(sb, "ts", DateTime.UtcNow.ToString("o"));
                AppendComma(sb);
                AppendStr(sb, "layer", layer);
                AppendComma(sb);
                AppendStr(sb, "outcome", outcome);
                if (!string.IsNullOrEmpty(s_iter))
                {
                    AppendComma(sb); sb.Append("\"iteration\":").Append(s_iter);
                }
                if (project != null) { AppendComma(sb); AppendStr(sb, "project", project); }
                if (target != null) { AppendComma(sb); AppendStr(sb, "target", target); }
                if (reason != null) { AppendComma(sb); AppendStr(sb, "reason", reason); }
                if (durationUs.HasValue) { AppendComma(sb); sb.Append("\"duration_us\":").Append(durationUs.Value); }
                if (bytesIn.HasValue) { AppendComma(sb); sb.Append("\"bytes_in\":").Append(bytesIn.Value); }
                if (bytesOut.HasValue) { AppendComma(sb); sb.Append("\"bytes_out\":").Append(bytesOut.Value); }
                if (fileCount.HasValue) { AppendComma(sb); sb.Append("\"file_count\":").Append(fileCount.Value); }
                if (cacheKey != null) { AppendComma(sb); AppendStr(sb, "cache_key", cacheKey); }
                sb.Append("}\n");

                // O_APPEND-equivalent: open with FileMode.Append + FileShare.ReadWrite
                // so multiple MSBuild nodes (entry + workers) can write concurrently.
                // POSIX append guarantees per-write atomicity for small writes; on
                // Windows, FileSystemRights.AppendData behaves equivalently for the
                // sub-PIPE_BUF lines we emit (<4 KB).
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                using var fs = new FileStream(
                    s_file,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: false);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Telemetry must never break a build.
            }
        }

        private static void AppendComma(StringBuilder sb) => sb.Append(',');

        private static void AppendStr(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            EscapeJson(sb, value ?? string.Empty);
            sb.Append('"');
        }

        private static void EscapeJson(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.AppendFormat("\\u{0:x4}", (int)c); }
                        else { sb.Append(c); }
                        break;
                }
            }
        }
    }
}
