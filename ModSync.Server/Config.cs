using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace ModSync.Server;

/// <summary>
/// Processed config — what the rest of the server uses. Holds the final flattened list of
/// SyncPaths (built-ins + user paths, sorted) and the compiled exclusion regexes.
///
/// `SyncPath` here is the shared model from `ModSync.Utility` (also used by the client).
/// Keeping one shape between client/server means the JSON contract is implicit, not duplicated.
/// </summary>
public class Config(List<SyncPath> syncPaths, List<string> exclusions)
{
    public readonly List<SyncPath> SyncPaths = syncPaths;
    public readonly List<string> Exclusions = exclusions;

    // Compile exclusion globs once at construction — cheaper than recompiling per file check.
    private readonly List<Regex> _globs = exclusions.ConvertAll(Glob.Create);

    /// <summary>True if the given filesystem path matches any exclusion glob.</summary>
    public bool IsExcluded(string filePath)
    {
        var normalized = PathExt.UnixPath(filePath);
        return _globs.Exists(g => g.IsMatch(normalized));
    }
}

/// <summary>
/// Raw shape of the on-disk config.jsonc. Used only at parse time, then discarded —
/// the validated/normalized output is a `Config` instance.
///
/// `syncPaths` is `List&lt;JsonElement&gt;` because corter's format allows two forms:
/// a plain string `"BepInEx/plugins"` or a full object `{ "path": "...", "enabled": false }`.
/// JsonElement lets us inspect at parse time and convert either case.
/// </summary>
public record RawConfig
{
    [JsonPropertyName("syncPaths")] public List<JsonElement> SyncPaths { get; init; } = [];
    [JsonPropertyName("exclusions")] public List<string> Exclusions { get; init; } = [];
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
    /// Default config written on first run.
    ///
    /// Path layout note (SPT 4 vs 3): the SPT 4 server runs from `&lt;gameRoot&gt;/SPT/`, so
    /// to reach the client-side BepInEx folder we use `../BepInEx/...`. SPT 3 ran from the
    /// game root directly and used plain `BepInEx/...`. This is the biggest config diff
    /// when porting from SPT 3 ModSync.
    /// </summary>
    private const string DefaultConfig = """
        {
            // SPT 4 directory layout: server runs from <gameRoot>/SPT/.
            //   "../BepInEx/..."  →  paths under the GAME root (client-side mods)
            //   "user/mods/..."   →  paths under the SERVER root (server-side mods, no prefix)
            "syncPaths": [
                "../BepInEx/plugins",
                "../BepInEx/patchers",
                "../BepInEx/config",
                {
                    "enabled": false,
                    "name": "(Optional) Server mods",
                    "path": "user/mods",
                    "restartRequired": false
                }
            ],
            "exclusions": [
                // SPT Installer
                "../BepInEx/plugins/spt",
                "../BepInEx/patchers/spt-prepatch.dll",
                // Fika (per-instance state)
                "user/mods/fika-server/types",
                "user/mods/fika-server/cache",
                "../BepInEx/plugins/Fika.Headless.dll",
                // Per-mod logs / caches / per-user state
                "../BepInEx/plugins/DanW-SPTQuestingBots/log",
                "user/mods/SPT-Realism/ProfileBackups",
                "user/mods/zzDrakiaXYZ-LiveFleaPrices/config",
                "../BepInEx/plugins/kmyuhkyuk-EFTApi/cache",
                "user/mods/ExpandedTaskText/src/**/cache.json",
                "user/mods/leaves-loot_fuckery/output",
                "user/mods/zz_guiltyman-addmissingquestweaponrequirements/log.log",
                "user/mods/zz_guiltyman-addmissingquestweaponrequirements/user/logs",
                "user/mods/acidphantasm-progressivebotsystem/logs",
                // Corter ModSync's own patcher (older versions shipped one)
                "../BepInEx/patchers/Corter-ModSync-Patcher.dll",
                // Universal opt-out — any mod can drop a `.nosync` sentinel to skip a file/dir
                "**/*.nosync",
                "**/*.nosync.txt",
                // Common dev/source-control junk under server mods
                "user/mods/**/.git",
                "user/mods/**/node_modules",
                "user/mods/**/*.js",
                "user/mods/**/*.js.map",
                // Windows "downloaded from internet" zone marker (alternate data stream)
                "**/*:Zone.Identifier"
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
    /// Apply corter's validation rules. Throws on the first violation with a clear message.
    /// Mirrors validateConfig() in config.ts so user-facing errors stay consistent.
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
    /// Convert one raw syncpath entry into a `SyncPath` instance, filling in corter's defaults
    /// for any field the user didn't specify. Bare-string form gets all defaults.
    /// </summary>
    private static SyncPath BuildSyncPath(JsonElement entry)
    {
        // Default field values from corter's TS implementation
        // (any user-supplied object property below overrides these).
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
        var builtins = new List<SyncPath>
        {
            new(
                path: "../ModSync.Updater.exe",
                name: "(Builtin) ModSync Updater",
                enabled: true, enforced: true, silent: true, restartRequired: false),
            new(
                path: "../BepInEx/plugins/Corter-ModSync.dll",
                name: "(Builtin) ModSync Plugin",
                enabled: true, enforced: true, silent: true, restartRequired: true),
        };

        var userPaths = raw.SyncPaths.ConvertAll(BuildSyncPath);

        // Concatenate, then sort by descending path length so more-specific matches take precedence.
        var allPaths = new List<SyncPath>(builtins.Count + userPaths.Count);
        allPaths.AddRange(builtins);
        allPaths.AddRange(userPaths);
        allPaths.Sort((a, b) => b.path.Length.CompareTo(a.path.Length));

        return new Config(allPaths, raw.Exclusions);
    }
}
