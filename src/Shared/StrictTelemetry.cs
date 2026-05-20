// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
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
    ///     "project": str, "target": str, "reason": str, "reason_code": str,
    ///     "duration_us": long, "bytes_in": long, "bytes_out": long,
    ///     "file_count": int, "cache_key": str, "summary_kind": str,
    ///     "summary_outcome": str, "count": long }
    ///
    /// Output is appended with O_APPEND-equivalent semantics so multiple processes
    /// (entry node + worker nodes) can write concurrently without interleaving
    /// individual lines.
    /// </summary>
    internal static class StrictTelemetry
    {
        private const string EnvFile = "MSBUILDSTRICTTELEMETRYFILE";
        private const string EnvIter = "MSBUILDSTRICTTELEMETRYITER";
        private const string SummaryOutcome = "summary";
        private const string SummaryKindOutcome = "outcome";
        private const string SummaryKindReason = "reason";

        internal enum ReasonCode
        {
            Disabled,
            NoProjectFile,
            NoManifest,
            ManifestCorrupt,
            ManifestMismatch,
            ForeignMachine,
            InputCountChanged,
            InputChanged,
            InputStatFailed,
            OutputMissingOrChanged,
            OutputStatFailed,
            SynthOutputMissing,
            MissingTargetResult,
            SkipTarget,
            NonCacheableTarget,
            ReplaceExistingProjectInstance,
            NonSuccess,
            ExceptionOrCircular,
            MissingOrFailedTarget,
            Exception,
        }

        // Re-read both env vars on every Emit call so that MSBuild Server / persistent
        // worker nodes (which outlive a single `dotnet build` invocation) pick up
        // changes between invocations. The sibling StrictTargetCache.GetMode follows
        // the same per-call re-read pattern for exactly this reason; see that method
        // before reverting this to a static-readonly initializer.
        private static volatile string s_file = Environment.GetEnvironmentVariable(EnvFile);
        private static volatile string s_iter = Environment.GetEnvironmentVariable(EnvIter);
        private static readonly ConcurrentDictionary<string, long> s_summaryCounts = new(StringComparer.Ordinal);
        private static int s_exitHookInstalled;

        public static bool IsEnabled => !string.IsNullOrEmpty(ResolveFile());

        private static string ResolveFile()
        {
            string current = Environment.GetEnvironmentVariable(EnvFile);
            if (!ReferenceEquals(current, s_file) && !string.Equals(current, s_file, StringComparison.Ordinal))
            {
                s_file = current;
            }
            return s_file;
        }

        private static string ResolveIter()
        {
            string current = Environment.GetEnvironmentVariable(EnvIter);
            if (!ReferenceEquals(current, s_iter) && !string.Equals(current, s_iter, StringComparison.Ordinal))
            {
                s_iter = current;
            }
            return s_iter;
        }

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
            string file = ResolveFile();
            if (string.IsNullOrEmpty(file))
            {
                return;
            }

            string iter = ResolveIter();
            EnsureExitHookInstalled();
            string reasonCode = ClassifyReasonCode(reason)?.ToString();

            try
            {
                WriteLine(file, BuildEventJson(
                    ts: DateTime.UtcNow,
                    iter: iter,
                    layer: layer,
                    outcome: outcome,
                    project: project,
                    target: target,
                    reason: reason,
                    reasonCode: reasonCode,
                    durationUs: durationUs,
                    bytesIn: bytesIn,
                    bytesOut: bytesOut,
                    fileCount: fileCount,
                    cacheKey: cacheKey,
                    summaryKind: null,
                    summaryOutcome: null,
                    count: null));

                if (!string.Equals(outcome, SummaryOutcome, StringComparison.Ordinal))
                {
                    IncrementSummary(layer, outcome, null);
                    if (!string.IsNullOrEmpty(reasonCode))
                    {
                        IncrementSummary(layer, outcome, reasonCode);
                    }
                }
            }
            catch
            {
                // Telemetry must never break a build.
            }
        }

        internal static void FlushSummaryForTests() => FlushSummary();

        internal static void ResetForTests()
        {
            s_summaryCounts.Clear();
            s_file = Environment.GetEnvironmentVariable(EnvFile);
            s_iter = Environment.GetEnvironmentVariable(EnvIter);
        }

        private static void EnsureExitHookInstalled()
        {
            if (System.Threading.Interlocked.CompareExchange(ref s_exitHookInstalled, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushSummary();
            }
        }

        private static void IncrementSummary(string layer, string outcome, string reasonCode)
        {
            string key = string.Concat(layer ?? string.Empty, "\u001f", outcome ?? string.Empty, "\u001f", reasonCode ?? string.Empty);
            s_summaryCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
        }

        private static void FlushSummary()
        {
            string file = ResolveFile();
            if (string.IsNullOrEmpty(file) || s_summaryCounts.IsEmpty)
            {
                return;
            }

            string iter = ResolveIter();
            DateTime now = DateTime.UtcNow;
            foreach (var kvp in s_summaryCounts.ToArray())
            {
                if (!s_summaryCounts.TryRemove(kvp.Key, out long count))
                {
                    continue;
                }

                string[] parts = kvp.Key.Split('\u001f');
                string layer = parts.Length > 0 ? parts[0] : string.Empty;
                string summaryOutcome = parts.Length > 1 ? parts[1] : string.Empty;
                string reasonCode = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
                string summaryKind = reasonCode is null ? SummaryKindOutcome : SummaryKindReason;

                try
                {
                    WriteLine(file, BuildEventJson(
                        ts: now,
                        iter: iter,
                        layer: layer,
                        outcome: SummaryOutcome,
                        project: null,
                        target: null,
                        reason: null,
                        reasonCode: reasonCode,
                        durationUs: null,
                        bytesIn: null,
                        bytesOut: null,
                        fileCount: null,
                        cacheKey: null,
                        summaryKind: summaryKind,
                        summaryOutcome: summaryOutcome,
                        count: count));
                }
                catch
                {
                    // Telemetry must never break a build.
                }
            }
        }

        private static ReasonCode? ClassifyReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return null;
            }

            if (string.Equals(reason, "disabled", StringComparison.Ordinal)) return ReasonCode.Disabled;
            if (string.Equals(reason, "no-project-file", StringComparison.Ordinal)) return ReasonCode.NoProjectFile;
            if (string.Equals(reason, "no-manifest", StringComparison.Ordinal)) return ReasonCode.NoManifest;
            if (string.Equals(reason, "manifest-corrupt", StringComparison.Ordinal)) return ReasonCode.ManifestCorrupt;
            if (string.Equals(reason, "manifest-mismatch", StringComparison.Ordinal)) return ReasonCode.ManifestMismatch;
            if (string.Equals(reason, "foreign-machine", StringComparison.Ordinal)) return ReasonCode.ForeignMachine;
            if (string.Equals(reason, "replace-instance", StringComparison.Ordinal)) return ReasonCode.ReplaceExistingProjectInstance;
            if (string.Equals(reason, "non-success", StringComparison.Ordinal)) return ReasonCode.NonSuccess;
            if (string.Equals(reason, "exception-or-circular", StringComparison.Ordinal)) return ReasonCode.ExceptionOrCircular;
            if (reason.StartsWith("skip-target:", StringComparison.Ordinal)) return ReasonCode.SkipTarget;
            if (reason.StartsWith("non-cacheable-target:", StringComparison.Ordinal)) return ReasonCode.NonCacheableTarget;
            if (reason.StartsWith("input-count-changed", StringComparison.Ordinal)) return ReasonCode.InputCountChanged;
            if (reason.StartsWith("input-changed ", StringComparison.Ordinal) || reason.StartsWith("input-changed", StringComparison.Ordinal)) return ReasonCode.InputChanged;
            if (reason.StartsWith("input-stat-failed", StringComparison.Ordinal)) return ReasonCode.InputStatFailed;
            if (reason.StartsWith("output-missing-or-changed", StringComparison.Ordinal)) return ReasonCode.OutputMissingOrChanged;
            if (reason.StartsWith("output-stat-failed", StringComparison.Ordinal)) return ReasonCode.OutputStatFailed;
            if (reason.StartsWith("synth-output-missing", StringComparison.Ordinal)) return ReasonCode.SynthOutputMissing;
            if (reason.StartsWith("missing-target-result:", StringComparison.Ordinal)) return ReasonCode.MissingTargetResult;
            if (reason.StartsWith("missing-or-failed-target:", StringComparison.Ordinal)) return ReasonCode.MissingOrFailedTarget;
            if (reason.StartsWith("error:", StringComparison.Ordinal) || reason.StartsWith("exception:", StringComparison.Ordinal) || reason.StartsWith("store-error:", StringComparison.Ordinal)) return ReasonCode.Exception;
            return null;
        }

        private static string BuildEventJson(
            DateTime ts,
            string iter,
            string layer,
            string outcome,
            string project,
            string target,
            string reason,
            string reasonCode,
            long? durationUs,
            long? bytesIn,
            long? bytesOut,
            int? fileCount,
            string cacheKey,
            string summaryKind,
            string summaryOutcome,
            long? count)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendStr(sb, "ts", ts.ToString("o"));
            AppendComma(sb);
            AppendStr(sb, "layer", layer);
            AppendComma(sb);
            AppendStr(sb, "outcome", outcome);
            if (!string.IsNullOrEmpty(iter)) { AppendComma(sb); sb.Append("\"iteration\":").Append(iter); }
            if (project != null) { AppendComma(sb); AppendStr(sb, "project", project); }
            if (target != null) { AppendComma(sb); AppendStr(sb, "target", target); }
            if (reason != null) { AppendComma(sb); AppendStr(sb, "reason", reason); }
            if (reasonCode != null) { AppendComma(sb); AppendStr(sb, "reason_code", reasonCode); }
            if (durationUs.HasValue) { AppendComma(sb); sb.Append("\"duration_us\":").Append(durationUs.Value); }
            if (bytesIn.HasValue) { AppendComma(sb); sb.Append("\"bytes_in\":").Append(bytesIn.Value); }
            if (bytesOut.HasValue) { AppendComma(sb); sb.Append("\"bytes_out\":").Append(bytesOut.Value); }
            if (fileCount.HasValue) { AppendComma(sb); sb.Append("\"file_count\":").Append(fileCount.Value); }
            if (cacheKey != null) { AppendComma(sb); AppendStr(sb, "cache_key", cacheKey); }
            if (summaryKind != null) { AppendComma(sb); AppendStr(sb, "summary_kind", summaryKind); }
            if (summaryOutcome != null) { AppendComma(sb); AppendStr(sb, "summary_outcome", summaryOutcome); }
            if (count.HasValue) { AppendComma(sb); sb.Append("\"count\":").Append(count.Value); }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void WriteLine(string file, string jsonLine)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(jsonLine);
            using var fs = new FileStream(
                file,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: false);
            fs.Write(bytes, 0, bytes.Length);
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
