using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Mono.Cecil;
using Newtonsoft.Json;

namespace ModSync.Patcher;

/// <summary>
/// BepInEx preloader patcher — runs before any plugin DLLs are loaded into memory.
///
/// Applies ModSync_Data/PendingUpdates to the game directory while no files are locked,
/// solving the "File has a user-mapped section" IOException that occurs when the plugin
/// tries to overwrite loaded DLLs in-process after downloading updates.
///
/// Uses BasePatcherPlugin (BepInEx 5.4.21+ plugin-style API). Patch() is a no-op —
/// we only use Finalizer() which runs after all assembly patching is complete.
/// </summary>
[PatcherPluginInfo("com.corter.modsync.patcher", "ModSync Patcher", "0.12.3")]
public class ModSyncPatcher : BasePatcherPlugin
{
    private static readonly string PendingUpdatesDir =
        Path.Combine(Directory.GetCurrentDirectory(), "ModSync_Data", "PendingUpdates");
    private static readonly string RemovedFilesPath =
        Path.Combine(Directory.GetCurrentDirectory(), "ModSync_Data", "RemovedFiles.json");

    // Nominally targets Assembly-CSharp so BepInEx registers us and calls Finalizer().
    public override IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    // No-op — we only need the Finalizer() hook, not to patch any assemblies.
    public override void Patch(AssemblyDefinition assembly) { }

    public override void Finalizer()
    {
        ApplyPending();
        RemoveDeleted();
    }

    // True if the game-root-relative path is inside EscapeFromTarkov_Data/Managed/.
    private static bool IsInManagedFolder(string relPath) =>
        relPath.Replace('\\', '/').StartsWith("EscapeFromTarkov_Data/Managed/", StringComparison.OrdinalIgnoreCase);

    private void ApplyPending()
    {
        if (!Directory.Exists(PendingUpdatesDir))
            return;

        var gameDir = Directory.GetCurrentDirectory();
        var applied = 0;
        var skipped = 0;

        foreach (var src in Directory.EnumerateFiles(PendingUpdatesDir, "*", SearchOption.AllDirectories))
        {
            var rel = src.Substring(PendingUpdatesDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dest = Path.Combine(gameDir, rel);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));

                if (IsInManagedFolder(rel) && File.Exists(dest))
                {
                    File.Copy(dest, dest + ".modsync-bak", overwrite: true);
                    Logger.LogInfo($"Backed up {rel}");
                }

                File.Copy(src, dest, overwrite: true);
                Logger.LogInfo($"Applied {rel}");
                applied++;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Skipped {rel}: {e.Message}");
                skipped++;
            }
        }

        if (skipped == 0)
        {
            Directory.Delete(PendingUpdatesDir, recursive: true);
            Logger.LogInfo($"All {applied} pending update(s) applied.");
        }
        else
        {
            Logger.LogWarning($"Applied {applied}, skipped {skipped} — PendingUpdates kept for next boot.");
        }
    }

    private void RemoveDeleted()
    {
        if (!File.Exists(RemovedFilesPath))
            return;

        var gameDir = Directory.GetCurrentDirectory();
        List<string> toRemove;

        try
        {
            toRemove = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(RemovedFilesPath));
        }
        catch (Exception e)
        {
            Logger.LogError($"Could not read RemovedFiles.json: {e.Message}");
            return;
        }

        foreach (var rel in toRemove)
        {
            var fullPath = Path.Combine(gameDir, rel);
            if (!File.Exists(fullPath))
                continue;

            var bakPath = fullPath + ".modsync-bak";
            if (IsInManagedFolder(rel) && File.Exists(bakPath))
            {
                File.Copy(bakPath, fullPath, overwrite: true);
                File.Delete(bakPath);
                Logger.LogInfo($"Restored {rel}");
            }
            else
            {
                File.Delete(fullPath);
                Logger.LogInfo($"Removed {rel}");
            }
        }

        File.Delete(RemovedFilesPath);
    }
}
