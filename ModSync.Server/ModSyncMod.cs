using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.External;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;

// Aliases — SemanticVersioning ships its own Version/Range types that shadow System.Version.
// Aliasing here makes the ModMetadata properties below read cleanly.
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace ModSync.Server;

/// <summary>
/// Mod metadata — SPT 4 replaces the old package.json with a strongly-typed record.
///
/// `record` (vs `class`): a reference type that gets value-based equality for free,
/// plus a compiler-generated immutable-ish constructor flow. Used here purely because
/// `AbstractModMetadata` is declared as a record — we have to match its shape.
///
/// `init` accessors (vs `set`): the property can only be assigned during object
/// initialization (constructor or object initializer). After that, it's read-only.
/// This is how SPT enforces "metadata is fixed at load time."
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.corter.modsync";
    public override string Name { get; init; } = "Corter-ModSync";
    public override string Author { get; init; } = "Corter";
    public override List<string>? Contributors { get; init; } = ["Dildz (SPT 4.0 port)"];
    public override Version Version { get; init; } = new("0.12.6");

    // Semver range — "~4.0.0" means ">=4.0.0 <4.1.0" (compatible with SPT 4.0.x).
    public override Range SptVersion { get; init; } = new("~4.0.0");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, Range>? ModDependencies { get; init; }
    public override string? Url { get; init; } = "https://github.com/Dildz/ModSync-for-SPT4";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "WTFPL";
}

/// <summary>
/// Main entry point. SPT discovers this class via the [Injectable] attribute, instantiates
/// it through DI (the constructor's dependencies are resolved automatically), and calls
/// PreSptLoadAsync() during startup.
///
/// `IPreSptLoadModAsync` runs BEFORE SPT itself finishes loading — important for us
/// because we register an HTTP listener and want to be ready before clients can connect.
/// Equivalent of corter's TS `IPreSptLoadMod` hook.
///
/// `TypePriority = OnLoadOrder.PreSptModLoader + 1` orders us just after the SPT mod loader
/// has finished registering all mods. Matches the load timing of the original TS server.
///
/// Primary constructor syntax — `ModSyncMod(...)` declares the constructor parameters
/// inline with the class definition. The parameters are implicitly stored as private
/// fields you can reference from any method.
///
/// **Two-stage init.** DI gives us <c>ModSyncHttpListener</c> (an <c>[Injectable]</c>
/// in its own right) already constructed. We load config from disk, run startup
/// checks, then call <c>listener.Initialize(config)</c> to activate it. Until that
/// call, the listener's <c>CanHandle</c> returns false and it's invisible to clients.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader + 1)]
public class ModSyncMod(
    ISptLogger<ModSyncMod> logger,
    ConfigUtil configUtil,
    ModSyncHttpListener listener) : IPreSptLoadModAsync
{
    // The two built-in files the mod author must ship in the mod folder alongside
    // the server DLL. Paths are relative to the server's working directory; in
    // SPT 4 the server runs from <gameRoot>/SPT/, so the updater + plugin live
    // one directory up. Match the built-in syncpaths declared in ConfigUtil.
    private const string UpdaterPath = "../ModSync.Updater.exe";
    private const string PluginPath = "../BepInEx/plugins/Corter-ModSync/Corter-ModSync.dll";

    public async Task PreSptLoadAsync()
    {
        Config config;

        try
        {
            config = await configUtil.LoadAsync();
        }
        catch (Exception ex)
        {
            // Mirrors corter's "load failed → log + leave listener dormant" behaviour.
            // The listener stays uninitialized so CanHandle returns false and the
            // /modsync/ routes 404 cleanly rather than serving partial data.
            logger.Error($"Corter-ModSync: failed to load config — server mod is disabled.\n{ex}");
            return;
        }

        // Validate that the files we promise to serve actually exist on disk.
        // Non-fatal: log the error but keep the listener dormant so we don't pretend
        // to be working. The user gets a clear "you forgot to extract X" message.
        var allFilesPresent = true;

        if (!File.Exists(UpdaterPath))
        {
            logger.Error(
                $"Corter-ModSync: '{UpdaterPath}' not found. Make sure ALL files from the release zip are extracted into the SPT install.");
            allFilesPresent = false;
        }

        if (!File.Exists(PluginPath))
        {
            logger.Error(
                $"Corter-ModSync: '{PluginPath}' not found. Make sure ALL files from the release zip are extracted into the SPT install.");
            allFilesPresent = false;
        }

        if (!allFilesPresent)
        {
            return;
        }

        // Hand config + version to the listener. After this returns, it starts
        // accepting requests on /modsync/*. We pull the version from ModMetadata
        // so the wire response always matches the declared mod version (single
        // source of truth — change it in one place).
        var modVersion = new ModMetadata().Version.ToString();
        listener.Initialize(config, modVersion);

        // Success → green, so the "we booted cleanly" banner is easy to spot in a busy console.
        logger.Success($"Corter-ModSync: server mod loaded (v{modVersion}). Listening on /modsync/*.");
    }
}
