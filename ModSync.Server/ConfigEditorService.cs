using System.Text.Json;
using SPTarkov.DI.Annotations;

namespace ModSync.Server;

/// <summary>
/// One editable syncPath row.
///
/// `WasBareString` is the whole reason this class exists rather than reusing
/// <see cref="ModSync.Utility.SyncPath"/>. In config.jsonc an entry may be written either as a
/// bare string or as an object, and the server treats the two identically - a bare string simply
/// means "all defaults". If the editor promoted every row to object form on save, an admin who
/// touched one unrelated setting would find all nine of their tidy one-line entries rewritten
/// into nine-line blocks. So the original form is remembered, and a row is only written back as
/// an object once it actually carries a non-default option.
/// </summary>
public class SyncPathRow
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Optional { get; set; }
    public bool Enforced { get; set; }
    public bool Silent { get; set; }
    public bool RestartRequired { get; set; } = true;
    public bool Headless { get; set; } = true;
    public List<string> BaseFiles { get; set; } = [];

    /// <summary>How this entry was written in the file we read.</summary>
    public bool WasBareString { get; init; }

    /// <summary>
    /// True when every option still holds its default, so the entry can be written back as a bare
    /// string. Mirrors the defaults in <c>ConfigUtil.BuildSyncPath</c> - if those ever change, this
    /// has to change with them, which is what <c>ConfigEditorServiceTests</c> pins down.
    /// </summary>
    public bool IsAllDefaults =>
        (string.IsNullOrEmpty(Name) || Name == Path)
        && Enabled
        && !Optional
        && !Enforced
        && !Silent
        && RestartRequired
        && Headless
        && BaseFiles.Count == 0;
}

/// <summary>An editable view of the whole config file.</summary>
public class ConfigDraft
{
    public List<SyncPathRow> SyncPaths { get; set; } = [];
    public List<string> Exclusions { get; set; } = [];
    public List<string> HeadlessIncludes { get; set; } = [];
    public List<string> ManagedIncludes { get; set; } = [];
    public List<string> HeadlessManagedIncludes { get; set; } = [];
}

/// <summary>
/// A problem worth showing the admin before they save. Severity is advisory vs blocking: the server
/// itself tolerates every case here, so nothing is refused outright - the point is to say what the
/// setting will actually do, at the moment it is being set.
/// </summary>
public record ConfigWarning(string Path, string Message, bool Blocking);

/// <summary>
/// Reads config.jsonc into an editable draft, and checks a draft for the mistakes that are easy to
/// make and expensive to discover on a live server.
///
/// **This deliberately re-reads the file rather than using the loaded <see cref="Config"/>.** By the
/// time ConfigUtil is done with a config it has prepended two hardcoded enforced syncPaths (the
/// Updater and ModSync's own plugin folder) and merged in any tombstones - none of which live in the
/// admin's file. Editing that resolved view would write ModSync's internals into their config.
///
/// Not a singleton: the file on disk is the source of truth and the admin may have hand-edited it
/// between page loads, so each visit gets a fresh read.
/// </summary>
[Injectable(InjectionType.Scoped)]
public class ConfigEditorService
{
    // The three catch-alls. Marking one of these `optional` gives players a checkbox that uninstalls
    // the mods they need in order to connect at all, which is a support ticket rather than a choice.
    private static readonly string[] CatchAlls =
    [
        "../BepInEx/plugins",
        "../BepInEx/patchers",
        "../BepInEx/config",
    ];

    private static readonly JsonSerializerOptions JsoncOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ConfigPath => ConfigUtil.ConfigFilePath;

    /// <summary>Read config.jsonc from disk into an editable draft.</summary>
    public static async Task<ConfigDraft> LoadDraftAsync(CancellationToken cancellationToken = default) =>
        ParseDraft(await File.ReadAllTextAsync(ConfigPath, cancellationToken));

    /// <summary>
    /// Parse config text into an editable draft. Pure, so the parsing rules can be tested without a
    /// config file on disk - the same split ServedPaths uses, where Resolve is pure and ConfigUtil
    /// does the I/O around it.
    /// </summary>
    public static ConfigDraft ParseDraft(string jsonc)
    {
        var raw = JsonSerializer.Deserialize<RawConfig>(jsonc, JsoncOptions)
            ?? throw new InvalidOperationException("Corter-ModSync: config.jsonc parsed to null (file is empty or malformed).");

        return new ConfigDraft
        {
            SyncPaths = raw.SyncPaths.ConvertAll(ToRow),
            Exclusions = [.. raw.Exclusions],
            HeadlessIncludes = [.. raw.HeadlessIncludes],
            ManagedIncludes = [.. raw.ManagedIncludes],
            HeadlessManagedIncludes = [.. raw.HeadlessManagedIncludes],
        };
    }

    private static SyncPathRow ToRow(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            var bare = entry.GetString() ?? string.Empty;
            return new SyncPathRow { Path = bare, Name = bare, WasBareString = true };
        }

        var path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;

        return new SyncPathRow
        {
            Path = path,
            Name = Str(entry, "name") ?? path,
            Enabled = Bool(entry, "enabled") ?? true,
            Optional = Bool(entry, "optional") ?? false,
            Enforced = Bool(entry, "enforced") ?? false,
            Silent = Bool(entry, "silent") ?? false,
            RestartRequired = Bool(entry, "restartRequired") ?? true,
            Headless = Bool(entry, "headless") ?? true,
            BaseFiles = entry.TryGetProperty("baseFiles", out var bf) && bf.ValueKind == JsonValueKind.Array
                ? [.. bf.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [],
            WasBareString = false,
        };

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static bool? Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;
    }

    /// <summary>
    /// Everything worth telling the admin about a draft. Ordered by the row it applies to so the UI
    /// can show each one against its own entry rather than in a list at the bottom.
    /// </summary>
    public static IReadOnlyList<ConfigWarning> Validate(ConfigDraft draft)
    {
        var warnings = new List<ConfigWarning>();

        foreach (var row in draft.SyncPaths)
        {
            if (string.IsNullOrWhiteSpace(row.Path))
            {
                warnings.Add(new ConfigWarning(row.Path, "This entry has no path, so it will not sync anything.", true));
                continue;
            }

            // Same advisory ConfigUtil logs at startup, surfaced where the choice is being made.
            if (row.Optional && row.Enforced)
                warnings.Add(new ConfigWarning(row.Path,
                    "'optional' and 'enforced' contradict each other. Enforced wins: the path appears in the "
                    + "player's F12 menu as read-only, with no checkbox.", false));

            if (row.Optional && CatchAlls.Contains(row.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
                warnings.Add(new ConfigWarning(row.Path,
                    "This is a catch-all path. Marking it optional gives every player a checkbox that "
                    + "uninstalls their whole modset, including the mods they need in order to connect.", true));

            if (row.BaseFiles.Count > 0 && !row.Enabled && !row.Optional)
                warnings.Add(new ConfigWarning(row.Path,
                    "This path declares baseFiles but is opt-in and has no menu toggle, so those files "
                    + "will never reach a client.", false));
        }

        var duplicates = draft.SyncPaths
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .GroupBy(r => r.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
            warnings.Add(new ConfigWarning(group.Key,
                $"'{group.Key}' is listed {group.Count()} times. Only one of them will take effect.", false));

        return warnings;
    }
}
