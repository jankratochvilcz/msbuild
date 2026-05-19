// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Strict;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    using ILoggingService = Microsoft.Build.BackEnd.Logging.ILoggingService;

    /// <summary>
    /// Engine-side "strict mode" cache for individual targets.
    ///
    /// When <c>MSBuildStrictMode</c> is true (project property or <c>MSBUILDSTRICTMODE=1</c>
    /// environment variable), every target that declares both <c>Inputs</c> and <c>Outputs</c>
    /// becomes content-hash-cacheable across builds, without any author-side wrapping.
    ///
    /// Lookup keys are derived from:
    ///   * project file full path  (so two projects sharing a target file don't collide)
    ///   * target name
    ///   * SHA-256 of every declared input file's *content* (sorted, normalised)
    ///   * the set of declared output paths (so renames invalidate)
    ///
    /// On a hit, declared output files are restored from the cache directory and the
    /// target is reported as <see cref="DependencyAnalysisResult.SkipUpToDate"/>.
    /// On a miss, a snapshot of the project's intermediate-output directory is taken so
    /// that <see cref="PersistOnSuccess"/> – invoked by <c>TargetEntry</c> after the
    /// target completes successfully – can capture both declared *and* incidentally-written
    /// outputs (compensates for under-declared <c>Outputs=</c>, which is common in practice
    /// and impossible to enforce on macOS without sandboxing).
    ///
    /// The cache is rooted at <c>$(BaseIntermediateOutputPath).strict-cache</c> so
    /// <c>dotnet clean</c> retains it, while <c>git clean -dxf</c> nukes it.
    /// </summary>
    internal sealed class StrictTargetCache
    {
        internal enum Mode { Off, Warn, Enforce }

        /// <summary>Default 1 GiB. Override via $(MSBuildStrictCacheMaxBytes) or MSBUILDSTRICTCACHEMAXBYTES.</summary>
        private const long DefaultCacheMaxBytes = 1024L * 1024L * 1024L;

        internal static bool IsEnabled(ProjectInstance project) => GetMode(project) != Mode.Off;

        internal static Mode GetMode(ProjectInstance project)
        {
            // Re-read the env var on every call (not cached) so MSBuild Server / long-lived nodes pick up changes.
            Mode envMode = ParseMode(Environment.GetEnvironmentVariable("MSBUILDSTRICTMODE"));
            if (envMode != Mode.Off)
            {
                return envMode;
            }
            return ParseMode(project?.GetPropertyValue("MSBuildStrictMode"));
        }

        /// <summary>True if this target is named in $(MSBuildStrictExemptTargets).</summary>
        internal static bool IsTargetExempt(ProjectInstance project, string targetName)
        {
            if (project == null || string.IsNullOrEmpty(targetName))
            {
                return false;
            }
            string list = project.GetPropertyValue("MSBuildStrictExemptTargets");
            if (string.IsNullOrEmpty(list))
            {
                return false;
            }
            foreach (string name in list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(name.Trim(), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static Mode ParseMode(string val)
        {
            if (string.IsNullOrEmpty(val))
            {
                return Mode.Off;
            }
            if (string.Equals(val, "enforce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(val, "strict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(val, "error", StringComparison.OrdinalIgnoreCase))
            {
                return Mode.Enforce;
            }
            if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(val, "1", StringComparison.Ordinal)
                || string.Equals(val, "warn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(val, "on", StringComparison.OrdinalIgnoreCase))
            {
                return Mode.Warn;
            }
            return Mode.Off;
        }

        private readonly ProjectInstance _project;
        private readonly string _targetName;
        private readonly ILoggingService _log;
        private readonly BuildEventContext _ctx;
        private readonly Mode _mode;

        // Populated on miss; consumed by PersistOnSuccess.
        private string _cacheDir;
        private string _preSnapshotFile;
        private string _extraPreSnapshotFile;
        private string _intermediateRoot;
        private List<string> _extraObservationDirs;
        private List<string> _declaredOutputs;
        private HashSet<string> _declaredOutputsSet;
        // Roots (absolute) where the target is allowed to write files outside obj/.
        // Populated from <StrictAllowedOutputDirs> on the project (semicolon list, project-relative
        // or absolute). Any write under these dirs is treated as a legitimate (declared) output,
        // is captured into the cache for replay, and does NOT trigger MSBSTRICT001.
        private List<string> _allowedOutputDirs;
        // True if a strict-mode violation was detected; consumed by HasViolation/PersistOnSuccess.
        private bool _violationDetected;

        internal StrictTargetCache(ProjectInstance project, string targetName, ILoggingService log, BuildEventContext ctx)
        {
            _project = project;
            _targetName = targetName;
            _log = log;
            _ctx = ctx;
            _mode = GetMode(project);
        }

        /// <summary>True if a miss was recorded and PersistOnSuccess should be called.</summary>
        internal bool HasPendingPersist => _cacheDir != null;

        /// <summary>True if PersistOnSuccess detected an enforce-mode violation.</summary>
        internal bool HasViolation => _violationDetected;

        internal Mode CurrentMode => _mode;

        /// <summary>
        /// Look up the strict cache for this (project, target, inputs, outputs) tuple.
        /// On hit, restores declared outputs and returns <c>true</c>.
        /// On miss, snapshots the intermediate-output tree for later diff and returns <c>false</c>.
        /// </summary>
        internal bool TryRestore(IList<string> inputs, IList<string> outputs)
        {
            try
            {
                if (inputs == null || inputs.Count == 0 || outputs == null || outputs.Count == 0)
                {
                    return false;
                }

                // Inputs must all exist on disk to participate in the cache.
                var resolvedInputs = new List<string>(inputs.Count);
                foreach (var i in inputs)
                {
                    string p = Path.Combine(_project.Directory, i);
                    if (!File.Exists(p))
                    {
                        Log(MessageImportance.Low, $"[strict] '{_targetName}': declared input '{p}' missing; bypassing cache.");
                        return false;
                    }
                    resolvedInputs.Add(p);
                }

                string key = ComputeKey(resolvedInputs, outputs);
                string cacheRoot = Path.Combine(GetCacheRoot(), SafeSegment(_targetName));
                string cacheDir = Path.Combine(cacheRoot, key);

                _declaredOutputs = new List<string>(outputs.Count);
                _declaredOutputsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in outputs)
                {
                    string abs = Path.GetFullPath(Path.Combine(_project.Directory, o));
                    _declaredOutputs.Add(abs);
                    _declaredOutputsSet.Add(abs);
                }

                _intermediateRoot = GetIntermediateRoot();
                _allowedOutputDirs = ParseAllowedOutputDirs();

                if (File.Exists(Path.Combine(cacheDir, ".ok")))
                {
                    var swHit = System.Diagnostics.Stopwatch.StartNew();
                    int restored = RestoreOutputs(cacheDir);
                    swHit.Stop();
                    Log(MessageImportance.High,
                        $"[strict] HIT  {_targetName}  key={key.Substring(0, 10)}  restored {restored} file(s)");
                    if (StrictTelemetry.IsEnabled)
                    {
                        StrictTelemetry.Emit(
                            layer: "target-cache",
                            outcome: "hit",
                            project: _project.FullPath,
                            target: _targetName,
                            durationUs: swHit.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000),
                            fileCount: restored,
                            cacheKey: key);
                    }
                    return true;
                }

                _cacheDir = cacheDir;
                if (_intermediateRoot != null)
                {
                    // Snapshot even if the dir does not yet exist (treated as empty); the target
                    // body itself may create it. Without this, undeclared writes into a freshly
                    // created obj/ are invisible to the diff and slip past enforcement.
                    _preSnapshotFile = Path.Combine(Path.GetTempPath(),
                        "msb-strict-" + key + "-" + Guid.NewGuid().ToString("N") + ".snap");
                    WriteSnapshot(_preSnapshotFile, SnapshotTree(_intermediateRoot));
                }

                // Extra observation: top-level files in the project directory and the parent
                // directory of each declared output (deduped, excluding IntermediateOutputPath
                // which is already covered above). Catches under-declared targets that write
                // outside obj/ (e.g. into the project root or bin/).
                _extraObservationDirs = BuildExtraObservationDirs();
                if (_extraObservationDirs.Count > 0)
                {
                    _extraPreSnapshotFile = Path.Combine(Path.GetTempPath(),
                        "msb-strict-extra-" + key + "-" + Guid.NewGuid().ToString("N") + ".snap");
                    WriteSnapshot(_extraPreSnapshotFile, SnapshotFlat(_extraObservationDirs));
                }

                Log(MessageImportance.High,
                    $"[strict] MISS {_targetName}  key={key.Substring(0, 10)}");
                if (StrictTelemetry.IsEnabled)
                {
                    StrictTelemetry.Emit(
                        layer: "target-cache",
                        outcome: "miss",
                        project: _project.FullPath,
                        target: _targetName,
                        fileCount: outputs.Count,
                        cacheKey: key);
                }
                return false;
            }
            catch (Exception ex)
            {
                // Cache must never break a build. Log and fall back to normal execution.
                Log(MessageImportance.Low, $"[strict] cache lookup failed for {_targetName}: {ex.Message}");
                _cacheDir = null;
                return false;
            }
        }

        /// <summary>
        /// After a target runs successfully, capture declared outputs plus any incidental
        /// files written under the intermediate-output dir and persist them under the cache
        /// key recorded by <see cref="TryRestore"/>.
        /// </summary>
        internal void PersistOnSuccess()
        {
            if (_cacheDir == null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(_cacheDir);
                int stored = 0;

                // Declared outputs.
                string declDir = Path.Combine(_cacheDir, "out", "decl");
                Directory.CreateDirectory(declDir);
                for (int i = 0; i < _declaredOutputs.Count; i++)
                {
                    string src = _declaredOutputs[i];
                    if (!File.Exists(src))
                    {
                        Log(MessageImportance.Low,
                            $"[strict] declared output '{src}' was not produced; skipping cache persistence for {_targetName}.");
                        return;
                    }
                    string destName = i.ToString("D4") + "_" + SafeSegment(Path.GetFileName(src));
                    LinkOrCopyForStore(src, Path.Combine(declDir, destName));
                    stored++;
                }

                // Auto-observed outputs (diff against pre-snapshot). Anything written under
                // IntermediateOutputPath that wasn't declared in Outputs= is an "undeclared write".
                // In Warn mode we log a warning and still cache it (so cache restoration is correct).
                // In Enforce mode we log an error, abort cache persistence, and surface the violation
                // to the caller so it can fail the build.
                var observed = new List<string>();
                var undeclared = new List<string>();
                if (_intermediateRoot != null && _preSnapshotFile != null && File.Exists(_preSnapshotFile))
                {
                    Dictionary<string, string> pre = ReadSnapshot(_preSnapshotFile);
                    Dictionary<string, string> post = SnapshotTree(_intermediateRoot);
                    string obsDir = Path.Combine(_cacheDir, "out", "obs");
                    foreach (KeyValuePair<string, string> kv in post)
                    {
                        if (pre.TryGetValue(kv.Key, out string oldStamp) && oldStamp == kv.Value)
                        {
                            continue;
                        }
                        string srcAbs = Path.Combine(_intermediateRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(srcAbs))
                        {
                            continue;
                        }

                        // Classify: declared output vs undeclared.
                        string srcFull = Path.GetFullPath(srcAbs);
                        bool isDeclared = _declaredOutputsSet.Contains(srcFull);

                        if (!isDeclared)
                        {
                            undeclared.Add(srcFull);
                        }

                        string destAbs = Path.Combine(obsDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destAbs));
                        LinkOrCopyForStore(srcAbs, destAbs);
                        observed.Add(kv.Key);
                        stored++;
                    }
                    WriteObservedList(_cacheDir, observed);
                    TryDelete(_preSnapshotFile);
                }

                // Extra observation: detect undeclared writes OUTSIDE IntermediateOutputPath
                // (e.g. into the project root or bin/). These are NEVER cached for replay
                // (we don't want to teach the cache to litter those locations); they are only
                // diagnostics.
                var extraUndeclared = new List<string>();
                if (_extraPreSnapshotFile != null && File.Exists(_extraPreSnapshotFile) && _extraObservationDirs != null)
                {
                    Dictionary<string, string> prex = ReadSnapshot(_extraPreSnapshotFile);
                    Dictionary<string, string> postx = SnapshotFlat(_extraObservationDirs);
                    foreach (KeyValuePair<string, string> kv in postx)
                    {
                        if (prex.TryGetValue(kv.Key, out string oldStamp) && oldStamp == kv.Value)
                        {
                            continue;
                        }
                        if (_declaredOutputsSet.Contains(kv.Key))
                        {
                            continue;
                        }
                        extraUndeclared.Add(kv.Key);
                    }
                    TryDelete(_extraPreSnapshotFile);
                }

                var allUndeclared = new List<string>(undeclared.Count + extraUndeclared.Count);
                allUndeclared.AddRange(undeclared);
                allUndeclared.AddRange(extraUndeclared);

                // Suppress diagnostics (and cache them as observed when under intermediate root)
                // for writes that fall under a user-declared <StrictAllowedOutputDirs> root.
                if (_allowedOutputDirs != null && _allowedOutputDirs.Count > 0)
                {
                    allUndeclared.RemoveAll(p =>
                    {
                        foreach (string root in _allowedOutputDirs)
                        {
                            if (IsUnderOrEqual(p, root))
                            {
                                return true;
                            }
                        }
                        return false;
                    });
                }

                if (allUndeclared.Count > 0)
                {
                    string projRel = Path.GetFullPath(_project.Directory);
                    foreach (string u in allUndeclared)
                    {
                        string display = u.StartsWith(projRel, StringComparison.OrdinalIgnoreCase)
                            ? u.Substring(projRel.Length).TrimStart(Path.DirectorySeparatorChar, '/')
                            : u;
                        string msg = $"Target '{_targetName}' wrote '{display}' which is not declared in Outputs=. Strict mode requires every produced file to be declared.";
                        if (_mode == Mode.Enforce)
                        {
                            LogStrictError("MSBSTRICT001", msg);
                        }
                        else
                        {
                            LogStrictWarning("MSBSTRICT001", msg);
                        }
                    }
                    if (_mode == Mode.Enforce)
                    {
                        _violationDetected = true;
                        // Do not write .ok – cache must not contain a build that violated strict mode.
                        Log(MessageImportance.High,
                            $"[strict] BLOCK {_targetName}  {allUndeclared.Count} undeclared write(s); cache entry discarded.");
                        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
                        return;
                    }
                }

                File.WriteAllText(Path.Combine(_cacheDir, ".ok"), DateTime.UtcNow.ToString("O"));
                Log(MessageImportance.High,
                    $"[strict] STORE {_targetName}  {stored} file(s) ({_declaredOutputs.Count} declared, {observed.Count} observed)");
                if (StrictTelemetry.IsEnabled)
                {
                    StrictTelemetry.Emit(
                        layer: "target-cache",
                        outcome: "store",
                        project: _project.FullPath,
                        target: _targetName,
                        fileCount: stored);
                }

                // Best-effort cache size cap.
                EvictIfOversized(GetCacheRoot());
            }
            catch (Exception ex)
            {
                Log(MessageImportance.Low, $"[strict] cache persist failed for {_targetName}: {ex.Message}");
            }
            finally
            {
                _cacheDir = null;
            }
        }

        private int RestoreOutputs(string cacheDir)
        {
            int restored = 0;
            string declDir = Path.Combine(cacheDir, "out", "decl");
            for (int i = 0; i < _declaredOutputs.Count; i++)
            {
                string src = Path.Combine(declDir, i.ToString("D4") + "_" + SafeSegment(Path.GetFileName(_declaredOutputs[i])));
                if (File.Exists(src))
                {
                    LinkFresh(src, _declaredOutputs[i]);
                    restored++;
                }
            }
            string obsList = Path.Combine(cacheDir, "observed.list");
            string obsDir = Path.Combine(cacheDir, "out", "obs");
            if (File.Exists(obsList) && _intermediateRoot != null && Directory.Exists(obsDir))
            {
                foreach (string rel in File.ReadAllLines(obsList))
                {
                    if (rel.Length == 0)
                    {
                        continue;
                    }
                    string src = Path.Combine(obsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    string dest = Path.Combine(_intermediateRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(src))
                    {
                        LinkFresh(src, dest);
                        restored++;
                    }
                }
            }
            return restored;
        }

        private string ComputeKey(IList<string> resolvedInputs, IList<string> outputs)
        {
            // Per-project sidecar (path -> mtime|size|hash) is loaded ONCE per process and
            // shared in-memory across all targets/projects via s_globalHashCache. We re-use
            // a hash whenever (mtime, size) match, avoiding the byte-read for unchanged inputs.
            string sidecarPath = GetInputHashSidecarPath();
            EnsureSidecarLoaded(sidecarPath);
            bool sidecarDirty = false;

            var sb = new StringBuilder();
            sb.Append("project=").Append(_project.FullPath ?? "").Append('\n');
            sb.Append("target=").Append(_targetName).Append('\n');

            var sortedInputs = new List<string>(resolvedInputs);
            sortedInputs.Sort(StringComparer.Ordinal);

            string[] hashes = new string[sortedInputs.Count];
            bool localDirty = false;
            if (sortedInputs.Count >= 8)
            {
                // Parallel hash: cheap for cache hits (stat-only), big win for cold builds
                // where CoreCompile-like targets pull dozens of ref assemblies.
                System.Threading.Tasks.Parallel.For(0, sortedInputs.Count, i =>
                {
                    bool d = false;
                    hashes[i] = GetOrComputeInputHash(sortedInputs[i], ref d);
                    if (d) { localDirty = true; }
                });
            }
            else
            {
                for (int i = 0; i < sortedInputs.Count; i++)
                {
                    hashes[i] = GetOrComputeInputHash(sortedInputs[i], ref localDirty);
                }
            }
            sidecarDirty |= localDirty;
            for (int i = 0; i < sortedInputs.Count; i++)
            {
                sb.Append("in:").Append(NormalizeRel(sortedInputs[i])).Append('|').Append(hashes[i]).Append('\n');
            }
            var sortedOutputs = new List<string>(outputs);
            sortedOutputs.Sort(StringComparer.Ordinal);
            foreach (string o in sortedOutputs)
            {
                sb.Append("out:").Append(NormalizeRel(o)).Append('\n');
            }

            if (sidecarDirty)
            {
                s_dirtySidecars[sidecarPath] = 1;
            }

            return Sha256Hex(sb.ToString());
        }

        private readonly struct InputHashRecord
        {
            public InputHashRecord(long ticks, long size, string hash)
            {
                Ticks = ticks; Size = size; Hash = hash;
            }
            public long Ticks { get; }
            public long Size { get; }
            public string Hash { get; }
        }

        // Process-wide hash cache: (fullPath -> InputHashRecord). Shared across targets and
        // projects so the same response file / ref assembly is hashed at most once per process.
        private static readonly ConcurrentDictionary<string, InputHashRecord> s_globalHashCache
            = new(StringComparer.OrdinalIgnoreCase);

        // Sidecars whose contents have been loaded into s_globalHashCache.
        private static readonly ConcurrentDictionary<string, byte> s_loadedSidecars
            = new(StringComparer.OrdinalIgnoreCase);

        // Sidecar paths with pending writes. Flushed at process exit and opportunistically.
        private static readonly ConcurrentDictionary<string, byte> s_dirtySidecars
            = new(StringComparer.OrdinalIgnoreCase);

        // Per-sidecar lock for batched flushes.
        private static readonly ConcurrentDictionary<string, object> s_sidecarLocks
            = new(StringComparer.OrdinalIgnoreCase);

        private static int s_exitHookInstalled;

        private void EnsureSidecarLoaded(string sidecarPath)
        {
            if (s_loadedSidecars.ContainsKey(sidecarPath))
            {
                return;
            }
            object gate = s_sidecarLocks.GetOrAdd(sidecarPath, _ => new object());
            lock (gate)
            {
                if (s_loadedSidecars.ContainsKey(sidecarPath))
                {
                    return;
                }
                LoadInputHashSidecarInto(sidecarPath, s_globalHashCache);
                s_loadedSidecars[sidecarPath] = 1;
            }
            if (System.Threading.Interlocked.CompareExchange(ref s_exitHookInstalled, 1, 0) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushAllDirtySidecars();
            }
        }

        private static string GetOrComputeInputHash(string path, ref bool dirty)
        {
            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { return HashFile(path); }
            long ticks = fi.LastWriteTimeUtc.Ticks;
            long size = fi.Length;
            string key = Path.GetFullPath(path);
            if (s_globalHashCache.TryGetValue(key, out InputHashRecord rec) && rec.Ticks == ticks && rec.Size == size)
            {
                return rec.Hash;
            }
            string hash = HashFile(path);
            s_globalHashCache[key] = new InputHashRecord(ticks, size, hash);
            dirty = true;
            return hash;
        }

        private string GetInputHashSidecarPath()
            => Path.Combine(GetCacheRoot(), "inputs.stamp");

        private static void LoadInputHashSidecarInto(string path, ConcurrentDictionary<string, InputHashRecord> dest)
        {
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    // Format: ticks|size|hash|absPath  (absPath last to tolerate '|' in unlikely names)
                    int p1 = line.IndexOf('|');
                    int p2 = line.IndexOf('|', p1 + 1);
                    int p3 = line.IndexOf('|', p2 + 1);
                    if (p1 < 0 || p2 < 0 || p3 < 0) { continue; }
                    if (!long.TryParse(line.Substring(0, p1), out long ticks)) { continue; }
                    if (!long.TryParse(line.Substring(p1 + 1, p2 - p1 - 1), out long size)) { continue; }
                    string hash = line.Substring(p2 + 1, p3 - p2 - 1);
                    string abs = line.Substring(p3 + 1);
                    // Don't clobber a fresher entry (another sidecar may have already loaded a newer mtime).
                    dest.TryAdd(abs, new InputHashRecord(ticks, size, hash));
                }
            }
            catch { }
        }

        internal static void FlushAllDirtySidecars()
        {
            foreach (var kv in s_dirtySidecars)
            {
                string sidecarPath = kv.Key;
                if (!s_dirtySidecars.TryRemove(sidecarPath, out _))
                {
                    continue;
                }
                object gate = s_sidecarLocks.GetOrAdd(sidecarPath, _ => new object());
                lock (gate)
                {
                    FlushSidecar(sidecarPath);
                }
            }
        }

        private static void FlushSidecar(string sidecarPath)
        {
            // Write entries from the global hash cache whose absolute path lives under the
            // sidecar's project root. Path prefix matching is OS-case-correct on Windows.
            string root = Path.GetDirectoryName(Path.GetDirectoryName(sidecarPath)); // strip .strict-cache/inputs.stamp
            if (string.IsNullOrEmpty(root))
            {
                return;
            }
            string projectRoot = Path.GetDirectoryName(root); // strip "obj"
            if (string.IsNullOrEmpty(projectRoot))
            {
                projectRoot = root;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath));
                using var w = new StreamWriter(sidecarPath);
                foreach (var kv in s_globalHashCache)
                {
                    if (!kv.Key.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    w.Write(kv.Value.Ticks);
                    w.Write('|');
                    w.Write(kv.Value.Size);
                    w.Write('|');
                    w.Write(kv.Value.Hash);
                    w.Write('|');
                    w.Write(kv.Key);
                    w.Write('\n');
                }
            }
            catch { }
        }

        private string NormalizeRel(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(_project.Directory);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            return full.Replace('\\', '/');
        }

        private string GetCacheRoot()
        {
            string baseIntermediate = _project.GetPropertyValue("BaseIntermediateOutputPath");
            if (string.IsNullOrEmpty(baseIntermediate))
            {
                baseIntermediate = "obj" + Path.DirectorySeparatorChar;
            }
            string full = Path.Combine(_project.Directory, baseIntermediate);
            return Path.Combine(full, ".strict-cache");
        }

        private string GetIntermediateRoot()
        {
            string intermediate = _project.GetPropertyValue("IntermediateOutputPath");
            if (string.IsNullOrEmpty(intermediate))
            {
                return null;
            }
            return Path.GetFullPath(Path.Combine(_project.Directory, intermediate));
        }

        private static Dictionary<string, string> SnapshotTree(string root)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root))
            {
                return dict;
            }
            string full = Path.GetFullPath(root);
            foreach (string f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                // Don't snapshot our own cache directory or we get infinite feedback.
                if (f.IndexOf(Path.DirectorySeparatorChar + ".strict-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                var fi = new FileInfo(f);
                string rel = f.Substring(full.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                dict[rel] = fi.LastWriteTimeUtc.Ticks.ToString() + "|" + fi.Length.ToString();
            }
            return dict;
        }

        private static void WriteSnapshot(string path, Dictionary<string, string> snap)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var w = new StreamWriter(path);
            foreach (var kv in snap)
            {
                w.Write(kv.Key);
                w.Write('\0');
                w.Write(kv.Value);
                w.Write('\n');
            }
        }

        private static Dictionary<string, string> ReadSnapshot(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                int sep = line.IndexOf('\0');
                if (sep < 0)
                {
                    continue;
                }
                dict[line.Substring(0, sep)] = line.Substring(sep + 1);
            }
            return dict;
        }

        private static void WriteObservedList(string cacheDir, List<string> observed)
        {
            File.WriteAllLines(Path.Combine(cacheDir, "observed.list"), observed);
        }

        /// <summary>
        /// Store path: copy from build output into the cache. Skips the copy if the destination
        /// already exists with matching size (this is a poor-man's content-address: keyed entries
        /// re-use blobs that another miss already wrote).
        /// Always uses File.Copy (not a hardlink) because a hardlink would let the next in-place
        /// write from the build silently corrupt the cached blob.
        /// </summary>
        private static void LinkOrCopyForStore(string src, string dest)
        {
            try
            {
                if (File.Exists(dest))
                {
                    long destLen = new FileInfo(dest).Length;
                    long srcLen = new FileInfo(src).Length;
                    if (destLen == srcLen)
                    {
                        return; // Assume identical; the cache key includes input-content hash.
                    }
                    File.Delete(dest);
                }
            }
            catch { }
            File.Copy(src, dest, true);
        }

        /// <summary>
        /// Prefers a hardlink (zero-copy, zero-IO) and falls back to <see cref="CopyFresh"/> when
        /// the FS can't (cross-volume, exFAT, network share, etc.). Cache blobs are treated as
        /// read-only "golden copies" so hardlinks are safe as long as the consumer never edits
        /// the file in-place (tasks normally re-create their outputs whole, not patch them).
        /// </summary>
        private static void LinkFresh(string src, string dest)
        {
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (TryHardLink(src, dest))
            {
                return;
            }
            CopyFresh(src, dest);
        }

        private static void CopyFresh(string src, string dest)
        {
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // Clear read-only on the existing destination so File.Copy doesn't throw on NuGet
            // content files or other ACL-protected targets.
            if (File.Exists(dest))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(dest);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Best-effort; let File.Copy surface any real error.
                }
            }
            File.Copy(src, dest, true);
            // Also clear read-only on the freshly-copied destination — File.Copy preserves the
            // source's attributes, and our cache copies may have been marked read-only by tools.
            try
            {
                FileAttributes attrs = File.GetAttributes(dest);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                }
            }
            catch { }
            File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
        }

        /// <summary>
        /// Project directory (non-recursive) plus the parent directory of each declared output,
        /// deduped, excluding any path that is at or under the IntermediateOutputPath (which is
        /// snapshotted recursively elsewhere) and the cache root itself.
        /// </summary>
        private List<string> BuildExtraObservationDirs()
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string projDir = Path.GetFullPath(_project.Directory);
            string cacheRoot = GetCacheRoot();

            if (Directory.Exists(projDir) && !IsUnderOrEqual(projDir, _intermediateRoot))
            {
                dirs.Add(projDir);
            }
            foreach (string o in _declaredOutputs)
            {
                string parent = Path.GetDirectoryName(o);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                {
                    continue;
                }
                string full = Path.GetFullPath(parent);
                if (IsUnderOrEqual(full, _intermediateRoot) || IsUnderOrEqual(full, cacheRoot))
                {
                    continue;
                }
                dirs.Add(full);
            }
            // <StrictAllowedOutputDirs>: explicitly observe these so we can both detect
            // changes (for diff-based caching) and not flag the writes as MSBSTRICT001.
            if (_allowedOutputDirs != null)
            {
                foreach (string d in _allowedOutputDirs)
                {
                    if (Directory.Exists(d) && !IsUnderOrEqual(d, _intermediateRoot) && !IsUnderOrEqual(d, cacheRoot))
                    {
                        dirs.Add(d);
                    }
                }
            }
            return new List<string>(dirs);
        }

        /// <summary>
        /// Parses the <c>StrictAllowedOutputDirs</c> property (semicolon-separated list of
        /// directories, project-relative or absolute) into a list of absolute paths.
        /// </summary>
        private List<string> ParseAllowedOutputDirs()
        {
            string raw = _project.GetPropertyValue("StrictAllowedOutputDirs");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            var result = new List<string>();
            foreach (string seg in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = seg.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                string abs = Path.IsPathRooted(trimmed)
                    ? Path.GetFullPath(trimmed)
                    : Path.GetFullPath(Path.Combine(_project.Directory, trimmed));
                result.Add(abs);
            }
            return result;
        }

        private static bool IsUnderOrEqual(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
            {
                return false;
            }
            string c = candidate.TrimEnd(Path.DirectorySeparatorChar, '/');
            string r = root.TrimEnd(Path.DirectorySeparatorChar, '/');
            if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Flat (top-level files only) snapshot of multiple directories, keyed by full absolute path.
        /// </summary>
        private static Dictionary<string, string> SnapshotFlat(IEnumerable<string> dirs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d))
                {
                    continue;
                }
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }
                foreach (string f in files)
                {
                    // Skip our own machinery.
                    if (f.IndexOf(Path.DirectorySeparatorChar + ".strict-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    try
                    {
                        var fi = new FileInfo(f);
                        dict[Path.GetFullPath(f)] = fi.LastWriteTimeUtc.Ticks.ToString() + "|" + fi.Length.ToString();
                    }
                    catch { }
                }
            }
            return dict;
        }

        private void EvictIfOversized(string cacheRoot)
        {
            try
            {
                if (!Directory.Exists(cacheRoot))
                {
                    return;
                }
                long maxBytes = GetCacheMaxBytes();
                if (maxBytes <= 0)
                {
                    return;
                }
                // Collect (.ok-file, size, lastAccessTime) per cache entry directory.
                var entries = new List<(string Dir, long Size, DateTime LastAccess)>();
                long total = 0;
                foreach (string okFile in Directory.EnumerateFiles(cacheRoot, ".ok", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(okFile);
                    if (dir == null)
                    {
                        continue;
                    }
                    long size = 0;
                    DateTime last = File.GetLastAccessTimeUtc(okFile);
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            size += new FileInfo(f).Length;
                        }
                    }
                    catch { }
                    entries.Add((dir, size, last));
                    total += size;
                }
                if (total <= maxBytes)
                {
                    return;
                }
                entries.Sort((a, b) => a.LastAccess.CompareTo(b.LastAccess));
                int evicted = 0;
                long freed = 0;
                foreach (var e in entries)
                {
                    if (total - freed <= maxBytes)
                    {
                        break;
                    }
                    try
                    {
                        Directory.Delete(e.Dir, recursive: true);
                        freed += e.Size;
                        evicted++;
                    }
                    catch { }
                }
                if (evicted > 0)
                {
                    Log(MessageImportance.Low,
                        $"[strict] evicted {evicted} cache entr{(evicted == 1 ? "y" : "ies")} ({freed / (1024 * 1024)} MiB) to stay under {maxBytes / (1024 * 1024)} MiB cap.");
                }
            }
            catch (Exception ex)
            {
                Log(MessageImportance.Low, $"[strict] cache eviction failed: {ex.Message}");
            }
        }

        private long GetCacheMaxBytes()
        {
            string envVal = Environment.GetEnvironmentVariable("MSBUILDSTRICTCACHEMAXBYTES");
            if (!string.IsNullOrEmpty(envVal) && long.TryParse(envVal, out long envBytes) && envBytes >= 0)
            {
                return envBytes;
            }
            string propVal = _project?.GetPropertyValue("MSBuildStrictCacheMaxBytes");
            if (!string.IsNullOrEmpty(propVal) && long.TryParse(propVal, out long propBytes) && propBytes >= 0)
            {
                return propBytes;
            }
            return DefaultCacheMaxBytes;
        }

        private static string HashFile(string path)
        {
            // Pooled 80 KiB buffer + reused IncrementalHash to keep allocations near zero
            // even on builds that touch hundreds of inputs.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: false);
            byte[] buf = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
#if NET
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    hasher.AppendData(buf, 0, read);
                }
                Span<byte> hash = stackalloc byte[32];
                hasher.GetHashAndReset(hash);
                return Convert.ToHexStringLower(hash);
#else
                using var sha = SHA256.Create();
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, read, null, 0);
                }
                sha.TransformFinalBlock(System.Array.Empty<byte>(), 0, 0);
                return HexEncodeLower(sha.Hash);
#endif
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private static string Sha256Hex(string s)
        {
            int byteCount = Encoding.UTF8.GetByteCount(s);
            byte[] buf = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = Encoding.UTF8.GetBytes(s, 0, s.Length, buf, 0);
#if NET
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(buf.AsSpan(0, written), hash);
                return Convert.ToHexStringLower(hash);
#else
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(buf, 0, written);
                return HexEncodeLower(hash);
#endif
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

#pragma warning disable IDE0051 // referenced via #if branches
        private static string HexEncodeLower(byte[] bytes)
#pragma warning restore IDE0051
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Attempts to atomically create <paramref name="dest"/> as a hardlink to <paramref name="src"/>.
        /// Falls back to false on cross-volume, unsupported FS, or any error so the caller can retry with File.Copy.
        /// </summary>
        private static bool TryHardLink(string src, string dest)
        {
            try
            {
                if (File.Exists(dest))
                {
                    // CreateHardLink/link fail if target exists; remove first.
                    File.Delete(dest);
                }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return NativeCreateHardLinkW(dest, src, IntPtr.Zero);
                }
                return NativeLink(src, dest) == 0;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeCreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int NativeLink(string oldpath, string newpath);

        private static string SafeSegment(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.ToString();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private void Log(MessageImportance importance, string message)
        {
            if (_log != null)
            {
                _log.LogCommentFromText(_ctx, importance, message);
            }
        }

        private void LogStrictWarning(string code, string message)
        {
            if (_log == null)
            {
                return;
            }
            try
            {
                _log.LogWarningFromText(
                    _ctx,
                    subcategoryResourceName: null,
                    warningCode: code,
                    helpKeyword: null,
                    file: new BuildEventFileInfo(_project.FullPath ?? string.Empty),
                    message: message);
            }
            catch
            {
                _log.LogCommentFromText(_ctx, MessageImportance.High, $"warning {code}: {message}");
            }
        }

        private void LogStrictError(string code, string message)
        {
            if (_log == null)
            {
                return;
            }
            try
            {
                _log.LogErrorFromText(
                    _ctx,
                    subcategoryResourceName: null,
                    errorCode: code,
                    helpKeyword: null,
                    file: new BuildEventFileInfo(_project.FullPath ?? string.Empty),
                    message: message);
            }
            catch
            {
                _log.LogCommentFromText(_ctx, MessageImportance.High, $"error {code}: {message}");
            }
        }
    }
}
