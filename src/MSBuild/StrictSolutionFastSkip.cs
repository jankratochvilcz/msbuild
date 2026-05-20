// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

#nullable disable

namespace Microsoft.Build.Strict
{
    /// <summary>
    /// Solution-level "instant skip" cache. When MSBuildStrictMode is enabled, after a
    /// successful build we record a manifest of <em>every</em> file under the build root
    /// whose modification could possibly invalidate the build (csproj/props/targets,
    /// source files, lock files), plus a manifest of every file produced
    /// (bin/, obj/ excluding our own cache dirs). On a subsequent invocation we re-stat
    /// every input and every output; if all stamps match, the build is a guaranteed no-op
    /// and we return success immediately without invoking the engine at all.
    ///
    /// This sits <em>above</em> the project-evaluation layer that dominates the no-op
    /// floor (~30 % csproj evaluation + ~25 % NuGet restore + ~15 % JIT on a 47-project
    /// SDK solution; see <c>demo/StrictModeDemo/LEARNINGS.md</c>), and is therefore the
    /// only practical place to get a 10× win on a true no-op rebuild without rewriting
    /// MSBuild evaluation.
    ///
    /// <para>Backwards-compatible: opt-in via <c>MSBUILDSTRICTMODE</c> env var, and the
    /// skip is only taken when both (a) all inputs unchanged by (mtime,size) and
    /// (b) all previously-recorded outputs still exist with matching (mtime,size).
    /// Any mismatch falls through to the normal build path.</para>
    /// </summary>
    internal static class StrictSolutionFastSkip
    {
        private const string CacheDirName = ".strict-fastskip";

        // Source/project files whose changes must invalidate the cache. The set covers the common
        // build inputs across the SDKs we ship for; additional extensions can be appended at
        // runtime via the MSBUILDSTRICTEXTRAINPUTEXTENSIONS env var (see
        // StrictModeSettings.GetExtraInputExtensions).
        private static readonly string[] s_inputExts =
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

        // Substrings to ignore in input enumeration (case-insensitive).
        private static readonly string[] s_skipDirSegments =
        {
            "\\bin\\", "/bin/",
            "\\obj\\", "/obj/",
            "\\.git\\", "/.git/",
            "\\.vs\\", "/.vs/",
            "\\node_modules\\", "/node_modules/",
            "\\packages\\", "/packages/",
            "\\.strict-fastskip\\", "/.strict-fastskip/",
            "\\.strict-cache\\", "/.strict-cache/",
            "\\.strict-solution-cache\\", "/.strict-solution-cache/",
        };

        public static bool IsEnabled()
        {
            return StrictModeSettings.IsLayerEnabled(
                projectPropertyValue: null,
                layerDisableEnvVar: StrictModeSettings.EnvDisableSolutionFastSkip);
        }

        /// <summary>
        /// Attempts to short-circuit the build. Returns true iff the cache was hit and
        /// no work is required (caller MUST treat as a successful build).
        /// </summary>
        public static bool TryFastSkip(string projectFile, string[] targets, IDictionary<string, string> globalProperties, out string reason)
        {
            reason = null;
            if (!IsEnabled())
            {
                reason = "disabled";
                return false;
            }
            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
            {
                reason = "no-project-file";
                return false;
            }

            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            bool hit = TryFastSkipCore(projectFile, targets, globalProperties, out reason, out int inputCount, out int outputCount);
            swTotal.Stop();
            if (StrictTelemetry.IsEnabled)
            {
                StrictTelemetry.Emit(
                    layer: "solution-fastskip",
                    outcome: hit ? "hit" : "miss",
                    project: projectFile,
                    reason: reason,
                    durationUs: swTotal.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000),
                    fileCount: inputCount + outputCount);
            }
            return hit;
        }

        private static bool TryFastSkipCore(string projectFile, string[] targets, IDictionary<string, string> globalProperties, out string reason, out int inputCount, out int outputCount)
        {
            reason = null;
            inputCount = 0;
            outputCount = 0;
            try
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                string cacheDir = Path.Combine(root, CacheDirName);
                string manifestPath = Path.Combine(cacheDir, ComputeArgKey(projectFile, targets, globalProperties) + ".manifest");
                if (!File.Exists(manifestPath))
                {
                    reason = "no-manifest";
                    return false;
                }

                Manifest m = Manifest.Load(manifestPath);
                if (m == null)
                {
                    reason = "manifest-corrupt";
                    return false;
                }

                // Portability guard: a manifest that records a different absolute project path
                // (e.g. tarballed from another machine, restored via OneDrive into a different
                // working tree) cannot be trusted — its Inputs/Outputs absolute paths point at
                // someone else's machine. Reject rather than risk a stale fast-skip.
                if (StrictModeSettings.IsForeignManifest(m.ProjectFile, projectFile))
                {
                    reason = "foreign-machine";
                    return false;
                }

                // 1. Fast directory-mtime check: if no input directory has changed mtime, the set
                //    of files in those directories cannot have changed (added/removed/renamed).
                //    NTFS, ext4 and APFS all update the directory mtime on entry changes.
                bool anyDirChanged = false;
                foreach (var kv in m.InputDirs)
                {
                    try
                    {
                        var di = new DirectoryInfo(kv.Key);
                        if (!di.Exists || di.LastWriteTimeUtc.Ticks != kv.Value)
                        {
                            anyDirChanged = true;
                            break;
                        }
                    }
                    catch
                    {
                        anyDirChanged = true;
                        break;
                    }
                }

                if (anyDirChanged)
                {
                    // Slow path: re-enumerate to catch added/removed files.
                    var current = EnumerateInputs(root, out _);
                    if (current.Count != m.Inputs.Count)
                    {
                        reason = $"input-count-changed ({m.Inputs.Count} -> {current.Count})";
                        return false;
                    }
                    foreach (var kv in m.Inputs)
                    {
                        if (!current.TryGetValue(kv.Key, out var st) || st.Ticks != kv.Value.Ticks || st.Size != kv.Value.Size)
                        {
                            reason = $"input-changed {kv.Key}";
                            return false;
                        }
                    }
                }
                else
                {
                    // Fast path: just stat each recorded input file.
                    foreach (var kv in m.Inputs)
                    {
                        try
                        {
                            var fi = new FileInfo(kv.Key);
                            if (!fi.Exists || fi.LastWriteTimeUtc.Ticks != kv.Value.Ticks || fi.Length != kv.Value.Size)
                            {
                                reason = $"input-changed {kv.Key}";
                                return false;
                            }
                        }
                        catch
                        {
                            reason = $"input-stat-failed {kv.Key}";
                            return false;
                        }
                    }
                }

                // 2. Verify all previously-recorded outputs still exist with same stamp.
                foreach (var kv in m.Outputs)
                {
                    FileInfo fi = new FileInfo(kv.Key);
                    if (!fi.Exists || fi.LastWriteTimeUtc.Ticks != kv.Value.Ticks || fi.Length != kv.Value.Size)
                    {
                        reason = $"output-missing-or-changed {kv.Key}";
                        return false;
                    }
                }

                reason = $"hit (inputs={m.Inputs.Count}, outputs={m.Outputs.Count})";
                inputCount = m.Inputs.Count;
                outputCount = m.Outputs.Count;
                return true;
            }
            catch (Exception ex)
            {
                reason = "exception: " + ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        internal sealed class PreBuildInputSnapshot
        {
            public string Root;
            public Dictionary<string, Stat> Inputs;
        }

        internal static PreBuildInputSnapshot CapturePreBuildInputSnapshot(string projectFile)
        {
            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
            {
                return null;
            }

            string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            return new PreBuildInputSnapshot
            {
                Root = root,
                Inputs = EnumerateInputs(root, out _),
            };
        }

        /// <summary>
        /// Records the post-build manifest so the next invocation can fast-skip.
        /// Call this only after a successful build.
        /// </summary>
        public static void RecordSuccess(string projectFile, string[] targets, IDictionary<string, string> globalProperties)
        {
            RecordSuccess(projectFile, targets, globalProperties, preBuildInputs: null);
        }

        internal static void RecordSuccess(string projectFile, string[] targets, IDictionary<string, string> globalProperties, PreBuildInputSnapshot preBuildInputs)
        {
            if (!IsEnabled())
            {
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int inputCount = 0, outputCount = 0;
            try
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                string cacheDir = Path.Combine(root, CacheDirName);
                Directory.CreateDirectory(cacheDir);
                string manifestPath = Path.Combine(cacheDir, ComputeArgKey(projectFile, targets, globalProperties) + ".manifest");

                Dictionary<string, Stat> inputs = EnumerateInputs(root, out var dirs);
                Dictionary<string, Stat> outputs = EnumerateOutputs(root);
                ReclassifyBuildWrittenInputs(root, preBuildInputs, inputs, outputs);

                var m = new Manifest
                {
                    SchemaVersion = 3,
                    ProjectFile = projectFile,
                    RecordedAt = DateTime.UtcNow.Ticks,
                    Inputs = inputs,
                    Outputs = outputs,
                    InputDirs = dirs,
                };
                inputCount = m.Inputs.Count;
                outputCount = m.Outputs.Count;
                m.Save(manifestPath);
            }
            catch
            {
                // Recording failures must never break a build.
            }
            sw.Stop();
            if (StrictTelemetry.IsEnabled)
            {
                StrictTelemetry.Emit(
                    layer: "solution-fastskip",
                    outcome: "store",
                    project: projectFile,
                    reason: $"inputs={inputCount} outputs={outputCount}",
                    durationUs: sw.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000),
                    fileCount: inputCount + outputCount);
            }
        }

        private static string ComputeArgKey(string projectFile, string[] targets, IDictionary<string, string> globalProperties)
        {
            var sb = new StringBuilder();
            sb.Append(Path.GetFullPath(projectFile)).Append('\n');
            if (targets != null)
            {
                Array.Sort(targets, CompareMSBuildIdentifierForCacheKey);
                foreach (var t in targets) { sb.Append("t:").Append(CanonicalizeMSBuildIdentifierForCacheKey(t)).Append('\n'); }
            }
            if (globalProperties != null)
            {
                var keys = new List<string>(globalProperties.Keys);
                keys.Sort(CompareMSBuildIdentifierForCacheKey);
                foreach (var k in keys) { sb.Append("g:").Append(CanonicalizeMSBuildIdentifierForCacheKey(k)).Append('=').Append(globalProperties[k]).Append('\n'); }
            }
            StrictCacheKeyEnvironment.AppendFingerprint(sb, StrictCacheKeyEnvironment.GetConfiguredValue(globalProperties));
            byte[] h;
            using (var sha = SHA256.Create())
            {
                h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            }
            var hex = new StringBuilder(h.Length * 2);
            foreach (byte b in h) { hex.Append(b.ToString("x2")); }
            return hex.ToString().Substring(0, 24);
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

        private static Dictionary<string, Stat> EnumerateInputs(string root, out Dictionary<string, long> dirs)
        {
            var result = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
            var dirsLocal = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> outputDirectories = DiscoverOutputDirectories(root);
            EnumerateDirRecursive(root, dirsLocal, outputDirectories, (path) =>
            {
                if (ShouldSkipPath(path)) { return; }
                string ext = Path.GetExtension(path);
                if (ext.Length == 0)
                {
                    string name = Path.GetFileName(path);
                    if (!name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("global.json", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                else
                {
                    bool matched = false;
                    for (int i = 0; i < s_inputExts.Length; i++)
                    {
                        if (string.Equals(ext, s_inputExts[i], StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                    }
                    if (!matched)
                    {
                        var extra = Microsoft.Build.Strict.StrictModeSettings.GetExtraInputExtensions();
                        if (extra.Count == 0 || !extra.Contains(ext)) { return; }
                    }
                }
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists)
                    {
                        result[path] = new Stat(fi.LastWriteTimeUtc.Ticks, fi.Length);
                    }
                }
                catch { }
            });
            dirs = dirsLocal;
            return result;
        }

        private static void ReclassifyBuildWrittenInputs(string root, PreBuildInputSnapshot preBuildInputs, Dictionary<string, Stat> currentInputs, Dictionary<string, Stat> outputs)
        {
            if (preBuildInputs?.Inputs == null || !string.Equals(preBuildInputs.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var kv in currentInputs)
            {
                if (!preBuildInputs.Inputs.TryGetValue(kv.Key, out var preBuildStat)
                    || preBuildStat.Ticks != kv.Value.Ticks
                    || preBuildStat.Size != kv.Value.Size)
                {
                    outputs[kv.Key] = kv.Value;
                }
            }

            foreach (string inputPath in new List<string>(currentInputs.Keys))
            {
                if (!preBuildInputs.Inputs.TryGetValue(inputPath, out var preBuildStat)
                    || !currentInputs.TryGetValue(inputPath, out var postBuildStat)
                    || preBuildStat.Ticks != postBuildStat.Ticks
                    || preBuildStat.Size != postBuildStat.Size)
                {
                    currentInputs.Remove(inputPath);
                }
            }
        }

        private static Dictionary<string, Stat> EnumerateOutputs(string root)
        {
            var result = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
            foreach (string outputDirectory in DiscoverOutputDirectories(root))
            {
                try
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        continue;
                    }

                    foreach (var f in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
                    {
                        if (f.IndexOf(CacheDirName, StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                        if (f.IndexOf(".strict-cache", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.Exists)
                            {
                                result[f] = new Stat(fi.LastWriteTimeUtc.Ticks, fi.Length);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        private static HashSet<string> DiscoverOutputDirectories(string root)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDefaultOutputDirectories(root, result);
            AddConfiguredOutputDirectories(root, result);
            return result;
        }

        private static void AddDefaultOutputDirectories(string root, HashSet<string> result)
        {
            try
            {
                foreach (var dirName in new[] { "bin", "obj" })
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, dirName, SearchOption.AllDirectories))
                    {
                        if (dir.IndexOf(CacheDirName, StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                        if (dir.IndexOf(".strict-cache", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                        result.Add(Path.GetFullPath(dir));
                    }
                }
            }
            catch { }
        }

        private static void AddConfiguredOutputDirectories(string root, HashSet<string> result)
        {
            bool useArtifactsOutput = false;
            HashSet<string> artifactRoots = new(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in EnumerateOutputConfigurationFiles(root))
            {
                string fileDirectory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(fileDirectory))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(filePath);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                    HashSet<int> unconditionalPropertyGroupDepths = [];
                    Dictionary<int, bool> blockedDepths = new();
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            unconditionalPropertyGroupDepths.Remove(reader.Depth);
                            blockedDepths.Remove(reader.Depth);
                            continue;
                        }

                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        int depth = reader.Depth;
                        bool parentBlocked = depth > 0
                            && blockedDepths.TryGetValue(depth - 1, out bool blockedParent)
                            && blockedParent;
                        bool blockedHere = parentBlocked || HasNonEmptyCondition(reader) || IsConditionalContainer(reader.LocalName);
                        blockedDepths[depth] = blockedHere;

                        if (reader.IsEmptyElement)
                        {
                            unconditionalPropertyGroupDepths.Remove(depth);
                            continue;
                        }

                        if (blockedHere)
                        {
                            continue;
                        }

                        if (reader.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase))
                        {
                            unconditionalPropertyGroupDepths.Add(depth);
                            continue;
                        }

                        if (!unconditionalPropertyGroupDepths.Contains(depth - 1))
                        {
                            continue;
                        }

                        switch (reader.LocalName)
                        {
                            case "OutputPath":
                            case "BaseOutputPath":
                            case "IntermediateOutputPath":
                            case "BaseIntermediateOutputPath":
                            case "PublishDir":
                                if (TryResolveStaticPath(fileDirectory, ReadSimpleElementContent(reader), out string resolvedDirectory))
                                {
                                    result.Add(resolvedDirectory);
                                }
                                break;
                            case "ArtifactsPath":
                                if (TryResolveStaticPath(fileDirectory, ReadSimpleElementContent(reader), out string resolvedArtifactsRoot))
                                {
                                    artifactRoots.Add(resolvedArtifactsRoot);
                                }
                                break;
                            case "UseArtifactsOutput":
                                useArtifactsOutput |= IsTruthy(ReadSimpleElementContent(reader));
                                break;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            if (useArtifactsOutput)
            {
                if (artifactRoots.Count == 0)
                {
                    artifactRoots.Add(Path.GetFullPath(Path.Combine(root, "artifacts")));
                }

                foreach (string artifactRoot in artifactRoots)
                {
                    result.Add(Path.Combine(artifactRoot, "bin"));
                    result.Add(Path.Combine(artifactRoot, "obj"));
                }
            }
        }

        private static IEnumerable<string> EnumerateOutputConfigurationFiles(string root)
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj", ".props", ".targets",
            };

            foreach (string filePath in EnumerateOutputConfigurationFilesRecursive(root, extensions))
            {
                yield return filePath;
            }
        }

        private static IEnumerable<string> EnumerateOutputConfigurationFilesRecursive(string directory, HashSet<string> extensions)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch
            {
                yield break;
            }

            foreach (string filePath in files)
            {
                if (extensions.Contains(Path.GetExtension(filePath)))
                {
                    yield return filePath;
                }
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                yield break;
            }

            foreach (string subdirectory in subdirectories)
            {
                if (ShouldSkipOutputConfigurationDirectory(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                foreach (string filePath in EnumerateOutputConfigurationFilesRecursive(subdirectory, extensions))
                {
                    yield return filePath;
                }
            }
        }

        private static bool HasNonEmptyCondition(XmlReader reader)
        {
            return !string.IsNullOrWhiteSpace(reader.GetAttribute("Condition"));
        }

        private static bool IsConditionalContainer(string elementName)
        {
            return elementName.Equals("Choose", StringComparison.OrdinalIgnoreCase)
                || elementName.Equals("When", StringComparison.OrdinalIgnoreCase)
                || elementName.Equals("Otherwise", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadSimpleElementContent(XmlReader reader)
        {
            int depth = reader.Depth;
            StringBuilder builder = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    builder ??= new StringBuilder();
                    builder.Append(reader.Value);
                }
            }

            return builder?.ToString() ?? string.Empty;
        }

        private static bool ShouldSkipOutputConfigurationDirectory(string directoryName)
        {
            return directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(".vs", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(CacheDirName, StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(".strict-cache", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(".strict-solution-cache", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveStaticPath(string baseDirectory, string value, out string resolvedPath)
        {
            resolvedPath = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.IndexOf('$') >= 0 || trimmed.IndexOf('%') >= 0 || trimmed.IndexOf('@') >= 0)
            {
                return false;
            }

            resolvedPath = Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(baseDirectory, trimmed));
            return true;
        }

        private static bool IsTruthy(string value)
        {
            if (value is null)
            {
                return false;
            }

            string trimmed = value.Trim();
            return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("1", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipPath(string path)
        {
            for (int i = 0; i < s_skipDirSegments.Length; i++)
            {
                if (path.IndexOf(s_skipDirSegments[i], StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        private static void EnumerateDirRecursive(string dir, Dictionary<string, long> dirs, HashSet<string> outputDirectories, Action<string> onFile)
        {
            try
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.Exists) { dirs[dir] = di.LastWriteTimeUtc.Ticks; }
                }
                catch { }

                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    onFile(f);
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.Length == 0) { continue; }
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals(".vs", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals(CacheDirName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (name.Equals(".strict-cache", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (IsUnderAnyOutputDirectory(sub, outputDirectories)) { continue; }
                    EnumerateDirRecursive(sub, dirs, outputDirectories, onFile);
                }
            }
            catch { }
        }

        private static bool IsUnderAnyOutputDirectory(string path, HashSet<string> outputDirectories)
        {
            foreach (string outputDirectory in outputDirectories)
            {
                if (IsUnderOrEqual(path, outputDirectory))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnderOrEqual(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal readonly struct Stat
        {
            public Stat(long ticks, long size) { Ticks = ticks; Size = size; }
            public long Ticks { get; }
            public long Size { get; }
        }

        private sealed class Manifest
        {
            public int SchemaVersion;
            public string ProjectFile;
            public long RecordedAt;
            public Dictionary<string, Stat> Inputs;
            public Dictionary<string, Stat> Outputs;
            // Directories whose mtime must be unchanged to trust that no new input files were added.
            public Dictionary<string, long> InputDirs;

            public void Save(string path)
            {
                using var fs = File.Create(path);
                using var w = new BinaryWriter(fs);
                w.Write(SchemaVersion);
                w.Write(ProjectFile ?? "");
                w.Write(RecordedAt);
                WriteDict(w, Inputs);
                WriteDict(w, Outputs);
                WriteDirDict(w, InputDirs ?? new Dictionary<string, long>());
            }

            public static Manifest Load(string path)
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    using var r = new BinaryReader(fs);
                    var m = new Manifest
                    {
                        SchemaVersion = r.ReadInt32(),
                    };
                    if (m.SchemaVersion != 3) { return null; }
                    m.ProjectFile = r.ReadString();
                    m.RecordedAt = r.ReadInt64();
                    m.Inputs = ReadDict(r);
                    m.Outputs = ReadDict(r);
                    m.InputDirs = ReadDirDict(r);
                    return m;
                }
                catch
                {
                    return null;
                }
            }

            private static void WriteDict(BinaryWriter w, Dictionary<string, Stat> d)
            {
                w.Write(d.Count);
                foreach (var kv in d)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value.Ticks);
                    w.Write(kv.Value.Size);
                }
            }

            private static Dictionary<string, Stat> ReadDict(BinaryReader r)
            {
                int n = r.ReadInt32();
                var d = new Dictionary<string, Stat>(n, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    string k = r.ReadString();
                    long ticks = r.ReadInt64();
                    long size = r.ReadInt64();
                    d[k] = new Stat(ticks, size);
                }
                return d;
            }

            private static void WriteDirDict(BinaryWriter w, Dictionary<string, long> d)
            {
                w.Write(d.Count);
                foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value); }
            }

            private static Dictionary<string, long> ReadDirDict(BinaryReader r)
            {
                int n = r.ReadInt32();
                var d = new Dictionary<string, long>(n, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++) { string k = r.ReadString(); long t = r.ReadInt64(); d[k] = t; }
                return d;
            }
        }
    }
}
