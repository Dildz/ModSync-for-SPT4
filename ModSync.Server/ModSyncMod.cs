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
    public override Version Version { get; init; } = new("0.11.1");

    // Semver range — "~4.0.0" means ">=4.0.0 <4.1.0" (compatible with SPT 4.0.x).
    public override Range SptVersion { get; init; } = new("~4.0.0");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, Range>? ModDependencies { get; init; }
    public override string? Url { get; init; } = "https://github.com/Dildz/ModSync-for-SPT4.0";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "WTFPL";
}

/// <summary>
/// Main entry point. SPT discovers this class via the [Injectable] attribute, instantiates
/// it through DI (the constructor's dependencies are resolved automatically), and calls
/// PreSptLoadAsync() during startup.
///
/// `IPreSptLoadModAsync` runs BEFORE SPT itself finishes loading — important for us
/// because we register an HTTP listener (in later phases) and want to be ready before
/// clients can connect. Equivalent of corter's TS `IPreSptLoadMod` hook.
///
/// `TypePriority = OnLoadOrder.PreSptModLoader + 1` orders us just after the SPT mod loader
/// has finished registering all mods. Matches the load timing of the original TS server.
///
/// Primary constructor syntax — `ModSyncMod(ISptLogger&lt;ModSyncMod&gt; logger)` declares
/// the constructor parameters inline with the class definition. The parameters are
/// implicitly stored as private fields you can reference from any method.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader + 1)]
public class ModSyncMod(ISptLogger<ModSyncMod> logger) : IPreSptLoadModAsync
{
    public Task PreSptLoadAsync()
    {
        // Skeleton for phase 3a — real logic (config loading, HTTP listener registration,
        // validation that ModSync.Updater.exe and Corter-ModSync.dll exist on disk) lands
        // in phases 3b-3d. For now we just want to confirm the mod loads.
        logger.Info("Corter-ModSync: server mod loaded (phase 3a skeleton).");
        return Task.CompletedTask;
    }
}
