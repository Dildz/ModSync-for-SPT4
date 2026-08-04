// This file compiles into BOTH the net9 server (nullable context ON) and the net472 client
// (nullable context absent). A `List<string>?` annotation trips CS8632 on the client; a bare
// `= null` default trips CS8625 on the server. Disabling the context here satisfies both, and
// CI builds with warnings-as-errors so this has to be exactly right.
#nullable disable

using System.Collections.Generic;

namespace ModSync.Utility;

public class SyncPath(
    string path,
    string name = "",
    bool enabled = true,
    bool enforced = false,
    bool silent = false,
    bool restartRequired = true,
    bool headless = true,
    // No `?` annotation: this file compiles into the net472 client, which has no nullable
    // context, and CS8632 there would trip the warnings-as-errors CI build.
    List<string> baseFiles = null)
{
    public readonly string path = path;
    public readonly string name = string.IsNullOrEmpty(name) ? path : name;
    public readonly bool enabled = enabled;
    public readonly bool enforced = enforced;
    public readonly bool silent = silent;
    public readonly bool restartRequired = restartRequired;

    /// <summary>
    /// False = never send this path (or its <see cref="baseFiles"/>) to a Fika headless client.
    /// Needed because a headless has no F12 menu and therefore ignores the opt-in toggles
    /// entirely — it syncs every configured path, so `enabled:false` alone can't keep a
    /// player-only mod off it. Defaults true (send to everyone), matching previous behaviour.
    /// </summary>
    public readonly bool headless = headless;

    /// <summary>
    /// Files this mod owns that live OUTSIDE its own folder — e.g. the two Unity assemblies
    /// DynamicMaps drops into EscapeFromTarkov_Data/Managed, or the nvngx_dlss.dll that Tarkov
    /// DLSS 4.5 swaps in. Declaring them here binds them to this syncpath's opt-in state, so
    /// they're served only when the mod is active and are never pushed to a player who didn't
    /// ask for the mod.
    ///
    /// A baseFile may either REPLACE a base-game file or simply ADD one the game doesn't ship
    /// (DynamicMaps' assemblies are additions — EFT has neither). Only a replacement leaves a
    /// &lt;file&gt;.modsync-bak behind, and removal keys off exactly that: a backup means restore
    /// the original, no backup means the mod added the file, so delete it like any other.
    /// </summary>
    public readonly List<string> baseFiles = baseFiles ?? [];
}
