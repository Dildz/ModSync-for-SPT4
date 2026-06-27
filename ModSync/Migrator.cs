using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModSync.Utility;
using Newtonsoft.Json.Linq;
using SPT.Common.Utils;

namespace ModSync;

public class Migrator(string baseDir)
{
    private string MODSYNC_DIR => Path.Combine(baseDir, "ModSync_Data");
    private string VERSION_PATH => Path.Combine(MODSYNC_DIR, "Version.txt");
    private string PREVIOUS_SYNC_PATH => Path.Combine(MODSYNC_DIR, "PreviousSync.json");
    private string MODSYNC_PATH => Path.Combine(baseDir, ".modsync");

    private List<string> CLEANUP_FILES => [MODSYNC_PATH, Path.Combine(baseDir, @"BepInEx\patchers\Corter-ModSync-Patcher.dll")];

    private Version DetectPreviousVersion()
    {
        try
        {
            if (Directory.Exists(MODSYNC_DIR) && File.Exists(VERSION_PATH))
                return Version.Parse(File.ReadAllText(VERSION_PATH));

            if (File.Exists(MODSYNC_PATH))
            {
                var persist = JObject.Parse(File.ReadAllText(MODSYNC_PATH));
                if (persist.ContainsKey("version") && persist["version"] != null)
                {
                    return persist["version"].Value<int>() switch
                    {
                        7 => Version.Parse("0.7.0"),
                        _ => Version.Parse("0.0.0")
                    };
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Failed to identify previous version. Cleaning up and attempting to continue.");
            Plugin.Logger.LogWarning(e);
        }

        return Version.Parse("0.0.0");
    }

    private void Cleanup(Version pluginVersion)
    {
        if (Directory.Exists(MODSYNC_DIR))
            Directory.Delete(MODSYNC_DIR, true);

        foreach (var file in CLEANUP_FILES.Where(File.Exists))
            File.Delete(file);

        Directory.CreateDirectory(MODSYNC_DIR);
        File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
    }

    public void TryMigrate(Version pluginVersion, List<SyncPath> syncPaths)
    {
        var oldVersion = DetectPreviousVersion();

        if (oldVersion == Version.Parse("0.0.0"))
        {
            Cleanup(pluginVersion);
            return;
        }

        if (oldVersion < Version.Parse("0.8.0"))
        {
            var persist = JObject.Parse(File.ReadAllText(MODSYNC_PATH));

            if (!persist.ContainsKey("previousSync") || persist["previousSync"] == null)
            {
                Cleanup(pluginVersion);
                return;
            }

            var oldPreviousSync = (JObject)persist["previousSync"];
            var newPreviousSync = new JObject();

            foreach (var syncPath in syncPaths)
                newPreviousSync.Add(syncPath.path, new JObject());

            foreach (var property in oldPreviousSync.Properties())
            {
                var syncPath = syncPaths.Find(s => property.Name.StartsWith($"{s.path}\\"));
                if (syncPath == null)
                {
                    Plugin.Logger.LogWarning($"Could not migrate previous sync of '{property.Name}'. Does not match any current sync paths.");
                    continue;
                }

                var modFile = (JObject)property.Value;
                if (!modFile.ContainsKey("crc"))
                {
                    Plugin.Logger.LogWarning($"Could not migrate previous sync of '{property.Name}'. Does not contain crc.");
                    continue;
                }

                (newPreviousSync.Property(syncPath.path)!.Value as JObject)!.Add(property.Name, new JObject() { ["crc"] = modFile["crc"]!.Value<uint>(), });
            }

            if (!Directory.Exists(MODSYNC_DIR))
                Directory.CreateDirectory(MODSYNC_DIR);

            File.WriteAllText(PREVIOUS_SYNC_PATH, Json.Serialize(newPreviousSync));
            File.WriteAllText(VERSION_PATH, pluginVersion.ToString());

            foreach (var file in CLEANUP_FILES.Where(File.Exists))
                File.Delete(file);
        }

        if (oldVersion < Version.Parse("0.9.0"))
        {
            var previousSync = JObject.Parse(File.ReadAllText(PREVIOUS_SYNC_PATH));

            foreach (var property in previousSync.Properties())
            {
                foreach (var file in (property.Value as JObject)!.Properties())
                {
                    var fileObject = (file.Value as JObject)!;

                    fileObject.Property("nosync")?.Remove();
                    fileObject.Property("crc")?.Remove();
                    fileObject.Add("hash", "");
                    fileObject.Add("directory", false);
                }
            }

            File.WriteAllText(PREVIOUS_SYNC_PATH, Json.Serialize(previousSync));
            File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
        }
        else if (oldVersion.Minor == pluginVersion.Minor && oldVersion != pluginVersion)
        {
            Plugin.Logger.LogWarning("Previous sync was made with a different version of the plugin. This may cause issues. Continuing...");
        }

        // Record the version we just finished migrating to. The pre-0.9.0 branches above each
        // write this themselves, but every >=0.9.0 path (same-minor patch bumps AND cross-minor
        // upgrades like 0.10 -> 0.12) previously left Version.txt stale forever — so the plugin
        // looked "older than the server" on every boot and re-warned. Writing it unconditionally
        // here self-heals all of those. (The 0.0.0 / missing-data paths above return early after
        // Cleanup already wrote it, so they don't reach this line.)
        File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
    }
}
