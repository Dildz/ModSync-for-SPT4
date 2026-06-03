using ModSync.Utility;
using SPTarkov.Server.Core.Models.Utils;

namespace ModSync.Server;

/// <summary>
/// File walker + hasher. Each call to <see cref="HashModFilesAsync"/> walks every enabled
/// syncpath fresh and re-hashes every file from scratch — no timestamp/size shortcuts.
///
/// Why no shortcuts: hash-on-change-only schemes (NarcoNet's approach) silently miss
/// modifications when timestamps are stripped or files are saved in-place with the same
/// size. Corter's full-rehash design trades some CPU for guaranteed correctness, and the
/// sampled-hash strategy in <see cref="ImoHash"/> keeps the cost low (samples three 32KB
/// chunks for files &gt;10MB instead of hashing the whole thing).
///
/// Not an [Injectable] class — constructed manually by the HTTP listener after Config has
/// loaded. Pattern matches corter's TS where SyncUtil is created per-request with the
/// loaded config.
///
/// Routing model:
///   • Universal <c>exclusions</c> apply to every walk regardless of client kind.
///   • For Fika headless clients, an additional <c>headlessIncludes</c> ALLOWLIST applies
///     to files under <c>BepInEx/plugins</c>. Paths in patchers/config flow through to
///     headless filtered only by the universal exclusions.
///   • Per-install opt-outs live client-side (<c>ModSync_Data/Exclusions.jsonc</c>) and
///     never reach the server.
/// </summary>
public class SyncUtil(Config config, ISptLogger<SyncUtil> logger)
{
    private const int MaxIORetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Heuristic: is this syncpath under BepInEx/plugins? Used to scope the headless
    /// allowlist — only plugins-area files get the allowlist gate. Handles both
    /// the direct path <c>../BepInEx/plugins</c> and any deeper sub-path under it.
    /// Comparison is on the unix-normalized form so backslashes don't trip it up.
    /// </summary>
    private static bool IsPluginsScoped(string syncPathRaw)
    {
        var p = PathExt.UnixPath(syncPathRaw).TrimEnd('/');
        return p == "../BepInEx/plugins" || p.StartsWith("../BepInEx/plugins/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Heuristic: is this syncpath the EscapeFromTarkov_Data/Managed folder?
    /// The managedIncludes allowlist applies here for both player and headless clients
    /// (each has its own list). Separator-normalized for cross-platform consistency.
    /// </summary>
    private static bool IsManagedScoped(string syncPathRaw)
    {
        var p = PathExt.UnixPath(syncPathRaw).TrimEnd('/');
        return p == "../EscapeFromTarkov_Data/Managed" || p.StartsWith("../EscapeFromTarkov_Data/Managed/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Recursive directory walk. Yields the path of every file we'd consider syncing,
    /// applying the configured exclusions as we go. Also yields any empty directory's
    /// own path — clients need to recreate empty dirs on their side, so we represent
    /// them as path entries with an empty hash and <c>directory=true</c> in the final
    /// ModFile.
    ///
    /// `yield return` (vs returning a list): lazy enumeration. Each item is produced on
    /// demand as the caller iterates. Memory stays flat regardless of how big the tree is.
    /// </summary>
    // skipExclusions=true is used for the headless+plugins scope, where headlessIncludes
    // is the sole gate and intentionally overrides the global exclusions list. For all
    // other scopes (players, headless patchers/config) exclusions apply as normal.
    public IEnumerable<string> GetFilesInDir(string dir, bool skipExclusions = false)
    {
        // Three early-exit cases, in order:
        //   1) Path doesn't exist at all (likely a stale syncpath) — warn and skip.
        //   2) Path points at a single file (e.g. our built-in ../ModSync.Updater.exe).
        //   3) Path points at a directory — fall through to the walk.
        if (!File.Exists(dir) && !Directory.Exists(dir))
        {
            logger.Warning($"Corter-ModSync: syncpath '{dir}' does not exist, will be ignored.");
            yield break;
        }

        if (File.Exists(dir))
        {
            if (!skipExclusions && config.IsExcluded(dir)) yield break;
            yield return dir;
            yield break;
        }

        var hasContents = false;

        // Files in this directory (non-recursive). EnumerateFiles is lazy — pulls one
        // entry from the OS at a time, doesn't materialize the full list.
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!skipExclusions && config.IsExcluded(file)) continue;
            yield return file;
            hasContents = true;
        }

        // Subdirectories — recurse into each.
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (!skipExclusions && config.IsExcluded(subDir)) continue;

            foreach (var x in GetFilesInDir(subDir, skipExclusions))
            {
                yield return x;
                hasContents = true;
            }
        }

        // If we walked the whole directory and produced nothing (empty dir, or every
        // child was excluded), yield the dir itself so the client knows to recreate it.
        if (!hasContents)
        {
            yield return dir;
        }
    }

    /// <summary>
    /// Build a ModFile for a single path — empty hash if it's a directory, sampled
    /// MetroHash128 if it's a file.
    ///
    /// Retries on IOException up to <see cref="MaxIORetries"/> times with a small delay.
    /// Most common cause on Windows is another process holding the file open with
    /// exclusive access (ERROR_SHARING_VIOLATION, HResult 0x80070020) — happens with
    /// log files, locked configs, etc. Linux is generally less prone to this.
    /// </summary>
    public async Task<ModFile> BuildModFileAsync(string file)
    {
        if (Directory.Exists(file))
        {
            return new ModFile(hash: "", directory: true);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var hash = await ImoHash.HashFile(file);
                return new ModFile(hash: hash, directory: false);
            }
            catch (IOException ex) when (attempt < MaxIORetries)
            {
                logger.Warning($"Corter-ModSync: error reading '{file}' (attempt {attempt + 1}/{MaxIORetries}): {ex.Message}. Retrying...");
                await Task.Delay(RetryDelay);
            }
        }
    }

    /// <summary>
    /// Walk + hash every file under each given syncpath. Returns the nested map the
    /// /modsync/hashes route serializes to JSON: outer key = syncpath, inner key = file
    /// path under that syncpath, value = ModFile.
    ///
    /// Paths in the response use Windows separators (backslashes) — the BepInEx client
    /// expects this even on a Linux server. Normalization happens at this boundary so the
    /// rest of the code can stay separator-agnostic.
    ///
    /// Files seen across multiple syncpaths only get hashed once (the first time we
    /// encounter them). Matches corter's dedup-by-set behavior in sync.ts.
    ///
    /// <paramref name="isHeadless"/> — when true, the headlessIncludes allowlist is the
    /// sole gate for BepInEx/plugins: exclusions are bypassed so that files excluded from
    /// players (e.g. Fika.Headless.dll) can still reach headless via the allowlist.
    /// Files in patchers/config (and any non-plugins syncpath) are still filtered by the
    /// universal exclusions. Enforced syncpaths bypass the allowlist so ModSync itself can
    /// always self-update on headless.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, ModFile>>> HashModFilesAsync(
        IEnumerable<SyncPath> syncPaths,
        bool isHeadless = false)
    {
        var result = new Dictionary<string, Dictionary<string, ModFile>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var startedAt = DateTime.UtcNow;
        var filesHashed = 0;

        foreach (var syncPath in syncPaths)
        {
            var perPath = new Dictionary<string, ModFile>();

            // Decide once per syncpath which allowlist gates apply.
            // Enforced paths bypass the headless plugins gate so ModSync itself can always self-update.
            var applyHeadlessPluginsAllowlist = isHeadless && !syncPath.enforced && IsPluginsScoped(syncPath.path);
            var applyManagedAllowlist = IsManagedScoped(syncPath.path);

            // For headless+plugins: skip exclusions in the walk — headlessIncludes is the
            // sole gate and is designed to override exclusions (e.g. Fika.Headless.dll is
            // in exclusions to block players, but headlessIncludes lets it reach headless).
            foreach (var file in GetFilesInDir(syncPath.path, skipExclusions: applyHeadlessPluginsAllowlist))
            {
                var winFile = PathExt.WinPath(file);
                if (!seen.Add(winFile)) continue;

                // Headless+plugins: headlessIncludes is the sole filter — exclusions are
                // intentionally bypassed above so that files like Fika.Headless.dll can be
                // excluded from players yet still reach headless via the allowlist.
                // Empty allowlist means zero plugins reach headless — intentional "fail closed".
                if (applyHeadlessPluginsAllowlist && !config.IsHeadlessAllowed(file)) continue;

                // Managed folder: only serve filenames explicitly listed in managedIncludes
                // (or headlessManagedIncludes for headless). Works by filename only so it's
                // identical on Docker (staging folder, 2 files) and Windows (full Managed
                // folder, 169 files) — the allowlist is what keeps vanilla DLLs out.
                if (applyManagedAllowlist)
                {
                    var fileName = Path.GetFileName(file);
                    var allowed = isHeadless
                        ? config.IsHeadlessManagedAllowed(fileName)
                        : config.IsManagedAllowed(fileName);
                    if (!allowed) continue;
                }

                perPath[winFile] = await BuildModFileAsync(file);
                filesHashed++;
            }

            result[PathExt.WinPath(syncPath.path)] = perPath;
        }

        var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
        logger.Info($"Corter-ModSync: hashed {filesHashed} files in {elapsedMs:F0}ms (headless={isHeadless}).");

        return result;
    }

    /// <summary>
    /// Resolve and security-check a path the client asked to download. Throws
    /// <see cref="HttpError"/> with status 400 if the resolved path escapes every
    /// configured syncpath (path traversal attempt or stale client request).
    /// </summary>
    public static string SanitizeDownloadPath(string file, IEnumerable<SyncPath> syncPaths)
    {
        var serverRoot = Directory.GetCurrentDirectory();
        var requested = Path.GetFullPath(Path.Combine(serverRoot, file));

        foreach (var sp in syncPaths)
        {
            var syncPathFull = Path.GetFullPath(Path.Combine(serverRoot, sp.path));
            var rel = Path.GetRelativePath(syncPathFull, requested);

            // Inside if the relative path doesn't escape upward and isn't another root.
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return requested;
            }
        }

        throw new HttpError(400, $"Corter-ModSync: requested file '{file}' is not in any enabled sync path.");
    }
}
