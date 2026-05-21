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
///   • Three compiled glob lists, one per array in config.jsonc:
///       - GlobalExclusions: universal denylist (applies to both players + headless)
///       - ClientExclusions: player-only denylist
///       - HeadlessIncludes: headless ALLOWLIST, scoped to BepInEx/plugins
///
/// `SyncPath` is the shared model from `ModSync.Utility` (also used by the client).
/// Keeping one shape between client/server means the JSON contract is implicit, not duplicated.
/// </summary>
public class Config(
    List<SyncPath> syncPaths,
    List<string> globalExclusions,
    List<string> clientExclusions,
    List<string> headlessIncludes)
{
    /// <summary>
    /// Wire-format path of the BepInEx plugins folder. The HeadlessIncludes allowlist
    /// only filters files whose path is inside this folder — everything else (patchers,
    /// config) flows through to headless unfiltered (besides GlobalExclusions, which
    /// always applies).
    /// </summary>
    public const string PluginsFolder = "../BepInEx/plugins";

    public readonly List<SyncPath> SyncPaths = syncPaths;
    public readonly List<string> GlobalExclusions = globalExclusions;
    public readonly List<string> ClientExclusions = clientExclusions;
    public readonly List<string> HeadlessIncludes = headlessIncludes;

    // Compile globs once at construction — cheaper than recompiling per file check.
    // `readonly` here means the reference can't change, but the contents (regex internal
    // state) are still mutable. C#'s equivalent of TypeScript's `readonly` array.
    private readonly List<Regex> _globalGlobs = globalExclusions.ConvertAll(Glob.Create);
    private readonly List<Regex> _clientGlobs = clientExclusions.ConvertAll(Glob.Create);
    private readonly List<Regex> _headlessIncludeGlobs = headlessIncludes.ConvertAll(Glob.Create);

    /// <summary>True if filePath matches any GlobalExclusions glob. Always applies.</summary>
    public bool IsGloballyExcluded(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _globalGlobs.Exists(g => g.IsMatch(normalized));
    }

    /// <summary>
    /// True if filePath matches any ClientExclusions glob. Only consulted on player
    /// (non-headless) syncs — headless clients ignore this list entirely.
    /// </summary>
    public bool IsClientExcluded(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _clientGlobs.Exists(g => g.IsMatch(normalized));
    }

    /// <summary>
    /// True if filePath lives inside the BepInEx/plugins folder. SyncUtil uses this
    /// to decide whether the HeadlessIncludes allowlist applies at all — files in
    /// patchers/ and config/ skip the include check and go straight to GlobalExclusions.
    /// </summary>
    public static bool IsInPluginsFolder(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return normalized == PluginsFolder
            || normalized.StartsWith(PluginsFolder + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// True if filePath is allowed by HeadlessIncludes — either matches an entry
    /// exactly (file or glob), OR is a descendant of an included directory.
    ///
    /// The directory-descendant check appends "/" to the include before comparing,
    /// so an include of `.../SAIN` matches `.../SAIN/SAIN.dll` but NOT `.../SAINFoo.dll`.
    /// </summary>
    public bool MatchesHeadlessInclude(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);

        // Direct match (covers exact file entries AND any glob patterns in the includes list)
        if (_headlessIncludeGlobs.Exists(g => g.IsMatch(normalized)))
            return true;

        // Folder-include: file is somewhere under an included directory
        foreach (var inc in HeadlessIncludes)
        {
            var normalizedInc = PathExt.UnixPath(inc);
            if (normalized.StartsWith(normalizedInc + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
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
    [JsonPropertyName("globalExclusions")] public List<string> GlobalExclusions { get; init; } = [];
    [JsonPropertyName("clientExclusions")] public List<string> ClientExclusions { get; init; } = [];
    [JsonPropertyName("headlessIncludes")] public List<string> HeadlessIncludes { get; init; } = [];
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
    /// Default config written to disk on first run. Mirrors the workspace's
    /// `config-preview.jsonc` verbatim — keep them in sync if either changes.
    ///
    /// Schema (4 top-level keys):
    ///   • syncPaths        — folders to walk. Just the 3 BepInEx folders; user/mods
    ///                        is NOT a syncpath (server mods stay server-only).
    ///   • globalExclusions — universal denylist (SPT internals + .nosync + .git).
    ///   • clientExclusions — player-only denylist. Only Fika.Headless.dll is hardcoded;
    ///                        admins add more here ONLY to reduce player-side bloat.
    ///   • headlessIncludes — headless ALLOWLIST for BepInEx/plugins only. Patchers
    ///                        and config flow through to headless via globalExclusions only.
    ///
    /// SPT 4 path note: server runs from `&lt;gameRoot&gt;/SPT/`, so `../BepInEx/...`
    /// reaches the client-side BepInEx folder. SPT 3 used plain `BepInEx/...`.
    /// </summary>
    private const string DefaultConfig = """
        {
            // SPT 4 directory layout: server runs from <gameRoot>/SPT/.
            //   "<gameRoot>/BepInEx/..."  →  client-side mods (../BepInEx/...)
            //
            // The `user/mods` folder is NOT a syncPath. Server mods stay server-only
            // by design — their client-facing components ship as separate BepInEx
            // plugins, which DO sync via the paths below.
            "syncPaths": [
                "../BepInEx/plugins",
                "../BepInEx/patchers",
                "../BepInEx/config"
            ],

            // ─── UNIVERSAL ────────────────────────────────────────────────────────────

            // Skipped for EVERY syncing client (players + headless).
            // Use for SPT internals + universal opt-out patterns. See CONFIG.md.
            "globalExclusions": [
                // SPT Installer files — not "mods", they come with the SPT install
                "../BepInEx/plugins/spt",
                "../BepInEx/patchers/spt-prepatch.dll",

                // Universal per-file opt-out — drop a `.nosync` or `.nosync.txt` file
                // next to any mod folder/file to skip it
                "**/*.nosync",
                "**/*.nosync.txt",

                // Git repo metadata — some mods are GitHub-only and may contain files
                // that are not needed.
                "**/.git"
            ],

            // ─── PLAYER side (denylist) ──────────────────────────────────────────────

            // Skipped ONLY when a player (non-headless) client syncs.
            // Use for HEADLESS-ONLY mods that regular players shouldn't receive.
            // Entries here are harmless if the named mod isn't installed.
            "clientExclusions": [
                // Fika's own headless DLL — must never reach regular players
                "../BepInEx/plugins/Fika/Fika.Headless.dll"
                // There is no harm in syncing ALL BepInEx (client) mods to all players,
                // even if you intend to use only the headless to host raids.
                // If you'd prefer to reduce client bloat, add mod paths here that
                // shouldn't be synced to player clients — basically a mirror of the
                // headlessIncludes allowlist below.
                // "../BepInEx/plugins/SAIN"                    // — bot AI
                // "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll"  // — bot AI
                // "../BepInEx/plugins/DrakiaXYZ-Waypoints"     // — bot pathfinding
                // all other client (player) exclusion mod paths...
            ],

            // ─── HEADLESS side (allowlist) ───────────────────────────────────────────

            // ALLOWLIST: BepInEx/plugins paths that a Fika headless client needs.
            // Headless is a specialized instance — it only gets what's listed here,
            // not the full plugins/ tree. This avoids shipping UI/HUD/visual mods
            // that headless doesn't need (no human is watching its screen).
            //
            // SCOPE: this allowlist applies to `../BepInEx/plugins` ONLY.
            // `../BepInEx/patchers` and `../BepInEx/config` are NOT filtered for
            // headless — it receives the same files there as a regular player.
            //
            // See CONFIG.md for a starter list of common headless-needed mods.
            "headlessIncludes": [
                // Examples — add your own paths uncommented:
                // "../BepInEx/plugins/Fika"                    // — Fika components (core & headless DLLs)
                // "../BepInEx/plugins/SAIN"                    // — bot AI
                // "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll"  // — bot AI
                // "../BepInEx/plugins/DrakiaXYZ-Waypoints"     // — bot pathfinding
                // all other headless only mod paths...
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
    /// Apply validation rules and throw on the first violation. Cross-checks against
    /// globalExclusions only — the client/headless arrays have routing-aware semantics
    /// where partial overlap with syncpaths is deliberate (e.g. excluding one DLL inside
    /// an included plugins folder is a normal use case).
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

            if (raw.GlobalExclusions.Contains(path))
            {
                throw new InvalidOperationException(
                    $"Corter-ModSync: '{path}' is in BOTH syncPaths and globalExclusions. This isn't doing what you want — remove from one.");
            }
        }
    }

    /// <summary>
    /// Convert one raw syncpath entry into a `SyncPath` instance, filling in defaults
    /// for any field the user didn't specify. Bare-string form gets all defaults.
    /// </summary>
    private static SyncPath BuildSyncPath(JsonElement entry)
    {
        // Default field values (any user-supplied object property below overrides these).
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

        // Object form — read each optional field with the default as fallback.
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
        //
        // The plugin lives inside its own subfolder (`Corter-ModSync/`) rather than as a flat
        // DLL in `BepInEx/plugins/`. BepInEx scans recursively, so this is purely a layout
        // tidiness choice — keeps Corter-ModSync.dll + Corter-ModSync.dll.config grouped.
        // The syncpath points at the folder so both files get walked + hashed together.
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

        var userPaths = raw.SyncPaths.ConvertAll(BuildSyncPath);

        // Concatenate, then sort by descending path length so more-specific matches take precedence.
        var allPaths = new List<SyncPath>(builtins.Count + userPaths.Count);
        allPaths.AddRange(builtins);
        allPaths.AddRange(userPaths);
        allPaths.Sort((a, b) => b.path.Length.CompareTo(a.path.Length));

        return new Config(allPaths, raw.GlobalExclusions, raw.ClientExclusions, raw.HeadlessIncludes);
    }
}
