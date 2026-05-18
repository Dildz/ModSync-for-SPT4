namespace ModSync.Server;

/// <summary>
/// Path separator + cwd-translation helpers.
///
/// **Separator helpers** (mirror of corter's misc.ts winPath/unixPath):
/// The BepInEx client (Windows-only) expects all paths from the server with **backslashes**.
/// On a Linux server, .NET's `Path.Combine` produces forward slashes, so before sending paths
/// to the client we must convert them. Going the other direction (parsing incoming requests),
/// we normalize to forward slashes for matching against config syncpaths/exclusions.
///
/// **Wire/server translation** (new in SPT 4): the server runs from `&lt;game&gt;/SPT/` but the
/// BepInEx client runs from `&lt;game&gt;/`. So every path crossing the wire needs to be expressed
/// relative to whichever side will resolve it — they're not the same.
///
/// We use **game-root-relative paths as the wire format**, because that's what the client
/// can directly hand to `Path.GetFullPath` / `Path.Combine`. The server stores paths in its
/// own cwd-relative form (which is what Config.cs's defaults produce: `../BepInEx/...` and
/// bare `user/mods`), and translates at the HTTP boundary.
///
/// Translation rules:
/// <list type="bullet">
///   <item>Server `../BepInEx/plugins` (up out of SPT/) ↔ Wire `BepInEx/plugins`</item>
///   <item>Server `user/mods` (inside SPT/) ↔ Wire `SPT/user/mods`</item>
///   <item>Server `../ModSync.Updater.exe` ↔ Wire `ModSync.Updater.exe`</item>
/// </list>
///
/// `static class`: a class that can't be instantiated. Just a namespace for related helpers.
/// </summary>
public static class PathExt
{
    /// <summary>Convert all '/' to '\\' — for paths going out to the Windows BepInEx client.</summary>
    public static string WinPath(string p) => p.Replace('/', '\\');

    /// <summary>Convert all '\\' to '/' — for normalizing paths before glob matching.</summary>
    public static string UnixPath(string p) => p.Replace('\\', '/');

    /// <summary>
    /// Server-cwd-relative path → game-root-relative ("wire") path.
    ///
    /// Strips a leading `../` (path goes up out of SPT/ to game root); otherwise prepends
    /// `SPT/` (path is inside the SPT subdir, client needs the prefix to reach it).
    ///
    /// Separator-aware: handles both `/` and `\`. The output uses whatever the input used
    /// for the separator after the modified prefix — we only mutate the leading 3 chars
    /// (or prepend 4 chars), the rest is untouched.
    /// </summary>
    public static string ToWirePath(string serverPath)
    {
        if (serverPath.StartsWith("../", StringComparison.Ordinal)) return serverPath[3..];
        if (serverPath.StartsWith(@"..\", StringComparison.Ordinal)) return serverPath[3..];
        // No leading "../" → path lives inside the SPT subdir on the server's filesystem.
        // From the client's POV that's `SPT/<whatever>`. Prefer backslash since the rest of
        // the wire format is backslash-normalized via WinPath().
        return @"SPT\" + serverPath;
    }

    /// <summary>
    /// Game-root-relative ("wire") path → server-cwd-relative path. Inverse of <see cref="ToWirePath"/>.
    ///
    /// If the wire path starts with `SPT/` or `SPT\`, strip that prefix (it's inside the
    /// SPT subdir and the server's cwd IS that subdir). Otherwise prepend `../` (server
    /// needs to go up to reach a game-root-level path).
    ///
    /// **Forward slash deliberately**, not backslash: this result feeds into `Path.Combine`
    /// + `Path.GetFullPath` on the server. Windows treats both `/` and `\` as separators
    /// so either works, but Linux (where SPT 4 servers often run in Docker) treats `\` as
    /// a literal filename character. A backslash prefix like `..\BepInEx/...` produces a
    /// mixed-separator string that `Path.GetFullPath` won't normalize on Linux — the
    /// `..\` doesn't get collapsed and the request fails sanitization with a confusing
    /// "not in any enabled sync path" error. Forward slash works on both platforms.
    ///
    /// Only the literal `SPT/` (with trailing separator) is stripped — a syncpath named
    /// `SPTfoo` won't false-match.
    /// </summary>
    public static string ToServerPath(string wirePath)
    {
        if (wirePath.StartsWith("SPT/", StringComparison.Ordinal)) return wirePath[4..];
        if (wirePath.StartsWith(@"SPT\", StringComparison.Ordinal)) return wirePath[4..];
        return "../" + wirePath;
    }
}
