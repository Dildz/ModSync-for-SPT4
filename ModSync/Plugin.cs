using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT.UI;
using ModSync.UI;
using ModSync.Utility;
using SPT.Common.Utils;
using UnityEngine;

namespace ModSync;

using SyncPathFileList = Dictionary<string, List<string>>;
using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

[BepInPlugin("corter.modsync", "Corter ModSync", "0.12.2")]
public class Plugin : BaseUnityPlugin
{
    private static readonly string MODSYNC_DIR = Path.Combine(Directory.GetCurrentDirectory(), "ModSync_Data");
    private static readonly string PENDING_UPDATES_DIR = Path.Combine(MODSYNC_DIR, "PendingUpdates");
    private static readonly string PREVIOUS_SYNC_PATH = Path.Combine(MODSYNC_DIR, "PreviousSync.json");
    private static readonly string LOCAL_HASHES_PATH = Path.Combine(MODSYNC_DIR, "LocalHashes.json");
    private static readonly string REMOVED_FILES_PATH = Path.Combine(MODSYNC_DIR, "RemovedFiles.json");
    private static readonly string LOCAL_EXCLUSIONS_PATH = Path.Combine(MODSYNC_DIR, "Exclusions.jsonc");
    private static readonly string LEGACY_EXCLUSIONS_PATH = Path.Combine(MODSYNC_DIR, "Exclusions.json");
    private static readonly string UPDATER_PATH = Path.Combine(Directory.GetCurrentDirectory(), "ModSync.Updater.exe");

    /// <summary>
    /// Content written to a fresh <c>ModSync_Data/Exclusions.jsonc</c> on first run.
    /// Same template for player and headless installs — no hardcoded mod names. The
    /// in-file comment header explains the semantic so admins/players can hand-edit
    /// the file without consulting docs.
    ///
    /// <c>@"..."</c> is a verbatim string literal: backslashes are taken literally,
    /// and the only escape sequence is <c>""</c> for a single double-quote.
    /// </summary>
    private const string EXCLUSIONS_SEED_TEMPLATE = @"// Personal denylist — paths or globs ModSync should NOT install or keep
// installed on this machine. Applied on top of the server's exclusions.
//
// Semantics:
//   - If ModSync previously installed a listed file, it will be REMOVED
//     on next sync.
//   - If you copied the file in by hand (ModSync never installed it),
//     ModSync leaves it alone — only files ModSync installed are touched.
//   - Listed files won't be downloaded going forward.
//
// On a Fika headless install this file is usually EMPTY — the server's
// `headlessIncludes` allowlist controls what reaches headless. Use this
// file only for per-install overrides on top of the allowlist.
//
// Edits to this file are read at game startup, not live — restart EFT
// to apply.
//
// Examples (delete the empty array below and replace with your own):
//   ""BepInEx/plugins/AmandsGraphics.dll"",
//   ""BepInEx/plugins/DynamicMaps/**""
[]
";

    // Configuration
    private Dictionary<string, ConfigEntry<bool>> configSyncPathToggles;
    private ConfigEntry<bool> configDeleteRemovedFiles;

    private List<SyncPath> syncPaths = [];
    private SyncPathModFiles remoteModFiles = [];
    private SyncPathModFiles previousSync = [];
    private List<string> localExclusions = [];

    private SyncPathFileList addedFiles = [];
    private SyncPathFileList updatedFiles = [];
    private SyncPathFileList removedFiles = [];
    private SyncPathFileList createdDirectories = [];

    private List<Task> downloadTasks = [];

    private bool pluginFinished;
    private int downloadCount;
    private int totalDownloadCount;

    private Server server;
    private CancellationTokenSource cts = new();

    public static new readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("ModSync");

    private int UpdateCount =>
        EnabledSyncPaths
            .Select(syncPath =>
                addedFiles[syncPath.path].Count
                + updatedFiles[syncPath.path].Count
                + (configDeleteRemovedFiles.Value || syncPath.enforced ? removedFiles[syncPath.path].Count : 0)
                + createdDirectories[syncPath.path].Count
            )
            .Sum();
    /// <summary>
    /// True when this BepInEx instance is loaded inside a Fika headless client. The
    /// Server class also reads this to append `?headless=1` to relevant endpoints.
    /// `public` (was `private`) so Server.cs can read it; otherwise unchanged.
    /// </summary>
    public static bool IsHeadless => Chainloader.PluginInfos.ContainsKey("com.fika.headless");
    private List<SyncPath> EnabledSyncPaths => syncPaths.Where(syncPath => configSyncPathToggles[syncPath.path].Value || syncPath.enforced).ToList();

    private bool SilentMode =>
        IsHeadless
        || EnabledSyncPaths.All(syncPath =>
            syncPath.silent
            || (
                addedFiles[syncPath.path].Count == 0
                && updatedFiles[syncPath.path].Count == 0
                && (!(configDeleteRemovedFiles.Value || syncPath.enforced) || removedFiles[syncPath.path].Count == 0)
                && createdDirectories[syncPath.path].Count == 0
            )
        );

    private bool NoRestartMode =>
        EnabledSyncPaths.All(syncPath =>
            !syncPath.restartRequired
            || (
                addedFiles[syncPath.path].Count == 0
                && updatedFiles[syncPath.path].Count == 0
                && (!(configDeleteRemovedFiles.Value || syncPath.enforced) || removedFiles[syncPath.path].Count == 0)
                && createdDirectories[syncPath.path].Count == 0
            )
        );

    private void AnalyzeModFiles(SyncPathModFiles localModFiles)
    {
        Sync.CompareModFiles(
            Directory.GetCurrentDirectory(),
            EnabledSyncPaths,
            localModFiles,
            remoteModFiles,
            previousSync,
            out addedFiles,
            out updatedFiles,
            out removedFiles,
            out createdDirectories
        );

        Logger.LogInfo($"Found {UpdateCount} files to download.");
        Logger.LogInfo($"- {addedFiles.SelectMany(path => path.Value).Count()} added");
        Logger.LogInfo($"- {updatedFiles.SelectMany(path => path.Value).Count()} updated");
        if (removedFiles.Count > 0)
            Logger.LogInfo($"- {removedFiles.SelectMany(path => path.Value).Count()} removed");

        if (UpdateCount > 0)
        {
            if (SilentMode)
                Task.Run(() => SyncMods(addedFiles, updatedFiles, createdDirectories));
            else
                updateWindow.Show();
        }
        else
            WriteModSyncData();
    }

    private void SkipUpdatingMods()
    {
        var enforcedAddedFiles = EnabledSyncPaths.ToDictionary(
            syncPath => syncPath.path,
            syncPath => syncPath.enforced ? addedFiles[syncPath.path] : [],
            StringComparer.OrdinalIgnoreCase
        );

        var enforcedUpdatedFiles = EnabledSyncPaths.ToDictionary(
            syncPath => syncPath.path,
            syncPath => syncPath.enforced ? updatedFiles[syncPath.path] : [],
            StringComparer.OrdinalIgnoreCase
        );

        var enforcedCreatedDirectories = EnabledSyncPaths.ToDictionary(
            syncPath => syncPath.path,
            syncPath => syncPath.enforced ? createdDirectories[syncPath.path] : [],
            StringComparer.OrdinalIgnoreCase
        );

        if (
            enforcedAddedFiles.Values.Any(files => files.Any())
            || enforcedUpdatedFiles.Values.Any(files => files.Any())
            || enforcedCreatedDirectories.Values.Any(files => files.Any())
        )
        {
            Task.Run(() => SyncMods(enforcedAddedFiles, enforcedUpdatedFiles, enforcedCreatedDirectories));
        }
        else
        {
            pluginFinished = true;
            updateWindow.Hide();
        }
    }

    private async Task SyncMods(SyncPathFileList filesToAdd, SyncPathFileList filesToUpdate, SyncPathFileList directoriesToCreate)
    {
        updateWindow.Hide();

        if (!Directory.Exists(PENDING_UPDATES_DIR))
            Directory.CreateDirectory(PENDING_UPDATES_DIR);

        foreach (var syncPath in EnabledSyncPaths)
        {
            foreach (var dir in directoriesToCreate[syncPath.path])
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception e)
                {
                    Logger.LogError("Failed to create empty directories: " + e);
                }
            }
        }

        downloadCount = 0;
        totalDownloadCount = 0;

        // Conservative parallel-download cap. Upstream used 8; we dropped it because
        // slow uplinks + Mono's TLS stack proved fragile under heavy concurrency
        // (handshakes timing out, retry storms not recovering). 2 is plenty when the
        // shared HttpClient + connection pool keep TCP/TLS sessions warm.
        var limiter = new SemaphoreSlim(2);
        var filesToDownload = EnabledSyncPaths
            .Select((syncPath) => new KeyValuePair<string, List<string>>(syncPath.path, [.. filesToAdd[syncPath.path], .. filesToUpdate[syncPath.path]]))
            .ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value);

        Logger.LogInfo($"Starting download of {UpdateCount} files.");
        downloadTasks = EnabledSyncPaths
            .SelectMany(syncPath =>
                filesToDownload.TryGetValue(syncPath.path, out var pathFilesToDownload)
                    ? pathFilesToDownload.Select(file =>
                        server.DownloadFile(file, syncPath.restartRequired ? PENDING_UPDATES_DIR : Directory.GetCurrentDirectory(), limiter, cts.Token)
                    )
                    : []
            )
            .ToList();

        totalDownloadCount = downloadTasks.Count;

        if (!IsHeadless)
            progressWindow.Show();

        while (downloadTasks.Count > 0 && !cts.IsCancellationRequested)
        {
            var task = await Task.WhenAny(downloadTasks);

            try
            {
                await task;
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && cts.IsCancellationRequested)
                    continue;

                cts.Cancel();
                progressWindow.Hide();
                if (!IsHeadless)
                    downloadErrorWindow.Show();
            }

            downloadTasks.Remove(task);
            downloadCount++;
        }

        downloadTasks.Clear();
        progressWindow.Hide();

        Logger.LogInfo("Download of files finished.");

        if (!cts.IsCancellationRequested)
        {
            WriteModSyncData();

            if (NoRestartMode)
            {
                Directory.Delete(PENDING_UPDATES_DIR, true);
                pluginFinished = true;
            }
            else if (!IsHeadless)
                restartWindow.Show();
            else
                StartUpdaterProcess();
        }
    }

    private async Task CancelUpdatingMods()
    {
        progressWindow.Hide();
        cts.Cancel();

        await Task.WhenAll(downloadTasks);

        Directory.Delete(PENDING_UPDATES_DIR, true);
        pluginFinished = true;
    }

    private void WriteModSyncData()
    {
        VFS.WriteTextFile(PREVIOUS_SYNC_PATH, Json.Serialize(remoteModFiles));
        if (EnabledSyncPaths.Any(syncPath => (configDeleteRemovedFiles.Value || syncPath.enforced) && removedFiles[syncPath.path].Count != 0))
            VFS.WriteTextFile(REMOVED_FILES_PATH, Json.Serialize(removedFiles.SelectMany(kvp => kvp.Value).ToList()));
    }

    private void StartUpdaterProcess()
    {
        if (IsHeadless)
        {
            // On Linux/Wine, Process.Start() with UseShellExecute=false exec()s the PE
            // binary directly as a Linux process — it fails with ENOEXEC and the updater
            // never runs. Apply pending updates in-process instead, then quit so Docker's
            // restart loop brings EFT back up with the new files already in place.
            Logger.LogInfo("ModSync: headless — applying pending updates in-process.");
            try { ApplyPendingUpdatesInProcess(); }
            catch (Exception e) { Logger.LogError($"ModSync: in-process apply failed: {e}"); }
            Application.Quit();
            return;
        }

        List<string> options = [];
        Logger.LogInfo($"Starting Updater with arguments {string.Join(" ", options)} {Process.GetCurrentProcess().Id}");
        var updaterStartInfo = new ProcessStartInfo
        {
            FileName = UPDATER_PATH,
            Arguments = string.Join(" ", options) + " " + Process.GetCurrentProcess().Id,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var updaterProcess = new Process { StartInfo = updaterStartInfo };
        updaterProcess.Start();
        Application.Quit();
    }

    private void ApplyPendingUpdatesInProcess()
    {
        var gameDir = Directory.GetCurrentDirectory();

        if (Directory.Exists(PENDING_UPDATES_DIR))
        {
            foreach (var src in Directory.EnumerateFiles(PENDING_UPDATES_DIR, "*", SearchOption.AllDirectories))
            {
                var rel = src.Substring(PENDING_UPDATES_DIR.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dest = Path.Combine(gameDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(src, dest, overwrite: true);
                Logger.LogInfo($"ModSync: applied {rel}");
            }
            Directory.Delete(PENDING_UPDATES_DIR, true);
            Logger.LogInfo("ModSync: all pending updates applied.");
        }

        if (File.Exists(REMOVED_FILES_PATH))
        {
            var toRemove = Json.Deserialize<List<string>>(VFS.ReadTextFile(REMOVED_FILES_PATH));
            foreach (var rel in toRemove)
            {
                var fullPath = Path.Combine(gameDir, rel);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    Logger.LogInfo($"ModSync: removed {rel}");
                }
            }
            File.Delete(REMOVED_FILES_PATH);
        }
    }

    private IEnumerator StartPlugin()
    {
        cts = new CancellationTokenSource();
        if (Directory.Exists(PENDING_UPDATES_DIR) || File.Exists(REMOVED_FILES_PATH))
            Logger.LogWarning(
                "ModSync found previous update. Updater may have failed, check the 'ModSync_Data/Updater.log' for details. Attempting to continue."
            );

        Logger.LogDebug("Fetching server version");
        var versionTask = server.GetModSyncVersion();
        yield return new WaitUntil(() => versionTask.IsCompleted);
        try
        {
            var version = versionTask.Result;

            Logger.LogInfo($"ModSync found server version: {version}");
            if (version != Info.Metadata.Version.ToString())
                Logger.LogWarning($"ModSync server version does not match plugin version. Found server version: {version}. Plugin may not work as expected!");
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error requesting server version. Please ensure the server mod is properly installed and try again."
            );
            yield break;
        }

        Logger.LogDebug("Fetching sync paths");
        var syncPathTask = server.GetModSyncPaths();
        yield return new WaitUntil(() => syncPathTask.IsCompleted);
        try
        {
            syncPaths = syncPathTask.Result;
            Logger.LogInfo($"ModSync: fetched {syncPaths.Count} sync paths from server.");
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error requesting sync paths. Please ensure the server mod is properly installed and try again."
            );
            yield break;
        }

        Logger.LogDebug("Processing sync paths");
        foreach (var syncPath in syncPaths)
        {
            if (Path.IsPathRooted(syncPath.path))
            {
                Chainloader.DependencyErrors.Add(
                    $"Could not load {Info.Metadata.Name} due to invalid sync path. Paths must be relative to SPT server root! Invalid path '{syncPath.path}'"
                );
                yield break;
            }

            if (!Path.GetFullPath(syncPath.path).StartsWith(Directory.GetCurrentDirectory()))
            {
                Chainloader.DependencyErrors.Add(
                    $"Could not load {Info.Metadata.Name} due to invalid sync path. Paths must be within SPT server root! Invalid path '{syncPath.path}'"
                );
                yield break;
            }
        }

        Logger.LogDebug("Running migrator");
        new Migrator(Directory.GetCurrentDirectory()).TryMigrate(Info.Metadata.Version, syncPaths);

        Logger.LogDebug("Loading syncPath configs");

        try
        {
            configSyncPathToggles = syncPaths
                .Select(syncPath => new KeyValuePair<string, ConfigEntry<bool>>(
                    syncPath.path,
                    Config.Bind(
                        "Synced Paths",
                        syncPath.name.Replace("\\", "/"),
                        syncPath.enabled,
                        new ConfigDescription(
                            $"Should the mod attempt to sync files from {syncPath.path.Replace("\\", "/")}",
                            null,
                            new ConfigurationManagerAttributes { ReadOnly = syncPath.enforced }
                        )
                    )
                ))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        catch (Exception e)
        {
            Logger.LogError($"Error binding sync path configs. This is likely a bug with ModSync. Please report it in the FIKA discord.\n{e}");
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error binding sync path configs. Please check your server configuration and try again."
            );
        }

        Logger.LogDebug("Loading previous sync data");
        try
        {
            previousSync = VFS.Exists(PREVIOUS_SYNC_PATH) ? Json.Deserialize<SyncPathModFiles>(VFS.ReadTextFile(PREVIOUS_SYNC_PATH)) : [];
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to malformed previous sync data. Please check ModSync_Data/PreviousSync.json for errors or delete it, and try again."
            );
            yield break;
        }

        // Per-install denylist — paths/globs the user (or admin, on a headless instance)
        // doesn't want ModSync to install or keep installed on this machine. Same file
        // and semantics for both player and headless. See EXCLUSIONS_SEED_TEMPLATE for
        // the in-file explanation written to the seed.
        //
        // First-run handling:
        //   1. Legacy v0.12.0 installs may have ModSync_Data/Exclusions.json (no 'c').
        //      Rename it in place so the user's existing list survives the format change.
        //   2. Otherwise, seed an empty array with the comment header.
        Logger.LogDebug("Loading local exclusions");
        if (!VFS.Exists(LOCAL_EXCLUSIONS_PATH))
        {
            if (VFS.Exists(LEGACY_EXCLUSIONS_PATH))
            {
                try
                {
                    File.Move(LEGACY_EXCLUSIONS_PATH, LOCAL_EXCLUSIONS_PATH);
                    Logger.LogInfo("ModSync: migrated legacy Exclusions.json → Exclusions.jsonc.");
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"ModSync: could not migrate Exclusions.json → .jsonc, will read legacy file in place: {e.Message}");
                }
            }
            else
            {
                try
                {
                    VFS.WriteTextFile(LOCAL_EXCLUSIONS_PATH, EXCLUSIONS_SEED_TEMPLATE);
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                    Chainloader.DependencyErrors.Add(
                        $"Could not load {Info.Metadata.Name} due to error writing local exclusions file. Please check BepInEx/LogOutput.log for more information."
                    );
                    yield break;
                }
            }
        }

        // Whichever file actually exists wins. Newtonsoft (via SPT's Json helper) skips
        // // and /* */ comments natively, so .jsonc content parses fine through the same
        // call we used for plain .json.
        var exclusionsFilePath = VFS.Exists(LOCAL_EXCLUSIONS_PATH) ? LOCAL_EXCLUSIONS_PATH : LEGACY_EXCLUSIONS_PATH;
        try
        {
            localExclusions = VFS.Exists(exclusionsFilePath) ? Json.Deserialize<List<string>>(VFS.ReadTextFile(exclusionsFilePath)) : [];
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to malformed local exclusion data. Please check ModSync_Data/Exclusions.jsonc for errors or delete it, and try again."
            );
            yield break;
        }

        Logger.LogDebug("Fetching exclusions");

        List<string> exclusions;
        var exclusionsTask = server.GetModSyncExclusions();
        yield return new WaitUntil(() => exclusionsTask.IsCompleted);
        try
        {
            exclusions = exclusionsTask.Result;
            Logger.LogInfo($"ModSync: fetched {exclusions.Count} exclusions from server.");
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error requesting exclusions. Please ensure the server mod is properly installed and try again."
            );
            yield break;
        }

        // Regular clients draw the sync windows (update/progress/restart) on top of
        // Tarkov's main menu, so we wait until CommonUI is up before continuing.
        // Fika.Headless skips the menu entirely (it uses PROFILE_ID to authenticate
        // and goes straight to its WebSocket loop), so CommonUI never instantiates
        // and this WaitUntil would yield forever. Skipping is safe because every
        // UI call further down is already gated on !IsHeadless (SilentMode kicks
        // in at AnalyzeModFiles, downstream windows early-exit, etc).
        if (IsHeadless)
            Logger.LogInfo("ModSync: headless detected, skipping CommonUI gate.");
        else
            yield return new WaitUntil(() => Singleton<CommonUI>.Instantiated);

        Logger.LogDebug("Hashing local files");
        var localModFilesTask = Sync.HashLocalFiles(
            Directory.GetCurrentDirectory(),
            EnabledSyncPaths,
            exclusions.Select(Glob.Create).ToList()
        );

        yield return new WaitUntil(() => localModFilesTask.IsCompleted);

        // Unity coroutines swallow exceptions thrown from inside their body, including
        // the rethrow that happens when reading `task.Result` on a faulted Task.
        // We catch explicitly here so failures surface in the log instead of dying silently.
        SyncPathModFiles localModFiles;
        try
        {
            localModFiles = localModFilesTask.Result;
        }
        catch (Exception e)
        {
            Logger.LogError($"ModSync: HashLocalFiles threw — {e.GetType().Name}: {e.Message}");
            Logger.LogError(e.ToString());
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error hashing local files. Please check BepInEx/LogOutput.log."
            );
            yield break;
        }

        VFS.WriteTextFile(LOCAL_HASHES_PATH, Json.Serialize(localModFiles));

        Logger.LogDebug("Fetching remote file hashes");
        var remoteHashesTask = server.GetRemoteModFileHashes(EnabledSyncPaths);
        yield return new WaitUntil(() => remoteHashesTask.IsCompleted);
        try
        {
            var remoteHashes = remoteHashesTask.Result;

            var localExclusionsForRemote = localExclusions.Select(Glob.CreateNoEnd).ToList();
            remoteModFiles = EnabledSyncPaths
                .Select(
                    (syncPath) =>
                    {
                        var remotePathHashes = remoteHashes[syncPath.path];

                        if (!syncPath.enforced)
                            remotePathHashes = remotePathHashes
                                .Where((kvp) => !Sync.IsExcluded(localExclusionsForRemote, kvp.Key))
                                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

                        return new KeyValuePair<string, Dictionary<string, ModFile>>(syncPath.path, remotePathHashes);
                    }
                )
                .ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error requesting server mod list. Please check the server log and try again."
            );
        }

        Logger.LogDebug("Comparing file hashes");
        try
        {
            AnalyzeModFiles(localModFiles);
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Chainloader.DependencyErrors.Add(
                $"Could not load {Info.Metadata.Name} due to error hashing local mods. Please ensure none of the files are open and try again."
            );
        }
    }

    private readonly UpdateWindow updateWindow = new("Installed mods do not match server", "Would you like to update?");
    private readonly ProgressWindow progressWindow = new("Downloading Updates...", "Your game will need to be restarted\nafter update completes.");
    private readonly AlertWindow restartWindow = new(new Vector2(480f, 200f), "Update Complete.", "Please restart your game to continue.");
    private readonly AlertWindow downloadErrorWindow = new(
        new Vector2(640f, 240f),
        "Download failed!",
        "There was an error updating mod files.\nPlease check BepInEx/LogOutput.log for more information.",
        "QUIT"
    );

    private void Awake()
    {
        ConsoleScreen.Processor.RegisterCommand(
            "modsync",
            () =>
            {
                ConsoleScreen.Log("Checking for updates.");
                StartCoroutine(StartPlugin());
            }
        );

        server = new Server(Info.Metadata.Version);

        configDeleteRemovedFiles = Config.Bind("General", "Delete Removed Files", true, "Should the mod delete files that have been removed from the server?");
    }

    private List<string> _optional;
    private List<string> optional =>
        _optional ??= EnabledSyncPaths
            .Where(syncPath => !syncPath.enforced)
            .SelectMany(syncPath =>
                addedFiles[syncPath.path]
                    .Select(file => $"ADDED {file}")
                    .Concat(updatedFiles[syncPath.path].Select(file => $"UPDATED {file}"))
                    .Concat(configDeleteRemovedFiles.Value || syncPath.enforced ? removedFiles[syncPath.path].Select(file => $"REMOVED {file}") : [])
                    .Concat(createdDirectories[syncPath.path].Select(file => $@"CREATED {file}\"))
            )
            .ToList();

    private List<string> _required;
    private List<string> required =>
        _required ??= EnabledSyncPaths
            .Where(syncPath => syncPath.enforced)
            .SelectMany(syncPath =>
                addedFiles[syncPath.path]
                    .Select(file => $"ADDED {file}")
                    .Concat(updatedFiles[syncPath.path].Select(file => $"UPDATED {file}"))
                    .Concat(configDeleteRemovedFiles.Value ? removedFiles[syncPath.path].Select(file => $"REMOVED {file}") : [])
                    .Concat(createdDirectories[syncPath.path].Select(file => $@"CREATED {file}\"))
            )
            .ToList();

    private List<string> _noRestart;
    private List<string> noRestart =>
        _noRestart ??= EnabledSyncPaths
            .Where(syncPath => !syncPath.restartRequired)
            .SelectMany(syncPath =>
                addedFiles[syncPath.path]
                    .Concat(updatedFiles[syncPath.path])
                    .Concat((configDeleteRemovedFiles.Value || syncPath.enforced) ? removedFiles[syncPath.path] : [])
                    .Concat(createdDirectories[syncPath.path])
            )
            .ToList();

    private void OnGUI()
    {
        if (!Singleton<CommonUI>.Instantiated)
            return;

        if (restartWindow.Active)
            restartWindow.Draw(StartUpdaterProcess);

        if (progressWindow.Active)
            progressWindow.Draw(downloadCount, totalDownloadCount, required.Count != 0 || noRestart.Count != 0 ? null : () => Task.Run(CancelUpdatingMods));

        if (updateWindow.Active)
        {
            updateWindow.Draw(
                (optional.Count != 0 ? string.Join("\n", optional) : "")
                    + (optional.Count != 0 && required.Count != 0 ? "\n\n" : "")
                    + (required.Count != 0 ? "[Enforced]\n" + string.Join("\n", required) : ""),
                () => Task.Run(() => SyncMods(addedFiles, updatedFiles, createdDirectories)),
                required.Count != 0 && optional.Count == 0 ? null : SkipUpdatingMods
            );
        }

        if (downloadErrorWindow.Active)
            downloadErrorWindow.Draw(Application.Quit);
    }

    public void Start()
    {
        StartCoroutine(StartPlugin());
    }

    public void Update()
    {
        if (updateWindow.Active || progressWindow.Active || restartWindow.Active || downloadErrorWindow.Active)
        {
            if (Singleton<LoginUI>.Instantiated && Singleton<LoginUI>.Instance.gameObject.activeSelf)
                Singleton<LoginUI>.Instance.gameObject.SetActive(false);

            if (Singleton<PreloaderUI>.Instantiated && Singleton<PreloaderUI>.Instance.gameObject.activeSelf)
                Singleton<PreloaderUI>.Instance.gameObject.SetActive(false);

            if (Singleton<CommonUI>.Instantiated && Singleton<CommonUI>.Instance.gameObject.activeSelf)
                Singleton<CommonUI>.Instance.gameObject.SetActive(false);
        }
        else if (pluginFinished)
        {
            pluginFinished = false;
            if (Singleton<LoginUI>.Instantiated && !Singleton<LoginUI>.Instance.gameObject.activeSelf)
                Singleton<LoginUI>.Instance.gameObject.SetActive(true);

            if (Singleton<PreloaderUI>.Instantiated && !Singleton<PreloaderUI>.Instance.gameObject.activeSelf)
                Singleton<PreloaderUI>.Instance.gameObject.SetActive(true);

            if (Singleton<CommonUI>.Instantiated && !Singleton<CommonUI>.Instance.gameObject.activeSelf)
                Singleton<CommonUI>.Instance.gameObject.SetActive(true);
        }
    }
}
