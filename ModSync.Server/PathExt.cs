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
/// **Wire/server translation** (new in SPT 4): the server runs from a subfolder of the game root
/// (`SPT/` on 4.0, renamed to `SPT_Runtime/` on 4.1) but the BepInEx client runs from `&lt;game&gt;/`.
/// So every path crossing the wire needs to be expressed relative to whichever side will resolve
/// it - they're not the same.
///
/// We use **game-root-relative paths as the wire format**, because that's what the client
/// can directly hand to `Path.GetFullPath` / `Path.Combine`. The server stores paths in its
/// own cwd-relative form (which is what Config.cs's defaults produce: `../BepInEx/...` and
/// bare `user/mods`), and translates at the HTTP boundary.
///
/// Translation rules:
/// <list type="bullet">
///   <item>Server `../BepInEx/plugins` (up out of the server folder) ↔ Wire `BepInEx/plugins`</item>
///   <item>Server `user/mods` (inside it) ↔ Wire `SPT_Runtime/user/mods`</item>
///   <item>Server `../ModSync.Updater.exe` ↔ Wire `ModSync.Updater.exe`</item>
/// </list>
///
/// `static class`: a class that can't be instantiated. Just a namespace for related helpers.
/// </summary>
public static class PathExt
{
    /// <summary>
    /// The server folder's name as the client sees it from the game root: `SPT` on SPT 4.0,
    /// `SPT_Runtime` on 4.1.
    ///
    /// Derived from the working directory rather than hardcoded. Every other server-side path
    /// (`../BepInEx/...`, `user/mods`) is resolved against that same cwd, so the two can never
    /// legitimately disagree - if they did, the `../` paths would already be resolving into the
    /// wrong place. Hardcoding it is what made 4.1 advertise `user/mods` as `SPT\user\mods` and
    /// land it in `&lt;gameRoot&gt;/SPT/`, a folder that doesn't exist on 4.1.
    ///
    /// Read once: the server never changes directory after startup.
    /// </summary>
    private static readonly string ServerFolder = new DirectoryInfo(Directory.GetCurrentDirectory()).Name;

    /// <summary>Convert all '/' to '\\' - for paths going out to the Windows BepInEx client.</summary>
    public static string WinPath(string p) => p.Replace('/', '\\');

    /// <summary>Convert all '\\' to '/' - for normalizing paths before glob matching.</summary>
    public static string UnixPath(string p) => p.Replace('\\', '/');

    /// <summary>
    /// Server-cwd-relative path → game-root-relative ("wire") path.
    ///
    /// Strips a leading `../` (path goes up out of the server folder to game root); otherwise
    /// prepends the server folder's name (path is inside it, client needs the prefix to reach it).
    ///
    /// Separator-aware: handles both `/` and `\`. The output uses whatever the input used
    /// for the separator after the modified prefix - we only mutate the leading 3 chars
    /// (or prepend 4 chars), the rest is untouched.
    /// </summary>
    public static string ToWirePath(string serverPath)
    {
        if (serverPath.StartsWith("../", StringComparison.Ordinal)) return serverPath[3..];
        if (serverPath.StartsWith(@"..\", StringComparison.Ordinal)) return serverPath[3..];
        // No leading "../" → path lives inside the server folder on the server's filesystem.
        // From the client's POV that's `<serverFolder>/<whatever>`. Prefer backslash since the
        // rest of the wire format is backslash-normalized via WinPath().
        return ServerFolder + @"\" + serverPath;
    }

    /// <summary>
    /// Game-root-relative ("wire") path → server-cwd-relative path. Inverse of <see cref="ToWirePath"/>.
    ///
    /// If the wire path starts with the server folder's name (either separator), strip that
    /// prefix - it's inside the server folder and the server's cwd IS that folder. Otherwise
    /// prepend `../` (server needs to go up to reach a game-root-level path).
    ///
    /// **Forward slash deliberately**, not backslash: this result feeds into `Path.Combine`
    /// + `Path.GetFullPath` on the server. Windows treats both `/` and `\` as separators
    /// so either works, but Linux (where SPT 4 servers often run in Docker) treats `\` as
    /// a literal filename character. A backslash prefix like `..\BepInEx/...` produces a
    /// mixed-separator string that `Path.GetFullPath` won't normalize on Linux - the
    /// `..\` doesn't get collapsed and the request fails sanitization with a confusing
    /// "not in any enabled sync path" error. Forward slash works on both platforms.
    ///
    /// Only the folder name followed by a separator is stripped, so on 4.0 a syncpath named
    /// `SPTfoo` won't false-match `SPT`.
    /// </summary>
    public static string ToServerPath(string wirePath)
    {
        if (wirePath.StartsWith(ServerFolder + "/", StringComparison.Ordinal)) return wirePath[(ServerFolder.Length + 1)..];
        if (wirePath.StartsWith(ServerFolder + @"\", StringComparison.Ordinal)) return wirePath[(ServerFolder.Length + 1)..];
        return "../" + wirePath;
    }
}
