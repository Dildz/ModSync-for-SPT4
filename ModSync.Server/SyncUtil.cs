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
/// loaded config. DI for Config + logger would be cleaner if SPT had a "load order"
/// dependency mechanism, but we don't need that complexity here.
///
/// Routing model: every walk takes an <c>isHeadless</c> flag. The same filesystem walk
/// produces different result sets for player vs headless clients:
///   • Both: <c>GlobalExclusions</c> always applies.
///   • Player only: <c>ClientExclusions</c> applies.
///   • Headless only: inside <c>BepInEx/plugins</c>, <c>HeadlessIncludes</c> acts as an
///     allowlist — files there must match an include or they're skipped. <c>patchers/</c>
///     and <c>config/</c> are not allowlist-filtered (master list says headless gets all).
/// </summary>
public class SyncUtil(Config config, ISptLogger<SyncUtil> logger)
{
    private const int MaxIORetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Per-path filter decision. Returns true if this path should be skipped for the
    /// given client kind. Encapsulates the three-array routing rules in one place so
    /// the walker stays readable.
    ///
    /// The headless allowlist only kicks in inside <see cref="Config.PluginsFolder"/>;
    /// files in patchers/ and config/ skip the allowlist check (still subject to
    /// GlobalExclusions). Mirrors the master-list rule "Headless &amp; Player get all
    /// of these" for non-plugins folders.
    /// </summary>
    private bool ShouldSkip(string path, bool isHeadless)
    {
        // Universal denylist — applies to both client kinds.
        if (config.IsGloballyExcluded(path)) return true;

        if (isHeadless)
        {
            // Headless allowlist scope: BepInEx/plugins only. Outside that, no allowlist.
            if (Config.IsInPluginsFolder(path) && !config.MatchesHeadlessInclude(path))
            {
                return true;
            }
        }
        else
        {
            // Player-only denylist (e.g. Fika.Headless.dll).
            if (config.IsClientExcluded(path)) return true;
        }

        return false;
    }

    /// <summary>
    /// Recursive directory walk. Yields the path of every file we'd consider syncing
    /// for the given client kind, applying exclusions/inclusions as we go. Also yields
    /// any empty directory's own path — clients need to recreate empty dirs on their
    /// side, so we represent them as path entries with an empty hash and
    /// <c>directory=true</c> in the final ModFile.
    ///
    /// `yield return` (vs returning a list): lazy enumeration. Each item is produced on
    /// demand as the caller iterates. Memory stays flat regardless of how big the tree is.
    ///
    /// Subdirectory descent for headless: we ALWAYS recurse into plugins subdirs (even
    /// non-allowlisted ones) and apply the allowlist at the file level. This is wasteful
    /// for big mod sets, but correct in the face of glob-style includes. Smart pruning
    /// can be added later if walk cost becomes noticeable.
    /// </summary>
    public IEnumerable<string> GetFilesInDir(string dir, bool isHeadless)
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
            // A bare-file syncpath (e.g. the built-in Updater). Still subject to filters.
            if (ShouldSkip(dir, isHeadless)) yield break;
            yield return dir;
            yield break;
        }

        var hasContents = false;

        // Files in this directory (non-recursive). EnumerateFiles is lazy — pulls one
        // entry from the OS at a time, doesn't materialize the full list.
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (ShouldSkip(file, isHeadless)) continue;
            yield return file;
            hasContents = true;
        }

        // Subdirectories — recurse into each. Pruning at the dir level only applies to
        // hard deny lists (Global, Client). Headless allowlist is enforced per-file.
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (config.IsGloballyExcluded(subDir)) continue;
            if (!isHeadless && config.IsClientExcluded(subDir)) continue;

            foreach (var x in GetFilesInDir(subDir, isHeadless))
            {
                yield return x;
                hasContents = true;
            }
        }

        // If we walked the whole directory and produced nothing (empty dir, or every
        // child was excluded), yield the dir itself so the client knows to recreate it.
        //
        // Exception: for headless inside the plugins folder, don't yield empty dirs
        // that aren't allowlisted — we shouldn't tell headless to recreate
        // SomeUIMod/ just because we filtered out every file inside.
        if (!hasContents)
        {
            if (isHeadless
                && Config.IsInPluginsFolder(dir)
                && !config.MatchesHeadlessInclude(dir))
            {
                yield break;
            }
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
    /// <paramref name="isHeadless"/> selects the filter set — see class doc for the rules.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, ModFile>>> HashModFilesAsync(
        IEnumerable<SyncPath> syncPaths,
        bool isHeadless)
    {
        var result = new Dictionary<string, Dictionary<string, ModFile>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var startedAt = DateTime.UtcNow;
        var filesHashed = 0;

        foreach (var syncPath in syncPaths)
        {
            var perPath = new Dictionary<string, ModFile>();

            foreach (var file in GetFilesInDir(syncPath.path, isHeadless))
            {
                var winFile = PathExt.WinPath(file);
                if (!seen.Add(winFile)) continue;

                perPath[winFile] = await BuildModFileAsync(file);
                filesHashed++;
            }

            result[PathExt.WinPath(syncPath.path)] = perPath;
        }

        var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
        logger.Info($"Corter-ModSync: hashed {filesHashed} files in {elapsedMs:F0}ms (mode={(isHeadless ? "headless" : "player")}).");

        return result;
    }

    /// <summary>
    /// Resolve and security-check a path the client asked to download. Throws
    /// <see cref="HttpError"/> with status 400 if the resolved path escapes every
    /// configured syncpath (path traversal attempt or stale client request).
    ///
    /// Logic:
    ///   1. Treat `file` as relative to the server's working directory.
    ///   2. Resolve to an absolute path, collapsing `..` and `.` segments.
    ///   3. For each syncpath, resolve its absolute form too. If `Path.GetRelativePath`
    ///      from the syncpath to the requested path doesn't start with ".." then the
    ///      request is inside that syncpath — accept it.
    ///
    /// Note: this does NOT enforce the per-client routing filter. Hash response is the
    /// primary gate; a client asking for a file it wasn't told about would only happen
    /// via a tampered or stale request and is rare. Adding the filter here would add
    /// defense-in-depth but complicates the API. Revisit if it becomes a real concern.
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
