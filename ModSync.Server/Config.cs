using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModSync.Server;

/// <summary>
/// Processed config — what the rest of the server uses. Holds:
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
    List<string> headlessManagedIncludes)
{
    public readonly List<SyncPath> SyncPaths = syncPaths;
    public readonly List<string> Exclusions = exclusions;
    public readonly List<string> HeadlessIncludes = headlessIncludes;
    public readonly List<string> ManagedIncludes = managedIncludes;
    public readonly List<string> HeadlessManagedIncludes = headlessManagedIncludes;

    // Compile globs once at construction — cheaper than recompiling per file check.
    // `readonly` here means the reference can't change, but the contents (regex internal
    // state) are still mutable. C#'s equivalent of TypeScript's `readonly` array.
    private readonly List<Regex> _exclusionGlobs = exclusions.ConvertAll(Glob.Create);

    // headlessIncludes uses plain prefix matching instead of globs — globs aren't
    // supported (by design; see config comments) so there's nothing to compile.
    private readonly List<string> _normalizedHeadlessIncludes =
        headlessIncludes.ConvertAll(e => PathExt.UnixPath(e).TrimEnd('/'));

    // managedIncludes entries are plain filenames (e.g. "Unity.VectorGraphics.dll").
    // Store as a case-insensitive set — Windows filesystems are case-insensitive and
    // we want "unity.vectorgraphics.dll" to match regardless of how the admin typed it.
    private readonly HashSet<string> _managedIncludesSet =
        new(managedIncludes, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _headlessManagedIncludesSet =
        new(headlessManagedIncludes, StringComparer.OrdinalIgnoreCase);

    /// <summary>True if filePath matches any of the configured exclusion globs.</summary>
    public bool IsExcluded(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _exclusionGlobs.Exists(g => g.IsMatch(normalized));
    }

    /// <summary>
    /// True if filePath matches any headlessIncludes entry. Only meaningful for files
    /// inside BepInEx/plugins when serving a headless client — callers gate by syncpath
    /// scope before consulting this. Empty allowlist => everything filtered out (the
    /// "you haven't configured headless yet" default).
    ///
    /// Entries can be a folder ("../BepInEx/plugins/SAIN") or an exact file
    /// ("../BepInEx/plugins/Foo.dll"). A folder entry matches the folder itself
    /// (empty-dir sentinel) and all files inside it. Exact-file entries match only
    /// that one file. Simple prefix matching — no glob support here by design.
    /// </summary>
    public bool IsHeadlessAllowed(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _normalizedHeadlessIncludes.Exists(entry =>
            normalized == entry || normalized.StartsWith(entry + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// True if the given filename appears in managedIncludes. Callers pass
    /// Path.GetFileName(filePath) — the allowlist is filename-only, not full path,
    /// so it works identically whether the server's Managed folder is a Docker
    /// staging directory (2 files) or a full Windows EFT install (169 files).
    /// </summary>
    public bool IsManagedAllowed(string fileName) => _managedIncludesSet.Contains(fileName);

    /// <summary>Same as IsManagedAllowed but for headless clients.</summary>
    public bool IsHeadlessManagedAllowed(string fileName) => _headlessManagedIncludesSet.Contains(fileName);
}

/// <summary>
/// Raw shape of the on-disk config.jsonc. Used only at parse time, then discarded —
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
/// it as a constructor parameter. Logger comes in via DI too — same pattern as ModSyncMod.
/// </summary>
[Injectable]
public class ConfigUtil(ISptLogger<ConfigUtil> logger)
{
    /// <summary>
    /// Default config written to disk on first run. Five top-level keys:
    ///   • syncPaths              — folders the server walks and offers
    ///   • exclusions             — universal denylist (every client, player and headless)
    ///   • headlessIncludes       — allowlist scoped to BepInEx/plugins for Fika headless clients
    ///   • managedIncludes        — allowlist for EscapeFromTarkov_Data/Managed DLLs (players)
    ///   • headlessManagedIncludes — same, for Fika headless
    ///
    /// Per-player opt-outs live CLIENT-SIDE in each install's
    /// <c>&lt;game&gt;/ModSync_Data/Exclusions.jsonc</c> — admins don't manage those.
    ///
    /// SPT 4 path note: server runs from <c>&lt;gameRoot&gt;/SPT/</c>, so <c>../BepInEx/...</c>
    /// reaches the client-side BepInEx folder. SPT 3 used plain <c>BepInEx/...</c>.
    /// </summary>
    private const string DefaultConfig = """
        {
            // ┌─────────────────────────────────────────────────────────────────────┐
            // │  WIKI — FULL CONFIG GUIDE:                                          │
            // │  https://github.com/Dildz/ModSync-for-SPT4.0/wiki/Configuration     │
            // │  Every option below is documented there with examples.              │
            // └─────────────────────────────────────────────────────────────────────┘

            // SPT 4 directory layout: server runs from <gameRoot>/SPT/.
            //   "<gameRoot>/BepInEx/..."  →  client-side mods (../BepInEx/...)
            //
            // The `user/mods` folder is NOT a syncPath. Server mods stay server-only
            // by design — their client-facing components ship as separate BepInEx
            // plugins, which DO sync via the paths below.
            //
            // Each path/entry can be configured as EITHER:
            //   1. A plain string  — sync this path with all defaults (what we use below).
            //   2. An object       — same path, but override HOW it syncs. A plain string
            //                        is just shorthand for an object with every option at
            //                        its default.
            //
            // The five options:
            //
            //     "path"            (required)        Folder or file to sync. Globs NOT allowed.
            //     "name"            (default: path)   Friendly label shown in the client's F12 sync menu.
            //     "enabled"         (default: true)   true = opt-out (synced unless client unchecks it),
            //                                         false = opt-in (client must check the box first).
            //     "enforced"        (default: false)  true = server wins, always. Client can't opt out,
            //                                         can't keep local edits, can't exclude it. Use for
            //                                         files every client MUST match (e.g. patchers).
            //     "restartRequired" (default: true)   Must the client restart after these files update?
            //     "silent"          (default: false)  false = show the client a prompt of changes.
            //                                         true  = apply quietly in the background on load.
            //
            // Paths are matched MOST-SPECIFIC FIRST, so you can set defaults on a folder
            // and override just one child inside it (e.g. enforce a single config file).
            "syncPaths": [
                "../BepInEx/plugins",
                "../BepInEx/patchers",
                "../BepInEx/config"

                // ── Object-form example (uncomment & adapt) ──
                // Force every client to match the server's patchers exactly — a version
                // mismatch on a patcher DLL can otherwise leave a client silently stuck:
                //
                // {
                //     "path": "../BepInEx/patchers",
                //     "name": "Preloader patchers",
                //     "enforced": true
                // }
                //
                // Opt-in extra (off unless the client ticks it), applied without a restart:
                //
                // {
                //     "path": "../BepInEx/plugins/SomeOptionalMod",
                //     "name": "Optional: Some Mod",
                //     "enabled": false,
                //     "restartRequired": false
                // }
            ],

            //-----------------------------------------------------------------------

            // Paths ignored by ModSync (players AND headless alike).
            // Use for SPT internals, per-instance state files mods don't want
            // overwritten, and the universal `.nosync` opt-out pattern.
            // See the wiki [exclusions] for guidance on what belongs here.
            "exclusions": [
                // SPT Installer files — not "mods", they come with the SPT install
                "../BepInEx/plugins/spt",
                "../BepInEx/patchers/spt-prepatch.dll",

                // Fika headless DLL — must never reach regular players. Headless
                // instances get this from their image / manual install, not via sync.
                "../BepInEx/plugins/Fika/Fika.Headless.dll",

                // Universal per-file opt-out — drop a `.nosync` or `.nosync.txt` file
                // next to any mod folder/file to skip it
                "**/*.nosync",
                "**/*.nosync.txt",

                // Git repo metadata — some mods are GitHub-only and may contain files
                // that aren't needed at runtime.
                "**/.git"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST for Fika headless clients, scoped to ../BepInEx/plugins ONLY.
            // patchers + config flow through to headless unfiltered (the universal
            // `exclusions` above still applies).
            //
            // A headless client only receives plugin paths that match an entry here.
            // Empty array (the default) means headless gets ZERO plugins — you MUST
            // populate this if you run a headless instance.
            //
            // Entries should be EXPLICIT — either a mod folder or an individual DLL.
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

                // Bot AI + behavior (example entries — add your own below)
                // "../BepInEx/plugins/SAIN",
                // "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
                // "../BepInEx/plugins/DrakiaXYZ-Waypoints"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST for EscapeFromTarkov_Data/Managed DLL files.
            //
            // Some mods ship Unity assemblies that need to be placed in
            // EscapeFromTarkov_Data/Managed/ on every player client. List the
            // DLL filenames here (filename only — no folder path).
            //
            // The server reads from ../EscapeFromTarkov_Data/Managed/ but only serves
            // the files you list here. This keeps it safe in both setups:
            //   - Docker/Linux server: that folder is a staging area with ONLY mod DLLs
            //   - Windows (host is also a player): that folder contains the full EFT
            //     install — the allowlist prevents vanilla Unity DLLs from being synced
            //
            // On install:  if the file already exists on the client, the original is
            //              backed up as <filename>.modsync-bak before being replaced.
            // On removal:  if a .modsync-bak exists, the original is restored automatically.
            //              If no backup was made (file was new), the file is deleted.
            // See the wiki [managedIncludes] for extra details.
            "managedIncludes": [
                // DynamicMaps example — remove or replace with your own mod's files:
                // "Unity.VectorGraphics.dll",
                // "Unity.InternalAPIEngineBridge.003.dll"
            ],

            //-----------------------------------------------------------------------

            // ALLOWLIST - but for Fika headless clients.
            //
            // Headless runs the game simulation without the rendering stack, so Managed
            // files are almost never needed there. Leave this empty unless a mod's install
            // instructions specifically say it is required on headless.
            // Will probably always be empty, but the option is here for future use if needed.
            "headlessManagedIncludes": [
            ]
        }
        """;

    /// <summary>
    /// JSON parsing options that accept comments and trailing commas — what makes ".jsonc" jsonc.
    /// Reused across all parse calls (System.Text.Json caches metadata when options are reused).
    /// </summary>
    private static readonly JsonSerializerOptions JsoncOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Resolve the mod's on-disk directory by inspecting where this assembly was loaded from.
    /// At runtime SPT loads us from `&lt;gameRoot&gt;/SPT/user/mods/Corter-ModSync/`, so the
    /// returned path is exactly that — where we'll read/write `config.jsonc`.
    /// </summary>
    private static string GetModDirectory()
    {
        var assemblyPath = typeof(ConfigUtil).Assembly.Location;
        return Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException("Corter-ModSync: could not resolve mod directory from assembly location");
    }

    /// <summary>
    /// Read config.jsonc from disk; write the default if it doesn't exist yet. Returns the
    /// raw parsed shape (still unvalidated).
    /// </summary>
    private async Task<RawConfig> ReadConfigFileAsync()
    {
        var configPath = Path.Combine(GetModDirectory(), "config.jsonc");

        if (!File.Exists(configPath))
        {
            logger.Info($"Corter-ModSync: no config.jsonc found at {configPath}, writing defaults.");
            await File.WriteAllTextAsync(configPath, DefaultConfig);
        }

        var text = await File.ReadAllTextAsync(configPath);
        var raw = JsonSerializer.Deserialize<RawConfig>(text, JsoncOptions)
            ?? throw new InvalidOperationException("Corter-ModSync: config.jsonc parsed to null (file is empty or malformed).");

        return raw;
    }

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
                    $"Corter-ModSync: '{path}' is in BOTH syncPaths and exclusions. This isn't doing what you want — remove from one.");
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
        const bool defaultEnforced = false;
        const bool defaultSilent = false;
        const bool defaultRestartRequired = true;

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
            enforced: entry.TryGetProperty("enforced", out var ef) && ef.ValueKind != JsonValueKind.Null
                ? ef.GetBoolean()
                : defaultEnforced,
            silent: entry.TryGetProperty("silent", out var sl) && sl.ValueKind != JsonValueKind.Null
                ? sl.GetBoolean()
                : defaultSilent,
            restartRequired: entry.TryGetProperty("restartRequired", out var rr) && rr.ValueKind != JsonValueKind.Null
                ? rr.GetBoolean()
                : defaultRestartRequired);
    }

    /// <summary>
    /// Public entry point. Reads disk, validates, builds the final Config including:
    ///   1) two prepended built-in syncpaths (ModSync's own DLL + Updater — always synced
    ///      so users can't accidentally desync the mod itself)
    ///   2) user-defined syncpaths from the file
    ///   3) sorted by path length descending — when a file matches multiple syncpaths,
    ///      the more-specific (longer) path wins.
    /// </summary>
    public async Task<Config> LoadAsync()
    {
        var raw = await ReadConfigFileAsync();
        Validate(raw);

        // Built-ins use ../ prefix for SPT 4 layout (server runs from <gameRoot>/SPT/).
        // `enforced=true` means the client always re-syncs these even if the user deleted them,
        // and `silent=true` hides them from the UI's "files about to update" prompt.
        var builtins = new List<SyncPath>
        {
            new(
                path: "../ModSync.Updater.exe",
                name: "(Builtin) ModSync Updater",
                enabled: true, enforced: true, silent: true, restartRequired: false),
            new(
                path: "../BepInEx/plugins/Corter-ModSync",
                name: "(Builtin) ModSync Plugin",
                enabled: true, enforced: true, silent: true, restartRequired: true),
        };

        // Add the Managed syncpath only when at least one list is populated.
        // Not enforced — players can opt out. restartRequired because replacing a Unity
        // assembly takes effect only after EFT restarts.
        if (raw.ManagedIncludes.Count > 0 || raw.HeadlessManagedIncludes.Count > 0)
        {
            builtins.Add(new SyncPath(
                path: "../EscapeFromTarkov_Data/Managed",
                name: "(Builtin) Managed Files",
                enabled: true, enforced: false, silent: false, restartRequired: true));
        }

        var userPaths = raw.SyncPaths.ConvertAll(BuildSyncPath);

        // Concatenate, then sort by descending path length so more-specific matches take precedence.
        var allPaths = new List<SyncPath>(builtins.Count + userPaths.Count);
        allPaths.AddRange(builtins);
        allPaths.AddRange(userPaths);
        allPaths.Sort((a, b) => b.path.Length.CompareTo(a.path.Length));

        return new Config(allPaths, raw.Exclusions, raw.HeadlessIncludes, raw.ManagedIncludes, raw.HeadlessManagedIncludes);
    }
}
