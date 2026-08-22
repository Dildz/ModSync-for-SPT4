using System;
using System.Collections.Generic;

namespace ModSync.Utility;

/// <summary>
/// ModSync's own three components. These are the syncpaths the server prepends to every config
/// (so the mod can't be accidentally desynced), and the set the client updates on its own before
/// touching anything else when its version doesn't match the server's.
///
/// Defined ONCE here because both sides need them and they must never drift: the server compiles
/// them into its builtin syncpaths, the client matches incoming wire paths against them. The
/// patcher was already renamed once (Corter-ModSync.Patcher.dll → Corter-ModSync-Prepatch.dll in
/// v0.12.4); a second rename with two copies of these strings would silently break self-update,
/// and the symptom - an outdated client quietly making decisions about a newer config - is
/// exactly the failure this set exists to prevent.
///
/// Paths are SERVER-cwd-relative (the server runs from &lt;gameRoot&gt;/SPT/). The client sees
/// them in WIRE form, game-root-relative with backslashes - use <see cref="ToWire"/>.
/// </summary>
public static class Builtins
{
    public const string UpdaterPath = "../ModSync.Updater.exe";
    public const string PatcherPath = "../BepInEx/patchers/Corter-ModSync-Prepatch.dll";
    public const string PluginPath = "../BepInEx/plugins/Corter-ModSync";

    public static readonly string[] All = [UpdaterPath, PatcherPath, PluginPath];

    /// <summary>
    /// Server-cwd-relative → wire form: drop the leading <c>../</c> and use backslashes, the
    /// same translation ModSyncHttpListener applies before sending paths to a client.
    /// </summary>
    public static string ToWire(string serverPath) =>
        serverPath.TrimStart('.', '/').Replace('/', '\\');

    /// <summary>
    /// True if a wire-form path is one of ModSync's own components. Separator- and
    /// case-insensitive, since the client receives backslashes and Windows paths don't care.
    /// </summary>
    public static bool IsBuiltinWirePath(string wirePath)
    {
        var normalized = wirePath.Replace('/', '\\');

        foreach (var builtin in All)
        {
            if (string.Equals(ToWire(builtin), normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
