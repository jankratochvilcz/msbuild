// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
#if NET
using System.Buffers;
#endif
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Build.BackEnd;
using Microsoft.Build.Framework;
using Microsoft.Build.Strict;

#nullable disable

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// Tier-3 "Strict Mode" project-level fast-skip cache.
    ///
    /// Sits in front of <see cref="BuildManager"/> evaluation so that on a warm hit we never
    /// load the project XML, never run the project-reference protocol, and never enter the
    /// scheduler – the requested per-project targets are serviced directly from a manifest
    /// stored under <c>$(ProjectDir)\obj\.strict-project\&lt;sha256&gt;.manifest</c>.
    ///
    /// Cache key inputs:
    ///   - project full path (case-insensitive on Windows)
    ///   - sorted global properties (key=value\n)
    ///   - sorted, comma-joined requested targets
    ///   - MSBuild assembly version
    ///   - sha256+size of every "source-like" file under the project directory
    ///     (.cs/.vb/.fs/.fsx/.resx/.razor/.cshtml/.xaml/project files etc.)
    ///   - sha256 of obj/project.assets.json and obj/project.nuget.cache (when present),
    ///     which transitively close over restored NuGet and ProjectReference outputs.
    ///   - Directory.Build.{props,targets} and Directory.Packages.props walking up to the
    ///     filesystem root.
    ///
    /// On a hit we synthesise a <see cref="BuildResult"/> populated with cached
    /// <see cref="TargetResult"/>s for the requested targets and complete the submission via
    /// the same code path already used by the project-cache plugin hit case
    /// (<see cref="BuildManager"/>: AddBuildRequestToSubmission → CompleteLogging →
    /// ReportResultsToSubmission). On a miss the normal build runs and
    /// <see cref="MaybeStoreOnCompletion"/> persists the manifest if the build succeeded
    /// and every requested target produced a result.
    ///
    /// Correctness invariants:
    ///   1. Never synthesise a target result for a target we did not actually cache.
    ///   2. Never cache when the build was anything other than success.
    ///   3. Never cache when any requested target is in the dynamic/destructive set
    ///      (Restore/Clean/Rebuild/Pack/Publish/VSTest).
    ///   4. Never cache when <see cref="BuildRequestDataFlags.ReplaceExistingProjectInstance"/>
    ///      is set – the caller wants a forced re-evaluation.
    ///   5. Bail to normal build on ANY exception – this layer is a pure optimisation.
    /// </summary>
    internal static class StrictProjectCache
    {
        private const string CacheDirName = ".strict-project";
        private const int ManifestSchemaVersion = 1;
        private static readonly TimeSpan s_orphanTempSweepAge = TimeSpan.FromHours(1);

        // Targets we are willing to synthesise from cache. These are pure "describe what the
        // project would produce" or terminal "do the build" targets that have well-defined,
        // serialisable output items. Anything outside this set bypasses the cache.
        private static readonly HashSet<string> s_cacheableTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Build",
            "GetTargetPath",
            "GetTargetPathWithTargetPlatformMoniker",
            "GetNativeManifest",
            "GetCopyToOutputDirectoryItems",
            "GetTargetFrameworks",
            "GetTargetFrameworksWithPlatformForSingleTargetFramework",
            "GetTargetFrameworksWithPlatform",
            "ResolveProjectReferences",
            "_ComputeNonExistentFileProperty",
            "GetAssemblyAttributes",
        };

        // Targets we never cache – they're either destructive, dynamic, or compose external
        // state we cannot statically close over via file hashing.
        private static readonly HashSet<string> s_skipTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Restore",
            "Clean",
            "Rebuild",
            "Pack",
            "Publish",
            "VSTest",
            "Test",
            "Run",
        };

        // "Source-like" extensions that should invalidate the cache when their content changes.
        // The set covers the common build inputs across the SDKs we ship for; additional
        // extensions can be appended at runtime via the MSBUILDSTRICTEXTRAINPUTEXTENSIONS env var
        // (see StrictModeSettings.GetExtraInputExtensions).
        private static readonly HashSet<string> s_sourceExts = new(StringComparer.OrdinalIgnoreCase)
        {
            // Managed languages and razor / blazor / xaml authoring
            ".cs", ".vb", ".fs", ".fsx", ".fsi", ".razor", ".cshtml", ".vbhtml", ".xaml", ".axaml",
            // ASP.NET classic / WCF authoring
            ".aspx", ".ascx", ".master", ".svc", ".asmx", ".ashx",
            // Native / interop
            ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".idl", ".def", ".rc",
            // TypeScript / JavaScript (BlazorWASM, JSInterop, npm-driven tooling)
            ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
            // T4 / code generation
            ".tt", ".t4",
            // gRPC / Protobuf
            ".proto",
            // Resources / data / docs commonly embedded as build inputs
            ".resx", ".resw", ".licx", ".settings", ".json", ".xml", ".yaml", ".yml", ".md",
            // Project / props / targets / tasks / response files
            ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".sqlproj", ".shproj",
            ".props", ".targets", ".tasks", ".overridetasks",
            ".sln", ".slnx", ".slnf",
            ".config", ".editorconfig", ".rsp",
            // Image / cursor / icon assets often embedded
            ".png", ".bmp", ".jpg", ".jpeg", ".gif", ".ico", ".cur",
        };

        // Directories whose content is irrelevant to the cache key.
        private static readonly HashSet<string> s_excludedDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules", "packages",
        };

        // Files at the project's obj/ that we DO want to include (cuts across the excluded "obj").
        private static readonly string[] s_objIncludes =
        {
            "project.assets.json",
            "project.nuget.cache",
        };

        // Global properties whose values are volatile across builds (timestamps, sentinels,
        // per-restore session ids, …) or whose role is internal-only. Including them in the
        // cache key would force a miss on every warm build. The convention "names that start
        // with '_' are MSBuild-internal" is also applied below.
        private static readonly HashSet<string> s_volatileGlobalProps = new(StringComparer.OrdinalIgnoreCase)
        {
            "_GeneratedAssemblyInfoFileSentinel",
            "_ResolveReferenceDependencies",
            "_GenerateRuntimeConfigurationFiles_BuildKey",
            "RestoreSources",
            "RestorePackagesPath",
            "MSBuildRestoreSessionId",
            "MSBuildSessionId",
            "BuildProjectReferences",
        };

        // Output-ish extensions: if a cached TaskItem's ItemSpec ends in one of these and
        // resolves to a missing file on disk, we MUST treat the manifest as a miss. The
        // OutputFileStamp list captured at persist time covers this for items that existed
        // when the manifest was written, but this guard catches the case where a later
        // 'Clean' or out-of-band deletion removes outputs we'd otherwise hand back blindly.
        private static readonly HashSet<string> s_outputExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".xml", ".runtimeconfig.json", ".deps.json",
        };

        // Per-process file hash cache, keyed by full path; invalidated on (mtime,size) change.
        private static readonly ConcurrentDictionary<string, (long Ticks, long Size, string Hash)> s_fileHashCache
            = new(StringComparer.OrdinalIgnoreCase);

        // Submission-level pending records populated on miss by RegisterMiss and consumed by
        // MaybeStoreOnCompletion when the submission's BuildResult is delivered.
        private static readonly ConcurrentDictionary<int, PendingRecord> s_pending = new();

        private static readonly string s_assemblyVersion = ComputeAssemblyVersion();

        private static string ComputeAssemblyVersion()
        {
            try
            {
                return typeof(BuildManager).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        private static int CompareMSBuildIdentifierForCacheKey(string left, string right)
        {
            return StringComparer.Ordinal.Compare(
                CanonicalizeMSBuildIdentifierForCacheKey(left),
                CanonicalizeMSBuildIdentifierForCacheKey(right));
        }

        private static string CanonicalizeMSBuildIdentifierForCacheKey(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            char[] chars = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c is >= 'a' and <= 'z')
                {
                    chars ??= value.ToCharArray();
                    chars[i] = (char)(c - ('a' - 'A'));
                }
            }

            return chars is null ? value : new string(chars);
        }

        public static bool IsEnabled()
        {
            return StrictModeSettings.IsLayerEnabled(
                projectPropertyValue: null,
                layerDisableEnvVar: StrictModeSettings.EnvDisableProjectCache);
        }

        internal static bool TargetsAreCacheable(IReadOnlyList<string> targets, out string reason)
        {
            reason = null;
            if (targets is null || targets.Count == 0)
            {
                // Default targets are allowed (treated as "Build").
                return true;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                string t = targets[i];
                if (string.IsNullOrEmpty(t))
                {
                    continue;
                }
                if (s_skipTargets.Contains(t))
                {
                    reason = "skip-target:" + t;
                    return false;
                }
                if (!s_cacheableTargets.Contains(t))
                {
                    reason = "non-cacheable-target:" + t;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Attempt to satisfy this (project, globals, targets) from disk cache.
        /// Returns the cached target outputs on hit, null on miss. Always populates
        /// <paramref name="cacheKey"/> and <paramref name="reason"/> for telemetry.
        /// </summary>
        public static CachedBuild TryHit(
            string projectFullPath,
            IReadOnlyDictionary<string, string> globalProperties,
            IReadOnlyList<string> targets,
            BuildRequestDataFlags flags,
            out string cacheKey,
            out string reason)
        {
            cacheKey = null;
            reason = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!IsEnabled())
                {
                    reason = "disabled";
                    return null;
                }
                if (string.IsNullOrEmpty(projectFullPath) || !File.Exists(projectFullPath))
                {
                    reason = "no-project-file";
                    return null;
                }
                if ((flags & BuildRequestDataFlags.ReplaceExistingProjectInstance) != 0)
                {
                    reason = "replace-instance";
                    return null;
                }
                if (!TargetsAreCacheable(targets, out string targetReason))
                {
                    reason = targetReason;
                    return null;
                }

                var sigInputs = EnumerateSignatureInputs(projectFullPath);
                cacheKey = ComputeCacheKey(projectFullPath, globalProperties, targets, sigInputs);
                string manifestPath = GetManifestPath(projectFullPath, cacheKey);
                if (!File.Exists(manifestPath))
                {
                    reason = "no-manifest";
                    EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                    return null;
                }

                Manifest m = Manifest.Load(manifestPath);
                if (m is null)
                {
                    reason = "manifest-corrupt";
                    EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                    return null;
                }

                if (m.SchemaVersion != ManifestSchemaVersion || !string.Equals(m.CacheKey, cacheKey, StringComparison.Ordinal))
                {
                    reason = "manifest-mismatch";
                    EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                    return null;
                }

                // Portability guard: if the manifest was produced on a different machine (or
                // copied via OneDrive/tarball into a different working tree on the same box),
                // its absolute OutputFiles paths are meaningless here. Reject rather than risk
                // restoring nothing — or, worse, restoring stale outputs from a sibling path.
                if (StrictModeSettings.IsForeignManifest(m.ProjectFullPath, projectFullPath))
                {
                    reason = "foreign-machine";
                    EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                    return null;
                }

                // Verify cached output files still exist with the same size+mtime.
                foreach (var of in m.OutputFiles)
                {
                    try
                    {
                        var fi = new FileInfo(of.Path);
                        if (!fi.Exists || fi.Length != of.Size || fi.LastWriteTimeUtc.Ticks != of.Ticks)
                        {
                            reason = "output-missing-or-changed:" + of.Path;
                            EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                            return null;
                        }
                    }
                    catch
                    {
                        reason = "output-stat-failed:" + of.Path;
                        EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                        return null;
                    }
                }

                // Additional belt-and-braces: walk every cached TaskItem ItemSpec. If the spec
                // looks like a file path AND has an output-ish extension (.dll/.exe/.pdb/…),
                // require the file to exist on disk. This protects against the failure mode
                // where a consuming project's ResolveAssemblyReference/Copy reads back a path
                // we hand it from cache only to find the bits were cleaned out from under us.
                // The OutputFileStamp check above is comprehensive for items present at persist
                // time, but this scan is the last line of defence for stale or partial caches.
                string projDirForRelative = Path.GetDirectoryName(projectFullPath);
                foreach (CachedTargetResult ctr in m.TargetResults)
                {
                    if (ctr.Items is null)
                    {
                        continue;
                    }
                    for (int i = 0; i < ctr.Items.Count; i++)
                    {
                        string spec = ctr.Items[i].ItemSpec;
                        if (!LooksLikeFilePath(spec))
                        {
                            continue;
                        }
                        string ext = Path.GetExtension(spec);
                        if (!s_outputExts.Contains(ext))
                        {
                            continue;
                        }
                        string abs = ResolveRelative(spec, projDirForRelative);
                        if (string.IsNullOrEmpty(abs))
                        {
                            continue;
                        }
                        if (!File.Exists(abs))
                        {
                            reason = "synth-output-missing:" + abs;
                            EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                            return null;
                        }
                    }
                }

                // Verify the manifest contains every requested target. Missing => miss.
                if (targets is not null && targets.Count > 0)
                {
                    foreach (string t in targets)
                    {
                        if (string.IsNullOrEmpty(t))
                        {
                            continue;
                        }
                        bool found = false;
                        for (int i = 0; i < m.TargetResults.Count; i++)
                        {
                            if (string.Equals(m.TargetResults[i].TargetName, t, StringComparison.OrdinalIgnoreCase))
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            reason = "missing-target-result:" + t;
                            EmitTelemetry("miss", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                            return null;
                        }
                    }
                }

                reason = "hit";
                EmitTelemetry("hit", projectFullPath, targets, reason, sw, sigInputs.Count, cacheKey);
                return new CachedBuild(m);
            }
            catch (Exception ex)
            {
                reason = "error:" + ex.GetType().Name;
                try
                {
                    EmitTelemetry("error", projectFullPath, targets, reason, sw, 0, cacheKey);
                }
                catch
                {
                    // Telemetry must never break a build.
                }
                return null;
            }
        }

        /// <summary>
        /// Register a miss for later post-build persistence by <see cref="MaybeStoreOnCompletion"/>.
        /// </summary>
        public static void RegisterMiss(
            int submissionId,
            string projectFullPath,
            IReadOnlyDictionary<string, string> globalProperties,
            IReadOnlyList<string> targets,
            string cacheKey)
        {
            if (string.IsNullOrEmpty(projectFullPath) || string.IsNullOrEmpty(cacheKey))
            {
                return;
            }
            s_pending[submissionId] = new PendingRecord(projectFullPath, ToList(globalProperties), ToList(targets), cacheKey);
        }

        // Tracks global request IDs whose result was synthesised from the strict cache. The
        // inner scheduler persist hook consults this to avoid re-hashing and re-writing a
        // manifest we just loaded from disk a few milliseconds earlier.
        private static readonly ConcurrentDictionary<int, byte> s_servedFromCache = new();

        /// <summary>
        /// Marks a global request id as having been served from the strict project cache so
        /// that the corresponding <see cref="BuildResult"/> arriving at <see cref="Microsoft.Build.BackEnd.Scheduler.ReportResult"/>
        /// is not re-persisted.
        /// </summary>
        public static void MarkServedFromCache(int globalRequestId)
        {
            if (globalRequestId != -1)
            {
                s_servedFromCache[globalRequestId] = 0;
            }
        }

        /// <summary>
        /// Returns true and removes the marker if <paramref name="globalRequestId"/> was
        /// previously marked as served from the strict cache.
        /// </summary>
        public static bool TryConsumeServedFromCache(int globalRequestId)
        {
            return globalRequestId != -1 && s_servedFromCache.TryRemove(globalRequestId, out _);
        }

        private static List<KeyValuePair<string, string>> ToList(IReadOnlyDictionary<string, string> dict)
        {
            if (dict is null)
            {
                return [];
            }
            var list = new List<KeyValuePair<string, string>>(dict.Count);
            foreach (var kv in dict)
            {
                list.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            }
            return list;
        }

        private static List<string> ToList(IReadOnlyList<string> targets)
        {
            if (targets is null)
            {
                return [];
            }
            var list = new List<string>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                list.Add(targets[i]);
            }
            return list;
        }

        /// <summary>
        /// Called after the engine has produced a <see cref="BuildResult"/> for the submission.
        /// Persists a manifest iff the build succeeded and we have a pending miss record.
        /// </summary>
        public static void MaybeStoreOnCompletion(int submissionId, BuildResult result)
        {
            if (result is null)
            {
                return;
            }
            if (!s_pending.TryRemove(submissionId, out PendingRecord pending))
            {
                return;
            }
            StoreManifestCore(pending.ProjectFullPath, pending.GlobalProperties, pending.Targets, pending.CacheKey, result);
        }

        /// <summary>
        /// Persist a manifest directly from caller-supplied (project, globals, targets) and a
        /// <see cref="BuildResult"/>, without going through the submission-id pending table.
        /// Used by the inner scheduler hook for child <c>ProjectReference</c> requests which
        /// share their submission-id with the parent and therefore cannot key into the
        /// per-submission pending dictionary.
        /// </summary>
        public static void MaybeStoreResult(
            string projectFullPath,
            IReadOnlyDictionary<string, string> globalProperties,
            IReadOnlyList<string> targets,
            string cacheKey,
            BuildResult result)
        {
            if (result is null || string.IsNullOrEmpty(projectFullPath) || string.IsNullOrEmpty(cacheKey))
            {
                return;
            }
            StoreManifestCore(projectFullPath, ToList(globalProperties), ToList(targets), cacheKey, result);
        }

        private static void StoreManifestCore(
            string projectFullPath,
            List<KeyValuePair<string, string>> globalProperties,
            List<string> requestedTargets,
            string cacheKey,
            BuildResult result)
        {
            try
            {
                if (result.OverallResult != BuildResultCode.Success)
                {
                    EmitTelemetry("skip", projectFullPath, requestedTargets, "non-success", null, 0, cacheKey);
                    return;
                }
                if (result.Exception is not null || result.CircularDependency)
                {
                    EmitTelemetry("skip", projectFullPath, requestedTargets, "exception-or-circular", null, 0, cacheKey);
                    return;
                }

                // Make sure every requested target has a successful result we can serialise.
                var resultsByTarget = result.ResultsByTarget;
                if (requestedTargets.Count > 0)
                {
                    foreach (string t in requestedTargets)
                    {
                        if (string.IsNullOrEmpty(t))
                        {
                            continue;
                        }
                        if (resultsByTarget is null || !resultsByTarget.TryGetValue(t, out TargetResult tr) || tr.ResultCode != TargetResultCode.Success)
                        {
                            EmitTelemetry("skip", projectFullPath, requestedTargets, "missing-or-failed-target:" + t, null, 0, cacheKey);
                            return;
                        }
                    }
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var manifest = new Manifest
                {
                    SchemaVersion = ManifestSchemaVersion,
                    CacheKey = cacheKey,
                    ProjectFullPath = projectFullPath,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks,
                    Targets = new List<string>(requestedTargets),
                    GlobalProperties = new List<KeyValuePair<string, string>>(globalProperties),
                    TargetResults = new List<CachedTargetResult>(),
                    OutputFiles = new List<OutputFileStamp>(),
                };

                var outputSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (resultsByTarget is not null)
                {
                    foreach (var kv in resultsByTarget)
                    {
                        TargetResult tr = kv.Value;
                        if (tr.ResultCode != TargetResultCode.Success)
                        {
                            continue;
                        }
                        var cached = new CachedTargetResult
                        {
                            TargetName = kv.Key,
                            ResultCode = (int)tr.ResultCode,
                            Items = new List<CachedItem>(),
                        };
                        ITaskItem[] items = tr.Items;
                        if (items is not null)
                        {
                            for (int i = 0; i < items.Length; i++)
                            {
                                ITaskItem ti = items[i];
                                if (ti is null)
                                {
                                    continue;
                                }
                                var ci = new CachedItem
                                {
                                    ItemSpec = ti.ItemSpec ?? string.Empty,
                                    Metadata = new List<KeyValuePair<string, string>>(),
                                };
                                CopyMetadata(ti, ci.Metadata);
                                cached.Items.Add(ci);

                                // Treat any item whose ItemSpec resolves to an existing file as a
                                // cache-validating output. Misses on next hit will notice mismatches.
                                TryAddOutputStamp(projectFullPath, ci.ItemSpec, manifest.OutputFiles, outputSet);
                            }
                        }
                        manifest.TargetResults.Add(cached);
                    }
                }

                string manifestPath = GetManifestPath(projectFullPath, cacheKey);
                Save(manifestPath, manifest);
                sw.Stop();

                EmitTelemetry("store", projectFullPath, requestedTargets, $"targets={manifest.TargetResults.Count}", sw, manifest.OutputFiles.Count, cacheKey);
            }
            catch (Exception ex)
            {
                try
                {
                    EmitTelemetry("error", projectFullPath, requestedTargets, "store-error:" + ex.GetType().Name, null, 0, cacheKey);
                }
                catch
                {
                }
            }
        }

        private static void CopyMetadata(ITaskItem item, List<KeyValuePair<string, string>> dest)
        {
            try
            {
                System.Collections.IDictionary md = item.CloneCustomMetadata();
                if (md is null)
                {
                    return;
                }
                foreach (System.Collections.DictionaryEntry e in md)
                {
                    string k = e.Key as string;
                    string v = e.Value as string;
                    if (!string.IsNullOrEmpty(k))
                    {
                        dest.Add(new KeyValuePair<string, string>(k, v ?? string.Empty));
                    }
                }
            }
            catch
            {
                // Some task item implementations may throw on CloneCustomMetadata. Skip.
            }
        }

        private static void TryAddOutputStamp(string projectFullPath, string itemSpec, List<OutputFileStamp> sink, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(itemSpec))
            {
                return;
            }
            try
            {
                string full;
                if (Path.IsPathRooted(itemSpec))
                {
                    full = Path.GetFullPath(itemSpec);
                }
                else
                {
                    string dir = Path.GetDirectoryName(projectFullPath);
                    if (string.IsNullOrEmpty(dir))
                    {
                        return;
                    }
                    full = Path.GetFullPath(Path.Combine(dir, itemSpec));
                }
                if (!seen.Add(full))
                {
                    return;
                }
                var fi = new FileInfo(full);
                if (!fi.Exists)
                {
                    return;
                }
                sink.Add(new OutputFileStamp(full, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch
            {
                // Best-effort.
            }
        }

        // ---------------------------------------------------------------------
        // Cache key construction
        // ---------------------------------------------------------------------

        private static string ComputeCacheKey(
            string projectFullPath,
            IReadOnlyDictionary<string, string> globalProperties,
            IReadOnlyList<string> targets,
            IReadOnlyList<SignatureInput> sigInputs)
        {
            var sb = new StringBuilder(1024);
            sb.Append("v=").Append(ManifestSchemaVersion).Append('\n');
            sb.Append("asm=").Append(s_assemblyVersion).Append('\n');
            sb.Append("proj=").Append(NormalizePath(projectFullPath)).Append('\n');

            if (globalProperties is not null && globalProperties.Count > 0)
            {
                var keys = new List<string>(globalProperties.Count);
                foreach (var k in globalProperties.Keys)
                {
                    // Drop volatile/internal global properties (timestamp-like sentinels, restore
                    // session ids, anything starting with '_' which MSBuild convention reserves
                    // for engine-internal scratch). Including them would change the cache key on
                    // every build invocation and lock the warm hit-rate to zero.
                    if (IsVolatileGlobalProperty(k))
                    {
                        continue;
                    }
                    keys.Add(k);
                }
                keys.Sort(CompareMSBuildIdentifierForCacheKey);
                foreach (var k in keys)
                {
                    sb.Append("g:").Append(CanonicalizeMSBuildIdentifierForCacheKey(k)).Append('=').Append(globalProperties[k] ?? string.Empty).Append('\n');
                }
            }

            if (targets is not null && targets.Count > 0)
            {
                var tlist = new List<string>(targets.Count);
                for (int i = 0; i < targets.Count; i++)
                {
                    if (!string.IsNullOrEmpty(targets[i]))
                    {
                        tlist.Add(targets[i]);
                    }
                }
                tlist.Sort(CompareMSBuildIdentifierForCacheKey);
                for (int i = 0; i < tlist.Count; i++)
                {
                    tlist[i] = CanonicalizeMSBuildIdentifierForCacheKey(tlist[i]);
                }
                sb.Append("t=").Append(string.Join(",", tlist)).Append('\n');
            }
            else
            {
                sb.Append("t=<default>\n");
            }

            StrictCacheKeyEnvironment.AppendFingerprint(sb, StrictCacheKeyEnvironment.GetConfiguredValue(globalProperties));

            for (int i = 0; i < sigInputs.Count; i++)
            {
                SignatureInput si = sigInputs[i];
                sb.Append("in:").Append(si.RelativePath).Append('|').Append(si.Size).Append('|').Append(si.Sha256).Append('\n');
            }

            return Sha256Hex(sb.ToString());
        }

        private static bool IsVolatileGlobalProperty(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            if (name[0] == '_')
            {
                return true;
            }
            return s_volatileGlobalProps.Contains(name);
        }

        private static bool LooksLikeFilePath(string spec)
        {
            if (string.IsNullOrEmpty(spec))
            {
                return false;
            }
            // Conservative heuristic: must contain a path separator AND must not look like
            // an MSBuild item-metadata expression (e.g. "@(Foo->'%(Bar)')").
            if (spec.IndexOf('\\') < 0 && spec.IndexOf('/') < 0)
            {
                return false;
            }
            if (spec.IndexOf('%') >= 0 || spec.IndexOf('@') >= 0 || spec.IndexOf('$') >= 0)
            {
                return false;
            }
            return true;
        }

        private static string ResolveRelative(string spec, string projDir)
        {
            try
            {
                if (Path.IsPathRooted(spec))
                {
                    return Path.GetFullPath(spec);
                }
                if (string.IsNullOrEmpty(projDir))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(projDir, spec));
            }
            catch
            {
                return null;
            }
        }

        private static string GetManifestPath(string projectFullPath, string cacheKey)
        {
            string projDir = Path.GetDirectoryName(projectFullPath);
            string dir = Path.Combine(projDir, "obj", CacheDirName);
            return Path.Combine(dir, cacheKey + ".manifest");
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }
            string full = Path.GetFullPath(path);
#if NET
            if (OperatingSystem.IsWindows())
            {
                return full.ToLowerInvariant();
            }
            return full;
#else
            // net472 always Windows.
            return full.ToLowerInvariant();
#endif
        }

        // ---------------------------------------------------------------------
        // Signature enumeration
        // ---------------------------------------------------------------------

        private readonly struct SignatureInput
        {
            public SignatureInput(string fullPath, string relativePath, long size, string sha256)
            {
                FullPath = fullPath;
                RelativePath = relativePath;
                Size = size;
                Sha256 = sha256;
            }
            public string FullPath { get; }
            public string RelativePath { get; }
            public long Size { get; }
            public string Sha256 { get; }
        }

        private static List<SignatureInput> EnumerateSignatureInputs(string projectFullPath)
        {
            var list = new List<SignatureInput>(64);
            string projDir = Path.GetDirectoryName(projectFullPath);
            if (string.IsNullOrEmpty(projDir))
            {
                return list;
            }

            // Source-like files under the project directory (recursive, with exclusions).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnumerateSourceFiles(projDir, projDir, list, seen);

            // obj/project.assets.json, obj/project.nuget.cache, dgspec/g.props/g.targets.
            string objDir = Path.Combine(projDir, "obj");
            if (Directory.Exists(objDir))
            {
                foreach (string name in s_objIncludes)
                {
                    string p = Path.Combine(objDir, name);
                    if (File.Exists(p))
                    {
                        AddSignature(p, projDir, list, seen);
                    }
                }
                try
                {
                    foreach (string p in Directory.EnumerateFiles(objDir, "*.nuget.dgspec.json"))
                    {
                        AddSignature(p, projDir, list, seen);
                    }
                    foreach (string p in Directory.EnumerateFiles(objDir, "*.nuget.g.props"))
                    {
                        AddSignature(p, projDir, list, seen);
                    }
                    foreach (string p in Directory.EnumerateFiles(objDir, "*.nuget.g.targets"))
                    {
                        AddSignature(p, projDir, list, seen);
                    }
                }
                catch
                {
                    // Ignore – the cache key only weakens slightly, the build will run anyway.
                }
            }

            // Directory.Build.{props,targets}, Directory.Packages.props, global.json, NuGet.config,
            // .editorconfig walking up to the filesystem root.
            string current = projDir;
            while (!string.IsNullOrEmpty(current))
            {
                foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config", ".editorconfig" })
                {
                    string p = Path.Combine(current, name);
                    if (File.Exists(p))
                    {
                        AddSignature(p, projDir, list, seen);
                    }
                }
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }

            // Stable ordering for a stable cache key.
            list.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void EnumerateSourceFiles(string root, string projDir, List<SignatureInput> sink, HashSet<string> seen)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                {
                    string name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                    {
                        // Skip dot-directories and dot-files (e.g. .vs, .git, .DS_Store).
                        continue;
                    }
                    if (Directory.Exists(entry))
                    {
                        if (s_excludedDirs.Contains(name))
                        {
                            continue;
                        }
                        EnumerateSourceFiles(entry, projDir, sink, seen);
                    }
                    else if (File.Exists(entry))
                    {
                        string ext = Path.GetExtension(entry);
                        if (s_sourceExts.Contains(ext) || StrictModeSettings.GetExtraInputExtensions().Contains(ext))
                        {
                            AddSignature(entry, projDir, sink, seen);
                        }
                    }
                }
            }
            catch
            {
                // Best-effort: if a directory becomes unreadable mid-walk we accept a slightly
                // weaker cache key – the build itself will fail (or succeed) anyway.
            }
        }

        private static void AddSignature(string fullPath, string projDir, List<SignatureInput> sink, HashSet<string> seen)
        {
            try
            {
                string full = Path.GetFullPath(fullPath);
                if (!seen.Add(full))
                {
                    return;
                }
                var fi = new FileInfo(full);
                if (!fi.Exists)
                {
                    return;
                }
                long size = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;
                string hash;
                if (s_fileHashCache.TryGetValue(full, out var rec) && rec.Ticks == ticks && rec.Size == size)
                {
                    hash = rec.Hash;
                }
                else
                {
                    hash = Sha256File(full);
                    s_fileHashCache[full] = (ticks, size, hash);
                }
                string rel = MakeRelative(full, projDir);
                sink.Add(new SignatureInput(full, rel, size, hash));
            }
            catch
            {
                // Skip unreadable file – degrades cache key precision but is safe.
            }
        }

        private static string MakeRelative(string fullPath, string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir))
            {
                return fullPath;
            }
            try
            {
                string b = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(b + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath.Substring(b.Length + 1);
                }
            }
            catch
            {
            }
            return fullPath;
        }

        // ---------------------------------------------------------------------
        // SHA256 helpers
        // ---------------------------------------------------------------------

        private static string Sha256Hex(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
#if NET
            byte[] hash = SHA256.HashData(bytes);
#else
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
#endif
            return ToHex(hash);
        }

        private static string Sha256File(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#if NET
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    inc.AppendData(buf, 0, n);
                }
                return ToHex(inc.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
#else
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(fs));
#endif
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        private static void EmitTelemetry(string outcome, string project, IReadOnlyList<string> targets, string reason, System.Diagnostics.Stopwatch sw, int fileCount, string cacheKey)
        {
            try
            {
                if (!StrictTelemetry.IsEnabled)
                {
                    return;
                }
                string target = (targets is not null && targets.Count > 0) ? string.Join(",", targets) : null;
                long? us = sw is null ? (long?)null : (sw.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
                StrictTelemetry.Emit(
                    layer: "project-fastskip",
                    outcome: outcome,
                    project: project,
                    target: target,
                    reason: reason,
                    durationUs: us,
                    fileCount: fileCount,
                    cacheKey: cacheKey);
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------------
        // CachedBuild – materialise a manifest into a BuildResult
        // ---------------------------------------------------------------------

        internal sealed class CachedBuild
        {
            private readonly Manifest _manifest;

            internal CachedBuild(Manifest m)
            {
                _manifest = m;
            }

            /// <summary>
            /// Populate a fresh <see cref="BuildResult"/> with cached <see cref="TargetResult"/>s.
            /// Only targets present in <paramref name="requestedTargets"/> are added (safety
            /// invariant #1: never return a result for a target we did not cache; here we go
            /// further and project down to exactly the requested set so callers cannot observe
            /// "extra" targets that happened to be cached together).
            /// </summary>
            public void PopulateBuildResult(BuildResult result, IReadOnlyList<string> requestedTargets)
            {
                if (result is null)
                {
                    return;
                }
                HashSet<string> requested = null;
                if (requestedTargets is not null && requestedTargets.Count > 0)
                {
                    requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < requestedTargets.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(requestedTargets[i]))
                        {
                            requested.Add(requestedTargets[i]);
                        }
                    }
                }

                foreach (CachedTargetResult ctr in _manifest.TargetResults)
                {
                    if (requested is not null && !requested.Contains(ctr.TargetName))
                    {
                        continue;
                    }

                    var items = new ProjectItemInstance.TaskItem[ctr.Items.Count];
                    for (int i = 0; i < ctr.Items.Count; i++)
                    {
                        CachedItem ci = ctr.Items[i];
                        var ti = new ProjectItemInstance.TaskItem(ci.ItemSpec ?? string.Empty, definingFileEscaped: null);
                        if (ci.Metadata is not null)
                        {
                            for (int j = 0; j < ci.Metadata.Count; j++)
                            {
                                var kv = ci.Metadata[j];
                                try
                                {
                                    ti.SetMetadata(kv.Key, kv.Value ?? string.Empty);
                                }
                                catch
                                {
                                    // Skip metadata we can't set (e.g. reserved names).
                                }
                            }
                        }
                        items[i] = ti;
                    }
                    var work = new WorkUnitResult(WorkUnitResultCode.Success, WorkUnitActionCode.Continue, null);
                    var tr = new TargetResult(items, work);
                    result.AddResultsForTarget(ctr.TargetName, tr);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Manifest (de)serialisation – simple binary format under obj\.strict-project\
        // ---------------------------------------------------------------------

        internal sealed class Manifest
        {
            public int SchemaVersion;
            public string CacheKey;
            public string ProjectFullPath;
            public long CreatedUtcTicks;
            public List<string> Targets;
            public List<KeyValuePair<string, string>> GlobalProperties;
            public List<CachedTargetResult> TargetResults;
            public List<OutputFileStamp> OutputFiles;

            public static Manifest Load(string path)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var br = new BinaryReader(fs, Encoding.UTF8);
                    var m = new Manifest
                    {
                        SchemaVersion = br.ReadInt32(),
                        CacheKey = br.ReadString(),
                        ProjectFullPath = br.ReadString(),
                        CreatedUtcTicks = br.ReadInt64(),
                        Targets = new List<string>(),
                        GlobalProperties = new List<KeyValuePair<string, string>>(),
                        TargetResults = new List<CachedTargetResult>(),
                        OutputFiles = new List<OutputFileStamp>(),
                    };

                    int n = br.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        m.Targets.Add(br.ReadString());
                    }
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        string k = br.ReadString();
                        string v = br.ReadString();
                        m.GlobalProperties.Add(new KeyValuePair<string, string>(k, v));
                    }
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var ctr = new CachedTargetResult
                        {
                            TargetName = br.ReadString(),
                            ResultCode = br.ReadInt32(),
                            Items = new List<CachedItem>(),
                        };
                        int ni = br.ReadInt32();
                        for (int j = 0; j < ni; j++)
                        {
                            var ci = new CachedItem
                            {
                                ItemSpec = br.ReadString(),
                                Metadata = new List<KeyValuePair<string, string>>(),
                            };
                            int nm = br.ReadInt32();
                            for (int k = 0; k < nm; k++)
                            {
                                string mk = br.ReadString();
                                string mv = br.ReadString();
                                ci.Metadata.Add(new KeyValuePair<string, string>(mk, mv));
                            }
                            ctr.Items.Add(ci);
                        }
                        m.TargetResults.Add(ctr);
                    }
                    n = br.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        string p = br.ReadString();
                        long sz = br.ReadInt64();
                        long ticks = br.ReadInt64();
                        m.OutputFiles.Add(new OutputFileStamp(p, sz, ticks));
                    }
                    return m;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void Save(string manifestPath, Manifest m)
        {
            string dir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                SweepStaleTempSiblings(manifestPath, dir);
            }
            // Multi-proc safety: write to a unique temp sibling then atomically rename.
            string tmp = manifestPath + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                bw.Write(m.SchemaVersion);
                bw.Write(m.CacheKey ?? string.Empty);
                bw.Write(m.ProjectFullPath ?? string.Empty);
                bw.Write(m.CreatedUtcTicks);

                bw.Write(m.Targets?.Count ?? 0);
                if (m.Targets is not null)
                {
                    for (int i = 0; i < m.Targets.Count; i++)
                    {
                        bw.Write(m.Targets[i] ?? string.Empty);
                    }
                }

                bw.Write(m.GlobalProperties?.Count ?? 0);
                if (m.GlobalProperties is not null)
                {
                    for (int i = 0; i < m.GlobalProperties.Count; i++)
                    {
                        var kv = m.GlobalProperties[i];
                        bw.Write(kv.Key ?? string.Empty);
                        bw.Write(kv.Value ?? string.Empty);
                    }
                }

                bw.Write(m.TargetResults?.Count ?? 0);
                if (m.TargetResults is not null)
                {
                    for (int i = 0; i < m.TargetResults.Count; i++)
                    {
                        CachedTargetResult ctr = m.TargetResults[i];
                        bw.Write(ctr.TargetName ?? string.Empty);
                        bw.Write(ctr.ResultCode);
                        bw.Write(ctr.Items?.Count ?? 0);
                        if (ctr.Items is not null)
                        {
                            for (int j = 0; j < ctr.Items.Count; j++)
                            {
                                CachedItem ci = ctr.Items[j];
                                bw.Write(ci.ItemSpec ?? string.Empty);
                                bw.Write(ci.Metadata?.Count ?? 0);
                                if (ci.Metadata is not null)
                                {
                                    for (int k = 0; k < ci.Metadata.Count; k++)
                                    {
                                        var kv = ci.Metadata[k];
                                        bw.Write(kv.Key ?? string.Empty);
                                        bw.Write(kv.Value ?? string.Empty);
                                    }
                                }
                            }
                        }
                    }
                }

                bw.Write(m.OutputFiles?.Count ?? 0);
                if (m.OutputFiles is not null)
                {
                    for (int i = 0; i < m.OutputFiles.Count; i++)
                    {
                        OutputFileStamp of = m.OutputFiles[i];
                        bw.Write(of.Path ?? string.Empty);
                        bw.Write(of.Size);
                        bw.Write(of.Ticks);
                    }
                }
            }

            // Atomic publish – overwrites prior manifest if any.
#if NET
            File.Move(tmp, manifestPath, overwrite: true);
#else
            if (File.Exists(manifestPath))
            {
                File.Replace(tmp, manifestPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, manifestPath);
            }
#endif
        }

        private static void SweepStaleTempSiblings(string manifestPath, string dir)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - s_orphanTempSweepAge;
                string pattern = Path.GetFileName(manifestPath) + ".tmp.*";
                foreach (string tmpPath in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(tmpPath) <= cutoff)
                        {
                            File.Delete(tmpPath);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        internal sealed class CachedTargetResult
        {
            public string TargetName;
            public int ResultCode;
            public List<CachedItem> Items;
        }

        internal sealed class CachedItem
        {
            public string ItemSpec;
            public List<KeyValuePair<string, string>> Metadata;
        }

        internal readonly struct OutputFileStamp
        {
            public OutputFileStamp(string path, long size, long ticks)
            {
                Path = path;
                Size = size;
                Ticks = ticks;
            }
            public string Path { get; }
            public long Size { get; }
            public long Ticks { get; }
        }

        private sealed class PendingRecord
        {
            public PendingRecord(string projectFullPath, List<KeyValuePair<string, string>> globalProperties, List<string> targets, string cacheKey)
            {
                ProjectFullPath = projectFullPath;
                GlobalProperties = globalProperties;
                Targets = targets;
                CacheKey = cacheKey;
            }
            public string ProjectFullPath { get; }
            public List<KeyValuePair<string, string>> GlobalProperties { get; }
            public List<string> Targets { get; }
            public string CacheKey { get; }
        }

        // ---------------------------------------------------------------------
        // Test hooks
        // ---------------------------------------------------------------------

        /// <summary>Clears the in-memory file hash cache. For unit tests only.</summary>
        internal static void ResetCachesForTest()
        {
            s_fileHashCache.Clear();
            s_pending.Clear();
            s_servedFromCache.Clear();
        }
    }
}
