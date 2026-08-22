using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

// Aliases - SemanticVersioning ships its own Version/Range types that shadow System.Version.
// Aliasing here makes the ModMetadata properties below read cleanly.
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace ModSync.Server;

/// <summary>
/// Mod metadata - SPT 4 replaces the old package.json with a strongly-typed contract.
///
/// SPT 4.1 changed this from an abstract record (`AbstractModMetadata`) to an interface
/// (`IModMetadata`). Practically that means two things: the properties no longer use
/// `override` (there's no base implementation to override - an interface only declares
/// the shape), and `IsBundleMod` is gone. SPT now decides that for itself by looking for
/// a bundles.json in the mod folder.
///
/// `record` (vs `class`): a reference type that gets value-based equality for free.
/// Kept here because the metadata is a plain immutable data carrier.
///
/// `init` accessors (vs `set`): the property can only be assigned during object
/// initialization (constructor or object initializer). After that, it's read-only.
/// This is how SPT enforces "metadata is fixed at load time."
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.corter.modsync";
    public string Name { get; init; } = "Corter-ModSync";
    public string Author { get; init; } = "Corter";
    public List<string>? Contributors { get; init; } = ["Dildz (SPT 4.x port)"];
    public Version Version { get; init; } = new("0.13.0");

    // Semver range - "~4.1.0" means ">=4.1.0 <4.2.0" (compatible with SPT 4.1.x).
    public Range SptVersion { get; init; } = new("~4.1.0");

    // New in 4.1. Set true only if the mod ships enum prepatch definitions in
    // user/patchers/{ModGuid}. Our BepInEx prepatcher is a client-side thing and
    // has nothing to do with this, so it stays false.
    public bool HasPrepatcher { get; init; } = false;

    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/Dildz/ModSync-for-SPT4";
    public string License { get; init; } = "WTFPL";
}

/// <summary>
/// Main entry point. SPT discovers this class via the [Injectable] attribute, instantiates
/// it through DI (the constructor's dependencies are resolved automatically), and calls
/// OnLoadAsync() during startup.
///
/// SPT 4.1 deleted the dedicated `IPreSptLoadModAsync` interface, so we use the general
/// `IOnLoad` hook instead. The pre-SPT-load timing is now expressed purely through load
/// order: SPT runs every `IOnLoad` whose TypePriority sits below `OnLoadOrder.GameCallbacks`
/// in an early pass, before the rest of startup. `Preload + 1` puts us in that pass - the
/// same slot the old `PreSptModLoader + 1` occupied (both are the value 100000), so the
/// timing is unchanged. That matters here because we register an HTTP listener and want to
/// be ready before clients can connect.
///
/// Primary constructor syntax - `ModSyncMod(...)` declares the constructor parameters
/// inline with the class definition. The parameters are implicitly stored as private
/// fields you can reference from any method.
///
/// **Two-stage init.** DI gives us <c>ModSyncHttpListener</c> (an <c>[Injectable]</c>
/// in its own right) already constructed. We load config from disk, run startup
/// checks, then call <c>listener.Initialize(config)</c> to activate it. Until that
/// call, the listener's <c>CanHandle</c> returns false and it's invisible to clients.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class ModSyncMod(
    ISptLogger<ModSyncMod> logger,
    ConfigUtil configUtil,
    ModSyncHttpListener listener) : IOnLoad
{
    // The two built-in files the mod author must ship in the mod folder alongside
    // the server DLL. Paths are relative to the server's working directory; the server
    // runs from a subfolder of the game root (SPT/ on 4.0, renamed to SPT_Runtime/ on
    // 4.1), so the updater + plugin live one directory up either way and these relative
    // paths are unaffected by the rename. Match the built-in syncpaths declared in ConfigUtil.
    private const string UpdaterPath = "../ModSync.Updater.exe";
    private const string PluginPath = "../BepInEx/plugins/Corter-ModSync/Corter-ModSync.dll";

    // The CancellationToken is signalled when the server shuts down (CTRL+C). We pass it
    // to anything that accepts one so a shutdown mid-startup doesn't leave work running.
    public async Task OnLoadAsync(CancellationToken cancellationToken)
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
            logger.Error($"Corter-ModSync: failed to load config - server mod is disabled.\n{ex}");
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
        // source of truth - change it in one place).
        var modVersion = new ModMetadata().Version.ToString();
        listener.Initialize(config, modVersion);

        // Success → green, so the "we booted cleanly" banner is easy to spot in a busy console.
        logger.Success($"Corter-ModSync: server mod loaded (v{modVersion}). Listening on /modsync/*.");
    }
}
