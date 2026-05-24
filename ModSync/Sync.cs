using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModSync.Utility;

namespace ModSync;

using SyncPathFileList = Dictionary<string, List<string>>;
using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

public static class Sync
{
    public static SyncPathFileList GetAddedFiles(List<SyncPath> syncPaths, SyncPathModFiles localModFiles, SyncPathModFiles remoteModFiles)
    {
        return syncPaths
            .Select(syncPath => new KeyValuePair<string, List<string>>(
                syncPath.path,
                remoteModFiles[syncPath.path]
                    .Where((kvp) => !kvp.Value.directory)
                    .Select((kvp) => kvp.Key)
                    .Except(localModFiles.TryGetValue(syncPath.path, out var modFiles) ? modFiles.Keys : new List<string>(), StringComparer.OrdinalIgnoreCase)
                    .ToList()
            ))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public static SyncPathFileList GetUpdatedFiles(
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousRemoteModFiles
    )
    {
        return syncPaths
            .Select(syncPath =>
            {
                if (!localModFiles.TryGetValue(syncPath.path, out var localPathFiles))
                    return new KeyValuePair<string, List<string>>(syncPath.path, []);

                var query = remoteModFiles[syncPath.path].Keys.Intersect(localPathFiles.Keys, StringComparer.OrdinalIgnoreCase);

                if (!syncPath.enforced)
                    query = query.Where(file =>
                        !previousRemoteModFiles.TryGetValue(syncPath.path, out var previousPathFiles)
                        || !previousPathFiles.TryGetValue(file, out var modFile)
                        || remoteModFiles[syncPath.path][file].hash != modFile.hash
                    );

                query = query.Where(file => remoteModFiles[syncPath.path][file].hash != localPathFiles[file].hash);

                return new KeyValuePair<string, List<string>>(syncPath.path, query.ToList());
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public static SyncPathFileList GetRemovedFiles(
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousRemoteModFiles
    )
    {
        return syncPaths
            .Select(syncPath =>
            {
                if (!localModFiles.TryGetValue(syncPath.path, out var localPathFiles))
                    return new KeyValuePair<string, List<string>>(syncPath.path, []);

                IEnumerable<string> query;
                if (syncPath.enforced)
                    query = localPathFiles.Keys.Except(remoteModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase);
                else
                    query = !previousRemoteModFiles.TryGetValue(syncPath.path, out var previousPathFiles)
                        ? []
                        : previousPathFiles
                            .Keys.Intersect(localPathFiles.Keys, StringComparer.OrdinalIgnoreCase)
                            .Except(remoteModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase);

                return new KeyValuePair<string, List<string>>(syncPath.path, query.ToList());
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public static SyncPathFileList GetCreatedDirectories(
        string basePath,
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles
    )
    {
        return syncPaths
            .Select(syncPath =>
            {
                return new KeyValuePair<string, List<string>>(
                    syncPath.path,
                    remoteModFiles[syncPath.path]
                        .Where((kvp) => kvp.Value.directory)
                        .Select((kvp) => kvp.Key)
                        .Except(localModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase)
                        .Where((dir) => !Directory.Exists(Path.Combine(basePath, dir)))
                        .ToList()
                );
            })
            .ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value);
    }

    /// <summary>
    /// Strip the <paramref name="basePath"/> prefix (and its trailing separator) from
    /// <paramref name="fullPath"/>. .NET Framework 4.7.2 has no <c>Path.GetRelativePath</c>,
    /// so we do the strip manually. Accepts either '\' (Windows native + Wine) or '/'
    /// (native Linux .NET) as the separator — so the function works regardless of which
    /// form the underlying filesystem hands back.
    /// </summary>
    private static string StripBasePath(string basePath, string fullPath)
    {
        if (string.IsNullOrEmpty(basePath) || !fullPath.StartsWith(basePath, StringComparison.Ordinal))
            return fullPath;

        var idx = basePath.Length;
        if (idx < fullPath.Length && (fullPath[idx] == '\\' || fullPath[idx] == '/'))
            idx++;
        return fullPath.Substring(idx);
    }

    private static List<string> GetFilesInDirectory(string basePath, string directory, List<Regex> exclusions)
    {
        if (File.Exists(directory))
            return [directory];

        if (!Directory.Exists(directory))
            return [];

        return Directory
            .GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where((file) => !IsExcluded(exclusions, StripBasePath(basePath, file)))
            .Concat(
                Directory
                    .GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where((subDir) => !IsExcluded(exclusions, StripBasePath(basePath, subDir)))
                    .SelectMany((subDir) => Directory.GetFileSystemEntries(subDir).Length == 0 ? [subDir] : GetFilesInDirectory(basePath, subDir, exclusions))
            )
            .ToList();
    }

    /// <summary>
    /// BepInEx/plugins folder in wire form. The headless allowlist only applies
    /// inside this folder — files in patchers/ and config/ pass through.
    /// </summary>
    private const string PLUGINS_FOLDER_WIRE = "BepInEx/plugins";

    /// <summary>
    /// True if a wire-form path lives inside BepInEx/plugins (i.e. is the plugins
    /// folder itself or a descendant of it).
    /// </summary>
    private static bool IsInPluginsFolder(string wirePath)
    {
        var normalized = wirePath.Replace('\\', '/');
        return normalized == PLUGINS_FOLDER_WIRE
            || normalized.StartsWith(PLUGINS_FOLDER_WIRE + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// True if a wire-form path is allowed by the headless allowlist — either matches
    /// an entry exactly (file or glob), OR is a descendant of an included directory.
    /// Mirrors the server-side <c>Config.MatchesHeadlessInclude</c>.
    /// </summary>
    private static bool MatchesHeadlessInclude(string wirePath, List<Regex> includeGlobs, List<string> includes)
    {
        var normalized = wirePath.Replace('\\', '/');

        // Direct match (exact file OR any glob pattern in the allowlist)
        if (includeGlobs.Any(g => g.IsMatch(normalized))) return true;

        // Folder-include: path is under an included directory
        foreach (var inc in includes)
        {
            var normalizedInc = inc.Replace('\\', '/');
            if (normalized.StartsWith(normalizedInc + "/", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public static async Task<SyncPathModFiles> HashLocalFiles(
        string basePath,
        List<SyncPath> syncPaths,
        List<Regex> remoteExclusions,
        List<Regex> localExclusions,
        List<string> headlessIncludes
    )
    {
        Plugin.Logger.LogInfo($"Corter-ModSync: HashLocalFiles entered. basePath='{basePath}', syncPaths={syncPaths.Count}, headlessIncludes={headlessIncludes.Count}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Thread-safe dedup set. The hashing pipeline below is `.AsParallel().Select(async ...)`,
        // so multiple threads call `.Add()` concurrently. A plain HashSet<T> isn't thread-safe
        // and corrupts under contention (CI on Windows hit this; local runs usually didn't).
        // ConcurrentDictionary<TKey, byte> is the idiomatic .NET workaround — there's no
        // built-in ConcurrentHashSet. We only care about the keys; `byte` is just a 1-byte
        // placeholder value.
        var processedFiles = new ConcurrentDictionary<string, byte>();
        var limitOpenFiles = new SemaphoreSlim(1024);

        // Pre-compile the allowlist globs once. Empty list (player client) means no
        // filtering happens — the predicate short-circuits via the Count == 0 check.
        var includeGlobs = headlessIncludes.Select(Glob.Create).ToList();
        var allowlistActive = headlessIncludes.Count > 0;

        var results = new SyncPathModFiles();

        foreach (var syncPath in syncPaths)
        {
            var path = Path.Combine(basePath, syncPath.path);

            // Find every candidate file under this syncpath. Then, when running as
            // a headless client, drop anything inside plugins/ that the allowlist
            // doesn't cover — mirrors the server's filter so local-vs-remote diffs stay
            // consistent (and we don't waste cycles hashing files we'll never sync).
            var candidates = GetFilesInDirectory(basePath, path, [.. remoteExclusions, .. syncPath.enforced ? [] : localExclusions])
                .Where(file => !processedFiles.ContainsKey(file));

            if (allowlistActive)
            {
                candidates = candidates.Where(file =>
                {
                    var rel = StripBasePath(basePath, file);
                    if (!IsInPluginsFolder(rel)) return true;
                    return MatchesHeadlessInclude(rel, includeGlobs, headlessIncludes);
                });
            }

            results[syncPath.path] = (
                await Task.WhenAll(
                    candidates
                        .AsParallel()
                        .Select(
                            async (file) =>
                            {
                                await limitOpenFiles.WaitAsync();
                                var modFile = await CreateModFile(file);
                                limitOpenFiles.Release();

                                processedFiles.TryAdd(file, 0);
                                return new KeyValuePair<string, ModFile>(StripBasePath(basePath, file), modFile);
                            }
                        )
                )
            ).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        watch.Stop();
        Plugin.Logger.LogInfo($"Corter-ModSync: Hashed {processedFiles.Count} files in {watch.Elapsed.TotalMilliseconds}ms (mode={(allowlistActive ? "headless" : "player")})");

        return results;
    }

    public static async Task<ModFile> CreateModFile(string file)
    {
        var hash = "";

        if (Directory.Exists(file))
            return new ModFile(hash, true);

        try
        {
            hash = await ImoHash.HashFile(file);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Corter-ModSync: Error hashing '{file}': {e.Message}");
            hash = "";
        }

        return new ModFile(hash);
    }

    public static void CompareModFiles(
        string basePath,
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousSync,
        out SyncPathFileList addedFiles,
        out SyncPathFileList updatedFiles,
        out SyncPathFileList removedFiles,
        out SyncPathFileList createdDirectories
    )
    {
        addedFiles = GetAddedFiles(syncPaths, localModFiles, remoteModFiles);
        updatedFiles = GetUpdatedFiles(syncPaths, localModFiles, remoteModFiles, previousSync);
        removedFiles = GetRemovedFiles(syncPaths, localModFiles, remoteModFiles, previousSync);
        createdDirectories = GetCreatedDirectories(basePath, syncPaths, localModFiles, remoteModFiles);
    }

    public static bool IsExcluded(List<Regex> exclusions, string path)
    {
        return exclusions.Any(regex => regex.IsMatch(path.Replace(@"\", "/")));
    }
}
