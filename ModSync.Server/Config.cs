using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;

namespace ModSync.Server;

/// <summary>
/// Processed config - what the rest of the server uses. Holds:
///   • SyncPaths: built-ins + user paths, sorted (longest first so deeper matches win)
///   • Exclusions: compiled glob list applied to every walk (universal denylist)
///   • HeadlessIncludes: compiled allowlist scoped to BepInEx/plugins for headless clients
///
/// `SyncPath` is the shared model from `ModSync.Utility` (also used by the client).
/// Keeping one shape between client/server means the JSON contract is implicit, not duplicated.
/// </summary>
public class Config(
    List<SyncPath> syncPaths,
    List<string> exclusions,
    List<string> headlessIncludes,
    List<string> managedIncludes,
    List<string> headlessManagedIncludes,
    List<string>? tombstones = null)
{
    public readonly List<SyncPath> SyncPaths = syncPaths;
    public readonly List<string> Exclusions = exclusions;
    public readonly List<string> HeadlessIncludes = headlessIncludes;
    public readonly List<string> ManagedIncludes = managedIncludes;
    public readonly List<string> HeadlessManagedIncludes = headlessManagedIncludes;

    // Compile globs once at construction - cheaper than recompiling per file check.
    // `readonly` here means the reference can't change, but the contents (regex internal
    // state) are still mutable. C#'s equivalent of TypeScript's `readonly` array.
    private readonly List<Regex> _exclusionGlobs = exclusions.ConvertAll(Glob.Create);

    // headlessIncludes uses plain prefix matching instead of globs - globs aren't
    // supported (by design; see config comments) so there's nothing to compile.
    private readonly List<string> _normalizedHeadlessIncludes =
        headlessIncludes.ConvertAll(e => PathExt.UnixPath(e).TrimEnd('/'));

    // managedIncludes entries are plain filenames (e.g. "Unity.VectorGraphics.dll").
    // Store as a case-insensitive set - Windows filesystems are case-insensitive and
    // we want "unity.vectorgraphics.dll" to match regardless of how the admin typed it.
    private readonly HashSet<string> _managedIncludesSet =
        new(managedIncludes, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _headlessManagedIncludesSet =
        new(headlessManagedIncludes, StringComparer.OrdinalIgnoreCase);

    // Retired syncPaths, advertised with an empty file list so clients uninstall them.
    // Their folders are gone from the server by definition, so the walk must be skipped
    // rather than attempted - see IsTombstone.
    private readonly HashSet<string> _tombstones =
        new(tombstones ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if this syncpath is a tombstone: retired from config.jsonc, its files already gone
    /// from the server, kept in the response only so clients remove their copies. Walking it
    /// would log "does not exist, will be ignored", which is the opposite of what's happening.
    /// </summary>
    public bool IsTombstone(string path) => _tombstones.Contains(path);


    /// <summary>True if filePath matches any of the configured exclusion globs.</summary>
    public bool IsExcluded(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _exclusionGlobs.Exists(g => g.IsMatch(normalized));
    }

    /// <summary>
    /// True if filePath matches any headlessIncludes entry. Only meaningful for files
    /// inside BepInEx/plugins when serving a headless client - callers gate by syncpath
    /// scope before consulting this. Empty allowlist => everything filtered out (the
    /// "you haven't configured headless yet" default).
    ///
    /// Entries can be a folder ("../BepInEx/plugins/SAIN") or an exact file
    /// ("../BepInEx/plugins/Foo.dll"). A folder entry matches the folder itself
    /// (empty-dir sentinel) and all files inside it. Exact-file entries match only
    /// that one file. Simple prefix matching - no glob support here by design.
    /// </summary>
    public bool IsHeadlessAllowed(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _normalizedHeadlessIncludes.Exists(entry =>
            normalized == entry || normalized.StartsWith(entry + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// True if the given filename appears in managedIncludes. Callers pass
    /// Path.GetFileName(filePath) - the allowlist is filename-only, not full path,
    /// so it works identically whether the server's Managed folder is a Docker
    /// staging directory (2 files) or a full Windows EFT install (169 files).
    /// </summary>
    public bool IsManagedAllowed(string fileName) => _managedIncludesSet.Contains(fileName);

    /// <summary>Same as IsManagedAllowed but for headless clients.</summary>
    public bool IsHeadlessManagedAllowed(string fileName) => _headlessManagedIncludesSet.Contains(fileName);

}

/// <summary>
/// Raw shape of the on-disk config.jsonc. Used only at parse time, then discarded -
/// the validated/normalized output is a `Config` instance.
///
/// `syncPaths` is `List&lt;JsonElement&gt;` because each entry can be either a plain
/// string `"BepInEx/plugins"` or a full object `{ "path": "...", "enabled": false }`.
/// JsonElement lets us inspect at parse time and convert either case.
/// </summary>
public record RawConfig
{
    [JsonPropertyName("syncPaths")] public List<JsonElement> SyncPaths { get; init; } = [];
    [JsonPropertyName("exclusions")] public List<string> Exclusions { get; init; } = [];
    [JsonPropertyName("headlessIncludes")] public List<string> HeadlessIncludes { get; init; } = [];
    [JsonPropertyName("managedIncludes")] public List<string> ManagedIncludes { get; init; } = [];
    [JsonPropertyName("headlessManagedIncludes")] public List<string> HeadlessManagedIncludes { get; init; } = [];
}

/// <summary>
/// Loads and validates config.jsonc. Single-call public API: `LoadAsync()`.
///
/// `[Injectable]` registers this class with SPT's DI container so other classes can take
/// it as a constructor parameter. Logger comes in via DI too - same pattern as ModSyncMod.
/// </summary>
[Injectable]
public class ConfigUtil(ISptLogger<ConfigUtil> logger)
{
    /// <summary>
    /// Default config written to disk on first run. Five top-level keys:
    ///   • syncPaths              - folders the server walks and offers
    ///   • exclusions             - universal denylist (every client, player and headless)
    ///   • headlessIncludes       - allowlist scoped to BepInEx/plugins for Fika headless clients
    ///   • managedIncludes        - allowlist for EscapeFromTarkov_Data/Managed DLLs (players)
    ///   • headlessManagedIncludes - same, for Fika headless
    ///
    /// Per-player opt-outs live CLIENT-SIDE in each install's
    /// <c>&lt;game&gt;/ModSync_Data/Exclusions.jsonc</c> - admins don't manage those.
    ///
    /// SPT 4 path note: server runs from <c>&lt;gameRoot&gt;/SPT/</c>, so <c>../BepInEx/...</c>
    /// reaches the client-side BepInEx folder. SPT 3 used plain <c>BepInEx/...</c>.
    /// </summary>
    private const string DefaultConfig = """
        {
            // ┌─────────────────────────────────────────────────────────────────────┐
            // │  WIKI - FULL CONFIG GUIDE:                                          │
            // │  https://github.com/Dildz/ModSync-for-SPT4/wiki/Configuration       │
            // └─────────────────────────────────────────────────────────────────────┘

            // SPT 4 directory layout: server runs from <gameRoot>/SPT/.
            //   "<gameRoot>/BepInEx/..."  →  client-side mods (../BepInEx/...)
            //
            // The `user/mods` folder is NOT a syncPath. Server mods stay server-only
            // by design - their client-facing components ship as separate BepInEx
            // plugins, which DO sync via the paths below. (One edge case can justify
            // changing that; it's written up on the wiki, deliberately not here.)
            //
            // Each path/entry is EITHER:
            //   1. A plain string  - sync this path with all defaults (what we use below).
            //   2. An object       - the same path, with some of those defaults overridden.
            //
            // The eight options:
            //
            //     "path"            (required)        Folder or file to sync. Globs NOT allowed.
            //     "name"            (default: path)   Friendly label shown in the client's F12 sync menu.
            //                                         Keep these unique - the client stores each F12
            //                                         toggle under its name.
            //     "enabled"         (default: true)   true = synced to everyone by default,
            //                                         false = opt-in, the client ticks it in F12 first.
            //                                         Whether the client can CHANGE that is decided by
            //                                         "optional" and "enforced" below.
            //     "optional"        (default: false)  true = the client gets a working F12 checkbox for
            //                                         this path, starting in the "enabled" state. Use it
            //                                         for a mod installed by default but still refusable
            //                                         ("enabled": false is already a checkbox, so opt-in
            //                                         mods don't need this). Unticking UNINSTALLS what
            //                                         ModSync put there - never mark a catch-all optional.
            //     "enforced"        (default: false)  true = server wins, always. Client can't opt out,
            //                                         can't keep local edits, can't exclude it. Use for
            //                                         files every client MUST match (e.g. patchers).
            //     "restartRequired" (default: true)   Must the client restart after these files update?
            //     "silent"          (default: false)  false = show the client a prompt of changes.
            //                                         true  = apply quietly in the background on load.
            //     "headless"        (default: true)   false = NEVER send to a Fika headless client.
            //                                         Headless has no F12 menu so it ignores the opt-in
            //                                         toggles and takes everything offered - this is the
            //                                         only way to mark a mod player-only (GPU/UI mods).
            //     "baseFiles"       (default: none)   Base-game files this mod REPLACES, living outside
            //                                         its own folder. They follow this entry's opt-in
            //                                         state, so opted-out players never receive them.
            //                                         The original is saved as <file>.modsync-bak and
            //                                         restored if the mod is later removed. See the
            //                                         "mods that replace base-game files" note below.
            //
            // Paths are matched MOST-SPECIFIC FIRST, so you can set defaults on a folder
            // and override just one child inside it (e.g. enforce a single config file).
            //
            // RETIRING A MOD: delete its files from the server AND delete its entry here.
            // ModSync remembers what it served (.modsync-served.json, next to this file) and
            // keeps offering the removed entry as EMPTY, which is what tells a client to
            // uninstall its copy - whenever it next connects, however long that takes.
            // Re-adding the entry cancels it; otherwise clear the record file when you're done.
            // Delete the entry but KEEP the files and the opposite happens, by design: the
            // entry was carving the mod out of the catch-alls, so without it they take over
            // and sync the mod to EVERYONE.
            //
            // TWO WAYS TO USE THIS (it's one dial, not two modes - see the wiki):
            //   Lazy    - keep just the 3 catch-alls below; everything syncs and each
            //             player declines what they don't want via their own
            //             ModSync_Data/Exclusions.jsonc. Best for large modsets.
            //   Curated - ALSO name mods as objects, using "enabled" / "optional" to put
            //             them in the player's F12 menu as toggles. Best for slim sets.
            //   Mix     - name the few you want optional; the catch-alls cover the rest.
            "syncPaths": [
                "../BepInEx/plugins",
                "../BepInEx/patchers",
                "../BepInEx/config"

                // ── Object-form examples (uncomment & adapt) ──
                // Enforce patchers - a version mismatch on a patcher DLL can otherwise
                // leave a client silently stuck:
                //
                // {
                //     "path": "../BepInEx/patchers",
                //     "name": "Preloader patchers",
                //     "enforced": true
                // }
                //
                // Opt-IN mod - shows in the F12 menu unticked; the player ticks it to
                // receive it. A mod's plugin and config are SEPARATE paths, so to make
                // its config optional too, add its ../BepInEx/config file the same way:
                //
                // {
                //     "path": "../BepInEx/plugins/SomeOptionalMod",
                //     "name": "Optional: Some Mod",
                //     "enabled": false,
                //     "restartRequired": false
                // }
                //
                // Opt-OUT mod - synced by default, but the player can untick it in the F12
                // menu, which uninstalls it. For a mod you want everyone on unless they
                // actively say no:
                //
                // {
                //     "path": "../BepInEx/plugins/SomeClientMod",
                //     "name": "Optional: Some Client Mod",
                //     "optional": true,
                //     "restartRequired": false
                // }
                //
                // Do NOT mark the three catch-alls above optional - they carry the mods
                // your players need to connect, so one untick would strip the modset.
                //
                // ── Mods that REPLACE base-game files ──
                // Some mods ship modified copies of files the game itself installs, rather
                // than adding their own. Those files must follow the mod's opt-in state, or
                // a player who declined the mod ends up with its modified engine files and
                // no mod - a broken install. List them in "baseFiles" and ModSync backs the
                // original up as <file>.modsync-bak, restoring it if the mod is removed.
                //
                // IMPORTANT: if a player installed the mod BY HAND, no backup exists, so
                // ModSync has no original to put back. It then refuses to remove that mod at
                // all and says so in the F12 menu - deleting a base-game file it never
                // replaced could break the client beyond repair.
                //
                // Tarkov DLSS 4.5 - replaces the NVIDIA runtime DLL. Only benefits RTX
                // 3000-series and newer, and is useless on a headless, so: opt-in + no
                // headless. One entry covers BOTH halves of the mod:
                //
                // {
                //     "path": "../BepInEx/patchers/TarkovDLSS45",
                //     "name": "(Optional) Tarkov DLSS 4.5",
                //     "enabled": false,
                //     "headless": false,
                //     "baseFiles": [
                //         "../EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"
                //     ]
                // }
                //
                // Known issue with this mod (not ModSync): it extends the game's DLSS
                // presets, and a "Default" preset gets written in a form the game can
                // no longer read back, hanging the client on the loading screen. Tell
                // players to pick an explicit DLSS preset (not "Default") BEFORE the
                // mod installs. If a client does hang, edit the "DLSSPreset" line in
                // user/sptSettings/Graphics.ini to a real preset - that keeps their
                // other graphics settings. Removing the mod also leaves the preset on
                // a mod-added value, so the same edit applies.
                //
                // DynamicMaps - replaces two Unity assemblies. Unlike the DLSS DLL these
                // CANNOT be re-downloaded, so a hand-installed copy is unrecoverable.
                // headless:false - a headless renders nothing, so a map overlay is dead
                // weight there, and it must never receive DM's replacement assemblies:
                //
                // {
                //     "path": "../BepInEx/plugins/DynamicMaps",
                //     "name": "(Optional) Dynamic Maps",
                //     "enabled": false,
                //     "headless": false,
                //     "baseFiles": [
                //         "../EscapeFromTarkov_Data/Managed/Unity.VectorGraphics.dll",
                //         "../EscapeFromTarkov_Data/Managed/Unity.InternalAPIEngineBridge.003.dll"
                //     ]
                // }
            ],

            //-----------------------------------------------------------------------

            // Paths ignored by ModSync (players AND headless alike).
            // Use for SPT internals, per-instance state files mods don't want
            // overwritten, and the universal `.nosync` opt-out pattern.
            // See the wiki [exclusions] for guidance on what belongs here.
            "exclusions": [
                // SPT / BepInEx baseline - NOT mods. These ship with SPT (or are
                // generated per-machine by BepInEx), so every client already has its
                // own. ModSync doesn't manage SPT itself, and an SPT update replaces
                // them - never push the host's copies over a client's.
                "../BepInEx/plugins/spt",
                "../BepInEx/patchers/spt-prepatch.dll",
                "../BepInEx/config/BepInEx.cfg",
                "../BepInEx/config/com.bepis.bepinex.configurationmanager.cfg",

                // Fika headless DLL - must never reach regular players. Headless
                // instances get this from their image / manual install, not via sync.
                "../BepInEx/plugins/Fika/Fika.Headless.dll",

                // Raid-Review DLL - must never reach regular players. Headless
                // instances get this from the 'headlessIncludes allow list'.
                // "../BepInEx/plugins/RAID_REVIEW.dll",

                // Universal per-file opt-out - drop a `.nosync` or `.nosync.txt` file
                // next to any mod folder/file to skip it
                "**/*.nosync",
                "**/*.nosync.txt",

                // Git repo metadata - some mods are GitHub-only and may contain files
                // that aren't needed at runtime.
                "**/.git",

                // ModSync's own server-side bookkeeping (what it served last boot, used to
                // retire removed mods). Only reachable at all if you sync "user/mods";
                // there is no reason for a client to carry the server's record.
                "**/.modsync-served.json"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST for Fika headless clients, scoped to ../BepInEx/plugins ONLY.
            // patchers + config flow through to headless unfiltered (the universal
            // `exclusions` above still applies).
            //
            // A headless client only receives plugin paths that match an entry here.
            // Empty array (the default) means headless gets ZERO plugins - you MUST
            // populate this if you run a headless instance.
            //
            // Entries should be EXPLICIT - either a mod folder or an individual DLL.
            // Don't use globs like "*.dll" here. The whole point of an allowlist is
            // being deliberate about what reaches headless; a careless glob can
            // accidentally re-include everything you meant to keep out.
            //   - a folder:     "../BepInEx/plugins/SAIN"                      (matches the folder + contents)
            //   - an exact DLL: "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll"    (matches just that file)
            //
            // See the wiki [headlessIncludes] for extra details.
            "headlessIncludes": [
                // Fika (Core + Headless.dll both live in this folder) and ModSync
                "../BepInEx/plugins/Fika",
                "../BepInEx/plugins/Corter-ModSync"   // ← add a comma here before uncommenting below

                // Bot AI + behavior (example entries - add your own below)
                // "../BepInEx/plugins/SAIN",
                // "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
                // "../BepInEx/plugins/DrakiaXYZ-Waypoints"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST for EscapeFromTarkov_Data/Managed DLL files.
            //
            // Some mods ship Unity assemblies that need to be placed in
            // EscapeFromTarkov_Data/Managed/ on every player client. List the
            // DLL filenames here (filename only - no folder path).
            //
            // The server reads from ../EscapeFromTarkov_Data/Managed/ but only serves
            // the files you list here. This keeps it safe in both setups:
            //   - Docker/Linux server: that folder is a staging area with ONLY mod DLLs
            //   - Windows (host is also a player): that folder contains the full EFT
            //     install - the allowlist prevents vanilla Unity DLLs from being synced
            //
            // On install:  if the file already exists on the client, the original is
            //              backed up as <filename>.modsync-bak before being replaced.
            // On removal:  if a .modsync-bak exists, the original is restored automatically.
            //              If no backup was made (file was new), the file is deleted.
            // See the wiki [managedIncludes] for extra details.
            "managedIncludes": [
                // DynamicMaps example - remove or replace with your own mod's files:
                // "Unity.VectorGraphics.dll",
                // "Unity.InternalAPIEngineBridge.003.dll"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST - but for Fika headless clients.
            //
            // Headless runs the game simulation without the rendering stack, so Managed
            // files are almost never needed there. Leave this empty unless a mod's install
            // instructions specifically say it is required on headless.
            "headlessManagedIncludes": [
            ]
        }
        """;

    /// <summary>
    /// JSON parsing options that accept comments and trailing commas - what makes ".jsonc" jsonc.
    /// Reused across all parse calls (System.Text.Json caches metadata when options are reused).
    /// </summary>
    private static readonly JsonSerializerOptions JsoncOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Options for the served-paths record. Indented on purpose - it's bookkeeping an admin may
    /// want to read or clear by hand, not a wire format.
    /// </summary>
    private static readonly JsonSerializerOptions ServedRecordOptions = new() { WriteIndented = true };

    /// <summary>
    /// Resolve the mod's on-disk directory by inspecting where this assembly was loaded from.
    /// At runtime SPT loads us from `&lt;gameRoot&gt;/SPT/user/mods/Corter-ModSync/`, so the
    /// returned path is exactly that - where we'll read/write `config.jsonc`.
    /// </summary>
    private static string GetModDirectory()
    {
        var assemblyPath = typeof(ConfigUtil).Assembly.Location;
        return Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException("Corter-ModSync: could not resolve mod directory from assembly location");
    }

    /// <summary>
    /// Absolute path to the live config.jsonc. Public because the web editor reads and writes the
    /// same file, and a second copy of this expression is a second chance for the two to disagree
    /// about which file is being edited.
    /// </summary>
    public static string ConfigFilePath => Path.Combine(GetModDirectory(), "config.jsonc");

    /// <summary>
    /// Read config.jsonc from disk; write the default if it doesn't exist yet. Returns the
    /// raw parsed shape (still unvalidated).
    /// </summary>
    private async Task<RawConfig> ReadConfigFileAsync()
    {
        var configPath = ConfigFilePath;

        if (!File.Exists(configPath))
        {
            logger.LogWithColor($"Corter-ModSync: no config.jsonc found at {configPath}, writing defaults.", Color.Grey);
            await File.WriteAllTextAsync(configPath, DefaultConfig);
        }

        // Always drop a read-only reference copy of the CURRENT defaults next to the live
        // config. An existing config.jsonc is never overwritten (an admin's syncPaths /
        // headlessIncludes are irreplaceable), which also means they'd otherwise never see
        // options or exclusions added in a later version. This gives them something to diff
        // against. Written unconditionally so the reference can't go stale; it lives under
        // user/mods, which ModSync never walks, so it can't leak to clients.
        try
        {
            await File.WriteAllTextAsync(Path.Combine(GetModDirectory(), "config_default.jsonc"), DefaultConfig);
        }
        catch (Exception e)
        {
            // Reference-only - never block startup over it.
            logger.LogWithColor($"Corter-ModSync: could not write config_default.jsonc: {e.Message}", Color.Grey);
        }

        var text = await File.ReadAllTextAsync(configPath);
        var raw = JsonSerializer.Deserialize<RawConfig>(text, JsoncOptions)
            ?? throw new InvalidOperationException("Corter-ModSync: config.jsonc parsed to null (file is empty or malformed).");

        NotifyAboutMissingOptions(text);

        return raw;
    }

    /// <summary>
    /// Name any top-level options the admin's config predates, so a new feature isn't invisible
    /// to everyone who already had a config.jsonc (which is never overwritten).
    ///
    /// Deliberately only NOTIFIES - it does not rewrite the file. Merging the missing block in
    /// would be doable (it's text), but the config is hand-curated and heavily commented, and
    /// silently editing someone's 30-line headlessIncludes to add a key they can read about in
    /// config_default.jsonc is a bad trade. Nothing breaks either way: RawConfig defaults every
    /// key, so a missing option just means that feature is off until they opt in.
    ///
    /// Only TOP-LEVEL keys are checked. Per-syncpath options (`headless`, `baseFiles`, …) are
    /// optional per entry and can't be "missing" - they simply take their default.
    ///
    /// Advisory only: any parse trouble here is swallowed, because the real parse already
    /// succeeded by the time we're called and a cosmetic check must never break startup.
    /// </summary>
    private void NotifyAboutMissingOptions(string text)
    {
        var missing = MissingTopLevelOptions(text, DefaultConfig);
        if (missing.Count == 0)
            return;

        logger.LogWithColor(
            $"Corter-ModSync: your config.jsonc predates these options: {string.Join(", ", missing)}. "
            + "Defaults are in use - see config_default.jsonc alongside it for the documented versions.",
            Color.Grey);
    }

    /// <summary>
    /// Top-level keys present in <paramref name="defaultText"/> but absent from
    /// <paramref name="configText"/>. Pure so the comparison can be tested directly.
    /// Returns empty on any parse trouble - see <see cref="NotifyAboutMissingOptions"/>.
    /// </summary>
    public static List<string> MissingTopLevelOptions(string configText, string defaultText)
    {
        try
        {
            var docOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            using var theirs = JsonDocument.Parse(configText, docOptions);
            using var defaults = JsonDocument.Parse(defaultText, docOptions);

            if (theirs.RootElement.ValueKind != JsonValueKind.Object
                || defaults.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var present = theirs.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return defaults.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !present.Contains(name))
                .ToList();
        }
        catch
        {
            // Cosmetic advisory - never let it affect startup.
            return [];
        }
    }

    /// <summary>Exposed so tests can compare a config against the shipped defaults.</summary>
    public static string ShippedDefaultConfig => DefaultConfig;

    /// <summary>
    /// Pull the .path string out of a syncpath entry (which may be either a bare string
    /// or an object with a "path" property). Throws if neither form matches.
    /// </summary>
    private static string ExtractPath(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            return entry.GetString()!;
        }

        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        {
            return p.GetString()!;
        }

        throw new InvalidOperationException(
            "Corter-ModSync: config.jsonc 'syncPaths' entry must be a string OR an object with a 'path' property.");
    }

    /// <summary>
    /// Apply validation rules and throw on the first violation.
    /// </summary>
    private static void Validate(RawConfig raw)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in raw.SyncPaths)
        {
            // Reject objects whose `name` is the wrong type or has chars that would break the client.
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null)
            {
                if (n.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException(
                        "Corter-ModSync: config.jsonc 'syncPaths.name' must be a string.");
                }

                var name = n.GetString()!;
                if (Regex.IsMatch(name, "[\n\t\\\\\"'\\[\\]]"))
                {
                    throw new InvalidOperationException(
                        "Corter-ModSync: config.jsonc 'syncPaths.name' contains invalid characters. Disallowed: newline, tab, \\ \" ' [ ]");
                }
            }

            var path = ExtractPath(entry);

            if (Path.IsPathRooted(path))
            {
                throw new InvalidOperationException(
                    $"Corter-ModSync: SyncPaths must be relative paths. Invalid: '{path}'");
            }

            if (!seenPaths.Add(path))
            {
                throw new InvalidOperationException(
                    $"Corter-ModSync: SyncPaths must be unique. Duplicate: '{path}'");
            }

            if (raw.Exclusions.Contains(path))
            {
                throw new InvalidOperationException(
                    $"Corter-ModSync: '{path}' is in BOTH syncPaths and exclusions. This isn't doing what you want - remove from one.");
            }
        }
    }

    /// <summary>
    /// Convert one raw syncpath entry into a `SyncPath` instance, filling in defaults
    /// for any field the user didn't specify. Bare-string form gets all defaults.
    /// </summary>
    private static SyncPath BuildSyncPath(JsonElement entry)
    {
        const bool defaultEnabled = true;
        const bool defaultOptional = false;
        const bool defaultEnforced = false;
        const bool defaultSilent = false;
        const bool defaultRestartRequired = true;
        const bool defaultHeadless = true;

        var path = ExtractPath(entry);

        if (entry.ValueKind == JsonValueKind.String)
        {
            return new SyncPath(
                path: path,
                name: path,
                enabled: defaultEnabled,
                enforced: defaultEnforced,
                silent: defaultSilent,
                restartRequired: defaultRestartRequired);
        }

        return new SyncPath(
            path: path,
            name: entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : path,
            enabled: entry.TryGetProperty("enabled", out var en) && en.ValueKind != JsonValueKind.Null
                ? en.GetBoolean()
                : defaultEnabled,
            optional: entry.TryGetProperty("optional", out var op) && op.ValueKind != JsonValueKind.Null
                ? op.GetBoolean()
                : defaultOptional,
            enforced: entry.TryGetProperty("enforced", out var ef) && ef.ValueKind != JsonValueKind.Null
                ? ef.GetBoolean()
                : defaultEnforced,
            silent: entry.TryGetProperty("silent", out var sl) && sl.ValueKind != JsonValueKind.Null
                ? sl.GetBoolean()
                : defaultSilent,
            restartRequired: entry.TryGetProperty("restartRequired", out var rr) && rr.ValueKind != JsonValueKind.Null
                ? rr.GetBoolean()
                : defaultRestartRequired,
            headless: entry.TryGetProperty("headless", out var hl) && hl.ValueKind != JsonValueKind.Null
                ? hl.GetBoolean()
                : defaultHeadless,
            baseFiles: entry.TryGetProperty("baseFiles", out var bf) && bf.ValueKind == JsonValueKind.Array
                ? bf.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : []);
    }

    // Server-cwd-relative paths of the two audience-specific builtins. The Updater is a
    // desktop-player tool; the patcher applies updates on headless. ModSyncHttpListener flips
    // their `enforced` flag per audience at serve time via ResolveEnforced.
    public const string UpdaterSyncPath = Builtins.UpdaterPath;
    public const string PatcherSyncPath = Builtins.PatcherPath;

    /// <summary>
    /// Per-audience enforcement for the two audience-specific builtins. The Updater (desktop
    /// players only) is enforced for players and relaxed for headless; the patcher (headless
    /// only) is enforced for headless and relaxed for players. "Enforced" means the client
    /// can't drop it via Exclusions.jsonc - so each audience can trim the component it never
    /// runs, yet can't accidentally remove the one it depends on. All other paths keep their
    /// configured value.
    /// </summary>
    public static bool ResolveEnforced(SyncPath syncPath, bool isHeadless)
    {
        if (syncPath.path == UpdaterSyncPath) return !isHeadless;
        if (syncPath.path == PatcherSyncPath) return isHeadless;
        return syncPath.enforced;
    }

    /// <summary>
    /// Public entry point. Reads disk, validates, builds the final Config including:
    ///   1) three prepended built-in syncpaths (ModSync's plugin, Updater, and patcher - always
    ///      synced so users can't accidentally desync the mod itself; Updater/patcher enforcement
    ///      is then made audience-specific at serve time, see ResolveEnforced)
    ///   2) user-defined syncpaths from the file
    ///   3) sorted by path length descending - when a file matches multiple syncpaths, the
    ///      more-specific (longer) path wins (so the patcher FILE builtin overrides the enclosing
    ///      ../BepInEx/patchers folder).
    /// </summary>
    public async Task<Config> LoadAsync()
    {
        var raw = await ReadConfigFileAsync();
        Validate(raw);

        // Built-ins use ../ prefix for SPT 4 layout (server runs from <gameRoot>/SPT/).
        // `enforced=true` means the client always re-syncs these even if the user deleted them,
        // and `silent=true` hides them from the UI's "files about to update" prompt.
        //
        // The Updater and patcher carry their DEFAULT (player-facing) enforcement here;
        // ModSyncHttpListener flips it per audience at serve time via ResolveEnforced. The patcher
        // is a dedicated FILE builtin rather than relying on the ../BepInEx/patchers folder so
        // enforcement stays scoped to ModSync's own patcher - enforcing the whole folder would
        // delete excluded-but-required files like spt-prepatch.dll.
        var builtins = new List<SyncPath>
        {
            new(
                path: UpdaterSyncPath,
                name: "(Builtin) ModSync Updater",
                enabled: true, enforced: true, silent: true, restartRequired: false),
            // enabled:false makes this a player-selectable F12 entry rather than a silent
            // always-on path. The client seeds an opt-in toggle's default from the INSTALLED
            // state, so a player who has the patcher (it ships in the release zip) still gets
            // it checked by default - unchecking it is what removes the file they never run.
            // Headless is unaffected: ResolveEnforced enforces this path for headless, and a
            // headless ignores toggles entirely and syncs every configured path.
            new(
                path: PatcherSyncPath,
                name: "(Builtin) ModSync Patcher",
                enabled: false, enforced: false, silent: true, restartRequired: true),
            new(
                path: Builtins.PluginPath,
                name: "(Builtin) ModSync Plugin",
                enabled: true, enforced: true, silent: true, restartRequired: true),
        };

        // Add the Managed syncpath only when at least one list is populated.
        // Not enforced - players can opt out. restartRequired because replacing a Unity
        // assembly takes effect only after EFT restarts.
        if (raw.ManagedIncludes.Count > 0 || raw.HeadlessManagedIncludes.Count > 0)
        {
            builtins.Add(new SyncPath(
                path: "../EscapeFromTarkov_Data/Managed",
                name: "(Builtin) Managed Files",
                enabled: true, enforced: false, silent: false, restartRequired: true));
        }


        var userPaths = raw.SyncPaths.ConvertAll(BuildSyncPath);

        // `optional` asks for a client checkbox; `enforced` says the client gets no say.
        // Enforcement wins, so the checkbox never appears - say why instead of leaving the
        // admin to wonder. Advisory only: the pair is contradictory, not dangerous.
        foreach (var sp in userPaths.Where(sp => sp.optional && sp.enforced))
        {
            logger.Warning(
                $"Corter-ModSync: syncPath '{sp.name}' sets both 'optional' and 'enforced'. "
                + "Enforced wins - it shows in the client's F12 menu as read-only, with no checkbox.");
        }

        var tombstones = ResolveTombstones(userPaths);

        // Concatenate, then sort by descending path length so more-specific matches take precedence.
        // Tombstones join the sort like any other path - a retired entry must still out-rank the
        // catch-all that encloses it, or the catch-all would claim its slot in the response.
        var allPaths = new List<SyncPath>(builtins.Count + userPaths.Count + tombstones.Count);
        allPaths.AddRange(builtins);
        allPaths.AddRange(userPaths);
        allPaths.AddRange(tombstones);
        allPaths.Sort((a, b) => b.path.Length.CompareTo(a.path.Length));

        return new Config(
            allPaths,
            raw.Exclusions,
            raw.HeadlessIncludes,
            raw.ManagedIncludes,
            raw.HeadlessManagedIncludes,
            tombstones.ConvertAll(sp => sp.path));
    }

    /// <summary>
    /// Read the last-served record, work out which retired paths still need advertising as
    /// tombstones, and write the record back. See <see cref="ServedPaths"/> for why this exists.
    ///
    /// Every failure path here returns NO tombstones. That's deliberate: a missing or corrupt
    /// record must only ever cost us a removal, never cause one. Losing the file degrades this
    /// to the behaviour ModSync had before tombstones existed.
    /// </summary>
    private List<SyncPath> ResolveTombstones(List<SyncPath> userPaths)
    {
        var recordPath = Path.Combine(GetModDirectory(), ServedPaths.FileName);

        try
        {
            List<ServedPath> previous = [];
            if (File.Exists(recordPath))
                previous = JsonSerializer.Deserialize<List<ServedPath>>(File.ReadAllText(recordPath), ServedRecordOptions) ?? [];

            var (tombstones, record) = ServedPaths.Resolve(
                previous,
                userPaths,
                // syncPath paths are server-cwd-relative, the same form the walk uses.
                path => File.Exists(path) || Directory.Exists(path),
                DateTime.UtcNow);

            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, ServedRecordOptions));

            if (tombstones.Count > 0)
            {
                logger.LogWithColor(
                    $"Corter-ModSync: {tombstones.Count} retired syncPath(s) still advertised so clients can uninstall them: "
                    + string.Join(", ", tombstones.Select(sp => sp.name)),
                    Color.Grey);
            }

            return tombstones;
        }
        catch (Exception e)
        {
            logger.Warning(
                $"Corter-ModSync: could not process {ServedPaths.FileName} ({e.Message}). "
                + "Retired mods won't be auto-removed from clients this boot.");
            return [];
        }
    }
}
