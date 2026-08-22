namespace ModSync.Server;

/// <summary>
/// One thing on disk a player could be given: a plugin folder, a loose plugin dll, or a patcher.
/// </summary>
/// <param name="Path">Server-relative, spelled the way config.jsonc spells it (<c>../BepInEx/plugins/SAIN</c>).</param>
/// <param name="Name">Just the leaf, with any <c>.dll</c> trimmed - this is the mod's name as an admin knows it.</param>
/// <param name="Area">Which folder it came from, so the editor can say "plugins" or "patchers".</param>
public record ClientMod(string Path, string Name, string Area);

/// <summary>
/// Lists what is actually installed client-side, so the config editor can offer real mods to pick
/// from instead of asking an admin to type paths.
///
/// **Why this exists at all:** the editor's job is to say who gets which mod, and every mistake an
/// admin can make by hand - a typo, a stale entry, a folder that was renamed - disappears if the
/// choices come from the disk rather than a text box.
///
/// Deliberately NOT recursive. Counting the files under BepInEx/plugins means stat-ing a couple of
/// thousand entries on every page load, and the editor never needs the number - it needs the names.
/// The one count it does show (how many entries a catch-all covers) is a single flat listing.
/// </summary>
public static class ClientModScanner
{
    // Where a client mod can live. These are the three folders the shipped config shares wholesale,
    // and the same three the editor's first section is built from.
    public const string PluginsPath = "../BepInEx/plugins";
    public const string PatchersPath = "../BepInEx/patchers";
    public const string ConfigPath = "../BepInEx/config";

    /// <summary>Every installed plugin and patcher, sorted by name.</summary>
    public static List<ClientMod> Scan(string serverRoot)
    {
        List<ClientMod> mods = [.. Entries(serverRoot, PluginsPath, "plugins"), .. Entries(serverRoot, PatchersPath, "patchers")];
        mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return mods;
    }

    /// <summary>The BepInEx config files, as server-relative paths.</summary>
    public static List<string> ConfigFiles(string serverRoot) =>
        [.. Entries(serverRoot, ConfigPath, "config")
            .Where(e => e.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path)];

    /// <summary>How many entries a folder holds, for "84 entries" next to a catch-all. Flat, not recursive.</summary>
    public static int CountEntries(string serverRoot, string relativePath)
    {
        try
        {
            var dir = Resolve(serverRoot, relativePath);
            return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Best guess at the config file belonging to a mod, or null when there is no confident match.
    ///
    /// Pure, so it can be tested against real names - which matters, because it is only right about
    /// two thirds of the time. Measured against a live server's 84 plugin folders and 50 config
    /// files it matched 32 with no ambiguous cases; the misses are all the same shape, a config
    /// whose words appear in a different order from the folder's
    /// (<c>xyz.drakia.waypoints.cfg</c> belongs to <c>DrakiaXYZ-Waypoints</c>).
    ///
    /// **So the editor must always SHOW what this returns as its own removable row and never attach
    /// it silently.** A wrong guess an admin can see and delete costs nothing; a wrong guess applied
    /// behind their back sends a stranger's config file to every player.
    /// </summary>
    public static string? SuggestConfigFile(string modName, IReadOnlyList<string> configPaths)
    {
        var needle = Normalize(TrimDll(modName));

        // Two characters match half the folder names on a real server. Below that, no guess is
        // better than a confident wrong one.
        if (needle.Length < 3) return null;

        return configPaths.FirstOrDefault(p =>
            Normalize(Path.GetFileNameWithoutExtension(p)).Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>A loose plugin is `NoInsurance.dll` on disk but "No Insurance" to its author - trim the extension.</summary>
    public static string TrimDll(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // Folder and config naming conventions disagree about separators and casing
    // (`acidphantasm-botplacementsystem` vs `com.acidphantasm.botplacementsystem`), so compare on
    // letters and digits alone.
    private static string Normalize(string s) =>
        new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static string Resolve(string serverRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(serverRoot, relativePath.Replace('\\', '/')));

    private static List<ClientMod> Entries(string serverRoot, string relativePath, string area)
    {
        try
        {
            var dir = Resolve(serverRoot, relativePath);
            if (!Directory.Exists(dir)) return [];

            return
            [
                .. Directory.EnumerateFileSystemEntries(dir)
                    .Select(Path.GetFileName)
                    .Where(leaf => !string.IsNullOrEmpty(leaf))
                    .Select(leaf => new ClientMod($"{relativePath}/{leaf}", TrimDll(leaf!), area)),
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable folder means "nothing to offer", never a broken page. The
            // admin can still add paths by hand.
            return [];
        }
    }
}
