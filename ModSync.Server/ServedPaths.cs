using ModSync.Utility;

namespace ModSync.Server;

/// <summary>
/// One syncPath as the server last served it, persisted between boots in
/// <c>.modsync-served.json</c> next to config.jsonc.
///
/// <paramref name="RetiredAt"/> is null while the entry is live in config.jsonc, and set to
/// the moment the server noticed it had been removed. A retired entry is a TOMBSTONE: still
/// advertised to clients, but with an empty file list, so they uninstall what they still hold.
/// </summary>
public record ServedPath(
    string Path,
    string Name,
    bool Silent,
    bool RestartRequired,
    bool Headless,
    DateTime? RetiredAt = null);

/// <summary>
/// Lets an admin retire a mod in ONE step - delete its files and its syncPath entry - and still
/// have every client uninstall it.
///
/// Without this, deleting the entry means the path never reaches the client's diff at all, so no
/// removal is computed; and because PreviousSync is rewritten from whatever the server served,
/// the record that licenses a removal is erased on the client's very next launch. The mod is then
/// stranded on every machine, permanently, with no way to clean it up.
///
/// The fix reuses the mechanism that already works ("a path served with an empty list means
/// uninstall it" - the same thing that makes a deselected path work), so NO client change is
/// needed and clients on older versions benefit too.
/// </summary>
public static class ServedPaths
{
    public const string FileName = ".modsync-served.json";

    /// <summary>
    /// Diff what we served last boot against the current config and decide what to tombstone.
    /// Pure - takes the existence check and the clock as parameters - so the whole policy is
    /// testable without touching disk.
    /// </summary>
    /// <param name="previous">The record from the last boot. Empty if the file is missing or unreadable.</param>
    /// <param name="current">Admin-configured syncPaths this boot (built-ins excluded - they never retire).</param>
    /// <param name="pathExists">Does this server-relative path still exist on disk?</param>
    public static (List<SyncPath> Tombstones, List<ServedPath> Record) Resolve(
        IReadOnlyList<ServedPath> previous,
        IReadOnlyList<SyncPath> current,
        Func<string, bool> pathExists,
        DateTime now)
    {
        var live = new HashSet<string>(current.Select(sp => sp.path), StringComparer.OrdinalIgnoreCase);
        var retired = new List<ServedPath>();

        foreach (var entry in previous)
        {
            // Back in the config: the admin restored it (or never meant to remove it). Its live
            // record is rebuilt from `current` below, so just stop tracking the tombstone.
            if (live.Contains(entry.Path))
                continue;

            // Already a tombstone. Keep it, with no expiry: a catch-all entry stays in the config
            // forever, which is exactly why the lazy setup removes a mod correctly no matter when
            // a player next logs in. Dropping tombstones on a timer would be the one thing that
            // reintroduces a deadline - a player away longer than the window would keep the mod
            // for good. The cost of keeping one is a line of text, so there's nothing to buy.
            // RetiredAt is recorded for the admin reading the file, not acted on.
            if (entry.RetiredAt is not null)
            {
                retired.Add(entry);
                continue;
            }

            // Served last boot, gone from the config now. Only retire it if the FILES are gone
            // too. Entry removed but files kept is a different, legitimate intent - "stop making
            // this optional, let the catch-all sync it to everyone" - and tombstoning there would
            // delete a mod the admin still wants served.
            if (!pathExists(entry.Path))
                retired.Add(entry with { RetiredAt = now });
        }

        // A tombstone is deliberately plain: enabled (so every client processes it), never
        // optional or enforced, which also keeps it out of the F12 menu. It carries the retired
        // entry's name, silent and restartRequired so the client's update prompt reads exactly
        // as it did when the mod was still configured.
        var tombstones = retired.ConvertAll(entry => new SyncPath(
            path: entry.Path,
            name: entry.Name,
            enabled: true,
            silent: entry.Silent,
            restartRequired: entry.RestartRequired,
            headless: entry.Headless));

        var record = current
            .Select(sp => new ServedPath(sp.path, sp.name, sp.silent, sp.restartRequired, sp.headless))
            .Concat(retired)
            .ToList();

        return (tombstones, record);
    }
}
