using System;
using System.IO;

namespace ModSync.Utility;

/// <summary>
/// Appends a line to ModSync_Data/ModSync.log, the durable log the patcher and Updater already
/// write to.
///
/// BepInEx overwrites LogOutput.log on every boot, so anything logged there is gone within a
/// session or two. That is fine for routine chatter, but not for one-shot events a player only
/// asks about later ("why did my toggle change?") - by then the only copy is here.
/// </summary>
public static class ModSyncLog
{
    private static bool started;

    public static void Append(string gameDir, string source, string message)
    {
        try
        {
            var dir = Path.Combine(gameDir, "ModSync_Data");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ModSync.log");

            if (!started)
            {
                File.AppendAllText(path, $"--- [Corter-ModSync {source}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
                started = true;
            }

            File.AppendAllText(path, $"[Corter-ModSync {source}]: {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break a launch.
        }
    }
}
