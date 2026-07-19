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

    /// <summary>
    /// Overlay <paramref name="current"/> onto <paramref name="previous"/>: paths this run
    /// covered get their fresh record, every other path keeps the one it had.
    ///
    /// Only used on a self-update run, where <paramref name="current"/> holds nothing but
    /// ModSync's own components. Writing that wholesale would drop every mod path's record,
    /// and since PreviousSync is what licenses a removal, the next launch would silently
    /// refuse to uninstall anything the player un-ticked.
    /// </summary>
    public static SyncPathModFiles MergePreviousSync(SyncPathModFiles previous, SyncPathModFiles current)
    {
        var merged = new SyncPathModFiles(previous, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in current)
            merged[kvp.Key] = kvp.Value;

        return merged;
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
                        .Except(localModFiles.TryGetValue(syncPath.path, out var localDirs) ? localDirs.Keys : new List<string>(), StringComparer.OrdinalIgnoreCase)
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

    public static async Task<SyncPathModFiles> HashLocalFiles(
        string basePath,
        List<SyncPath> syncPaths,
        List<Regex> remoteExclusions,
        Func<SyncPath, bool> isActive = null
    )
    {
        // Ownership (which syncpath a local file belongs to) is claimed across EVERY path
        // passed in, even inactive/opt-out ones — so a disabled override carves its files
        // out of an enclosing catch-all's local set, and they're never flagged for add/remove.
        // Only ACTIVE paths get hashed and returned. Default: everything active.
        isActive ??= _ => true;

        Plugin.Logger.LogInfo($"Corter-ModSync: HashLocalFiles entered. basePath='{basePath}', syncPaths={syncPaths.Count}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Thread-safe dedup set. The hashing pipeline below is `.AsParallel().Select(async ...)`,
        // so multiple threads call `.Add()` concurrently. A plain HashSet<T> isn't thread-safe
        // and corrupts under contention (CI on Windows hit this; local runs usually didn't).
        // ConcurrentDictionary<TKey, byte> is the idiomatic .NET workaround — there's no
        // built-in ConcurrentHashSet. We only care about the keys; `byte` is just a 1-byte
        // placeholder value.
        var processedFiles = new ConcurrentDictionary<string, byte>();
        var limitOpenFiles = new SemaphoreSlim(1024);

        var results = new SyncPathModFiles();

        foreach (var syncPath in syncPaths)
        {
            var path = Path.Combine(basePath, syncPath.path);

            // Find every candidate file under this syncpath, then hash in parallel.
            //
            // The local walk only filters by REMOTE exclusions (the server's universal
            // denylist — .nosync sentinels, SPT internals, etc). The player's own
            // ModSync_Data/Exclusions.jsonc is NOT applied here — that filter is applied
            // to the REMOTE file list later (in Plugin.cs). Reason: a locally-present
            // file that the player added to exclusions still needs to be visible to the
            // diff so GetRemovedFiles can uninstall it (when previousSync says ModSync
            // installed it). Filtering it out of the local walk would hide it from the
            // diff and the file would stay forever.
            // baseFiles are base-game files this mod REPLACES, living outside its own folder
            // (e.g. nvngx_dlss.dll). They belong to this syncpath for diff purposes, so append
            // them to the walk — otherwise the local side would look empty for those paths and
            // the diff would re-download them on every launch.
            var baseFilePaths = syncPath.baseFiles
                .Select(bf => Path.Combine(basePath, bf))
                .Where(File.Exists);

            var candidates = GetFilesInDirectory(basePath, path, remoteExclusions)
                .Concat(baseFilePaths)
                .Where(file => !processedFiles.ContainsKey(file));

            // Inactive path: claim its files (so an enclosing catch-all can't pick them up)
            // but don't hash or return them — opt-out files are neither installed nor removed.
            if (!isActive(syncPath))
            {
                foreach (var file in candidates)
                    processedFiles.TryAdd(file, 0);
                continue;
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
        Plugin.Logger.LogInfo($"Corter-ModSync: Hashed {processedFiles.Count} files in {watch.Elapsed.TotalMilliseconds}ms");

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
