using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
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
/// TargetDLLs() nominally targets Assembly-CSharp so BepInEx registers this plugin
/// and calls Finish(). Patch() is a no-op — we only need the Finish() hook.
/// </summary>
public static class Patcher
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("ModSync.Patcher");

    private static readonly string PendingUpdatesDir =
        Path.Combine(Directory.GetCurrentDirectory(), "ModSync_Data", "PendingUpdates");
    private static readonly string RemovedFilesPath =
        Path.Combine(Directory.GetCurrentDirectory(), "ModSync_Data", "RemovedFiles.json");

    // BepInEx only registers a patcher (and calls Finish) if TargetDLLs is non-empty.
    // We target Assembly-CSharp so BepInEx loads us; Patch() is a no-op.
    public static IEnumerable<string> TargetDLLs() => new[] { "Assembly-CSharp.dll" };

    public static void Patch(AssemblyDefinition assembly) { }

    public static void Finish()
    {
        ApplyPending();
        RemoveDeleted();
    }

    // True if the game-root-relative path is inside EscapeFromTarkov_Data/Managed/.
    private static bool IsInManagedFolder(string relPath) =>
        relPath.Replace('\\', '/').StartsWith("EscapeFromTarkov_Data/Managed/", StringComparison.OrdinalIgnoreCase);

    private static void ApplyPending()
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
                    Log.LogInfo($"Backed up {rel}");
                }

                File.Copy(src, dest, overwrite: true);
                Log.LogInfo($"Applied {rel}");
                applied++;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Skipped {rel}: {e.Message}");
                skipped++;
            }
        }

        if (skipped == 0)
        {
            Directory.Delete(PendingUpdatesDir, recursive: true);
            Log.LogInfo($"All {applied} pending update(s) applied.");
        }
        else
        {
            Log.LogWarning($"Applied {applied}, skipped {skipped} — PendingUpdates kept for next boot.");
        }
    }

    private static void RemoveDeleted()
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
            Log.LogError($"Could not read RemovedFiles.json: {e.Message}");
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
                Log.LogInfo($"Restored {rel}");
            }
            else
            {
                File.Delete(fullPath);
                Log.LogInfo($"Removed {rel}");
            }
        }

        File.Delete(RemovedFilesPath);
    }
}
