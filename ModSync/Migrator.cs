using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using ModSync.Utility;
using Newtonsoft.Json.Linq;
using SPT.Common.Utils;

namespace ModSync;

public class Migrator(string baseDir)
{
    /// <summary>BepInEx config section the F12 syncpath toggles live in.</summary>
    public const string ConfigSection = "Synced Paths";

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

    /// <summary>
    /// Keys already present in a section of a BepInEx .cfg, read straight from the file text.
    ///
    /// Needed because BepInEx only exposes settings that have been BOUND, and at the point we
    /// run nothing is bound yet - a value saved by a previous launch is still just a line in the
    /// file. Binding to find out would be self-defeating: an absent key returns the default we
    /// passed, which is indistinguishable from a saved value that happens to equal it.
    ///
    /// The format is plain `key = value` under a `[Section]` header, and keys are written
    /// verbatim (paths and all), so a split on the first " = " is enough.
    /// </summary>
    public static HashSet<string> ReadSavedKeys(string cfgText, string section)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var inSection = false;

        foreach (var raw in cfgText.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = line == $"[{section}]";
                continue;
            }

            if (!inSection || line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var split = line.IndexOf(" = ", StringComparison.Ordinal);
            if (split > 0)
                keys.Add(line.Substring(0, split));
        }

        return keys;
    }

    /// <summary>
    /// Brings a player's saved F12 choices across when the key a toggle is stored under changes.
    ///
    /// Toggles used to be keyed by the syncpath's NAME, which is server config the admin can edit
    /// at any time. Renaming an entry therefore silently reset everyone's choice for that mod, and
    /// left the old line behind as an orphan. They are keyed by PATH now, which is the identity of
    /// the thing being synced and doesn't change when a label does.
    ///
    /// This runs once per client, before anything is bound: for each syncpath with no saved value
    /// under its path but one under its name, the value is carried across and the stale line is
    /// removed. After that a rename is invisible to the player - which is the whole point, because
    /// with `optional` a reset is no longer harmless: it would re-tick a mod the player had
    /// deliberately opted out of, and reinstall it.
    ///
    /// Returns path -> carried value, for the caller to use as that toggle's bind default.
    /// </summary>
    public static Dictionary<string, bool> CarryToggleValues(
        ConfigFile config,
        List<SyncPath> syncPaths,
        Action<string> report)
    {
        var carried = new Dictionary<string, bool>(StringComparer.Ordinal);

        if (!File.Exists(config.ConfigFilePath))
            return carried;

        HashSet<string> saved;
        try
        {
            saved = ReadSavedKeys(File.ReadAllText(config.ConfigFilePath), ConfigSection);
        }
        catch (Exception e)
        {
            // Cosmetic continuity, never worth failing a launch over: without it the player just
            // gets the seeded defaults, which is exactly the old behaviour.
            Plugin.Logger.LogWarning($"ModSync: could not read saved F12 settings ({e.Message}). Toggles will use their defaults.");
            return carried;
        }

        foreach (var syncPath in syncPaths)
        {
            var pathKey = syncPath.path.Replace("\\", "/");
            var nameKey = syncPath.name.Replace("\\", "/");

            if (pathKey == nameKey || saved.Contains(pathKey) || !saved.Contains(nameKey))
                continue;

            var old = new ConfigDefinition(ConfigSection, nameKey);
            var oldEntry = config.Bind(old, false);
            carried[syncPath.path] = oldEntry.Value;
            config.Remove(old);

            report($"ModSync: '{nameKey}' is now stored as '{pathKey}' - your setting ({oldEntry.Value}) was kept.");
        }

        return carried;
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
        // upgrades like 0.10 -> 0.12) previously left Version.txt stale forever - so the plugin
        // looked "older than the server" on every boot and re-warned. Writing it unconditionally
        // here self-heals all of those. (The 0.0.0 / missing-data paths above return early after
        // Cleanup already wrote it, so they don't reach this line.)
        File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
    }
}
