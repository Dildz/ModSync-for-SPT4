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

[BepInPlugin("corter.modsync", "Corter ModSync", "0.13.0")]
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
    /// The Updater's path in WIRE form (game-root-relative), i.e. how it appears as a key in
    /// the server's file list. Server-side it's <c>../ModSync.Updater.exe</c>; the leading
    /// <c>../</c> is stripped by PathExt.ToWirePath before it reaches us.
    /// </summary>
    private const string UPDATER_WIRE_PATH = "ModSync.Updater.exe";


    /// <summary>
    /// True when the server runs a different ModSync version than this plugin. When set, this
    /// run syncs ONLY ModSync's own components and then restarts, so the NEXT launch evaluates
    /// the server's config with matching code.
    ///
    /// Why this matters: an outdated plugin cannot be trusted to interpret a newer server's
    /// config. v0.12.6 added opt-in carve-outs and baseFiles, and a v0.12.5 client reading that
    /// config concluded a mod it should have left alone had been deleted server-side - and
    /// offered to remove it, with none of the safety checks that shipped alongside the feature.
    /// Upstream only logged a warning here and carried on; this makes the warning mean something.
    /// </summary>
    private bool selfUpdatePending;

    /// <summary>
    /// Content written to a fresh <c>ModSync_Data/Exclusions.jsonc</c> on first run.
    /// Same template for player and headless installs - no hardcoded mod names. The
    /// in-file comment header explains the semantic so admins/players can hand-edit
    /// the file without consulting docs.
    ///
    /// <c>@"..."</c> is a verbatim string literal: backslashes are taken literally,
    /// and the only escape sequence is <c>""</c> for a single double-quote.
    /// </summary>
    private const string EXCLUSIONS_SEED_TEMPLATE = @"// Personal denylist - paths or globs ModSync should NOT install or keep
// installed on this machine. Applied on top of the server's exclusions.
//
// Semantics:
//   - If ModSync previously installed a listed file, it will be REMOVED
//     on next sync.
//   - If you copied the file in by hand (ModSync never installed it),
//     ModSync leaves it alone - only files ModSync installed are touched.
//   - Listed files won't be downloaded going forward.
//
// On a Fika headless install this file is usually EMPTY - the server's
// `headlessIncludes` allowlist controls what reaches headless. Use this
// file only for per-install overrides on top of the allowlist.
//
// Edits to this file are read at game startup, not live - restart EFT
// to apply.
//
// You do NOT need to list ModSync's own components here. The headless-only
// patcher appears as its own toggle in the F12 menu (untick it to remove it),
// and a headless drops the desktop-only Updater.exe by itself.
//
// Examples (delete the empty array below and replace with your own):
//   ""BepInEx/plugins/NoInsurance.dll"",
//   ""BepInEx/plugins/HollywoodGraphics/**""
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
        ProcessedSyncPaths
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

    /// <summary>
    /// This plugin's version, as announced to the server on /modsync/paths so it can decide
    /// whether we're safe to hand the full config to. `public` so Server.cs can read it.
    /// </summary>
    public static string PluginVersion => MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();

    /// <summary>
    /// True if this syncpath's files already exist on the client. Used to seed an opt-in
    /// toggle's DEFAULT to the installed state, so a mod the player already has isn't
    /// defaulted off and immediately flagged for removal. Only affects the first bind.
    /// </summary>
    private static bool IsInstalledLocally(string relPath)
    {
        var full = Path.Combine(Directory.GetCurrentDirectory(), relPath);
        if (File.Exists(full))
            return true;
        return Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any();
    }
    /// <summary>
    /// An "optional" path is one the player genuinely chooses: opt-in (<c>enabled:false</c>)
    /// and not enforced by the server. Only these get a working F12 checkbox - everything
    /// else is shown for transparency but drawn as text (see <see cref="InfoOnlyDrawer"/>).
    /// </summary>
    private static bool IsOptional(SyncPath syncPath) =>
        !syncPath.enabled
        && !syncPath.enforced;

    /// <summary>
    /// Which syncpaths appear in the F12 menu at all. Three kinds earn a place:
    ///   • opt-in mods - the player's actual choices, drawn as real checkboxes
    ///   • enforced paths - shown read-only so a player can SEE what the server pins
    ///   • ModSync's own components - same, so the mod isn't invisible in its own menu
    ///
    /// Everything else (the ../BepInEx/plugins|patchers|config catch-alls, and any other
    /// enabled:true path) is hidden: it's neither a choice nor a server-pinned guarantee, so
    /// listing it is noise. Those are opted out of per-machine via ModSync_Data/Exclusions.jsonc,
    /// which the F12 menu can't affect anyway.
    /// </summary>
    private static bool IsVisibleInMenu(SyncPath syncPath) =>
        IsOptional(syncPath)
        || syncPath.enforced
        || Builtins.IsBuiltinWirePath(syncPath.path);

    /// <summary>
    /// Draws a visible-but-not-selectable path as a short explainer instead of a checkbox, so
    /// the menu stays informative without offering toggles that do nothing. ConfigurationManager
    /// calls this in place of the normal control.
    ///
    /// The text deliberately omits the mod's name - ConfigurationManager already prints it in
    /// the left-hand column, so repeating it just reads as a stutter.
    /// </summary>
    private static Action<ConfigEntryBase> InfoOnlyDrawer(SyncPath syncPath)
    {
        var text = syncPath.enforced
            ? "Enforced by the server - always installed."
            : "Installed by default - opt out via ModSync_Data/Exclusions.jsonc.";

        return _ => GUILayout.Label(text, GUILayout.ExpandWidth(true));
    }

    // Players pick opt-in mods via the F12 toggles. A headless has no F12 and is driven purely
    // by the server-side recipe (headlessIncludes / headlessManagedIncludes / enforced /
    // exclusions), so it ignores the toggles entirely and syncs every configured path - the
    // server does the filtering. This keeps headlessIncludes authoritative instead of being
    // silently overridden by a toggle the headless can't reach.
    private List<SyncPath> EnabledSyncPaths =>
        // Version mismatch: restrict this run to ModSync's own components. Everything
        // downstream (local hash, remote request, diff, removals) works off this set, so
        // limiting it here is enough to make the whole run a self-update.
        selfUpdatePending
        ? syncPaths.Where(sp => Builtins.IsBuiltinWirePath(sp.path)).ToList()
        : IsHeadless
            // `headless:false` is the one thing a headless still honours - it's how an admin
            // marks a mod player-only (GPU-specific, UI-only, …). The server already omits
            // these from a headless response; filtering here too keeps the client from asking
            // for something it will never be given.
            ? syncPaths.Where(syncPath => syncPath.headless).ToList()
            : syncPaths.Where(syncPath => configSyncPathToggles[syncPath.path].Value || syncPath.enforced).ToList();

    /// <summary>
    /// Opt-in paths the player has UN-checked that ModSync previously installed (they have a
    /// PreviousSync entry). These get compared with an empty remote so their ModSync-installed
    /// files are UNINSTALLED - the toggle acts as a real install/remove switch. A path never
    /// synced by ModSync isn't here (not in PreviousSync), so hand-installed mods are left alone.
    /// Headless has no toggles, so nothing is "deselected" there.
    /// </summary>
    private List<SyncPath> DeselectedSyncPaths =>
        // Never uninstall anything during a self-update run - that decision belongs to the
        // NEW plugin, on the next launch, with the current safety checks in place.
        selfUpdatePending || IsHeadless
            ? []
            : syncPaths.Where(syncPath =>
                !syncPath.enforced
                && !configSyncPathToggles[syncPath.path].Value
                && previousSync.ContainsKey(syncPath.path)).ToList();

    /// <summary>
    /// The full set the diff/compare + apply operate on: everything to keep in sync
    /// (<see cref="EnabledSyncPaths"/>) PLUS anything just deselected that we need to uninstall
    /// (<see cref="DeselectedSyncPaths"/>). The server is only asked for the enabled set, so
    /// deselected paths get an empty remote → the removal logic uninstalls what we installed.
    /// </summary>
    private List<SyncPath> ProcessedSyncPaths => EnabledSyncPaths.Concat(DeselectedSyncPaths).ToList();

    private bool SilentMode =>
        IsHeadless
        || ProcessedSyncPaths.All(syncPath =>
            syncPath.silent
            || (
                addedFiles[syncPath.path].Count == 0
                && updatedFiles[syncPath.path].Count == 0
                && (!(configDeleteRemovedFiles.Value || syncPath.enforced) || removedFiles[syncPath.path].Count == 0)
                && createdDirectories[syncPath.path].Count == 0
            )
        );

    private bool NoRestartMode =>
        ProcessedSyncPaths.All(syncPath =>
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
            ProcessedSyncPaths,
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
        // PreviousSync records what the server last offered, and it's the ONLY thing that
        // licenses a removal - no entry for a path means nothing under it can be removed.
        //
        // A self-update run only ever sees ModSync's own components, so writing remoteModFiles
        // wholesale would erase every other path's record and leave the next launch unable to
        // remove anything (un-ticking a mod would silently do nothing for one boot). Merge
        // instead: refresh the paths this run actually covered, keep the rest untouched.
        var syncData = selfUpdatePending ? Sync.MergePreviousSync(previousSync, remoteModFiles) : remoteModFiles;

        VFS.WriteTextFile(PREVIOUS_SYNC_PATH, Json.Serialize(syncData));
        if (ProcessedSyncPaths.Any(syncPath => (configDeleteRemovedFiles.Value || syncPath.enforced) && removedFiles[syncPath.path].Count != 0))
            VFS.WriteTextFile(REMOVED_FILES_PATH, Json.Serialize(removedFiles.SelectMany(kvp => kvp.Value).ToList()));
    }


    private void StartUpdaterProcess()
    {
        if (IsHeadless)
        {
            // Plugin DLLs are already loaded into memory by the time this runs - attempting
            // File.Copy(overwrite:true) throws IOException("File has a user-mapped section").
            // Corter-ModSync-Prepatch (BepInEx/patchers/) applies PendingUpdates at preloader
            // stage on the next boot, before any DLLs are locked, so just quit here.
            Logger.LogInfo("ModSync: headless - update staged, restarting for patcher to apply.");
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

    private IEnumerator StartPlugin()
    {
        cts = new CancellationTokenSource();
        // Leftover update data at this point means the apply step couldn't finish
        // (Updater on desktop, preloader patcher on headless). Both write to the same
        // ModSync_Data/ModSync.log, so one message covers both platforms.
        if (Directory.Exists(PENDING_UPDATES_DIR) || File.Exists(REMOVED_FILES_PATH))
            Logger.LogWarning(
                "ModSync found a previous update that could not be fully applied. Check 'ModSync_Data/ModSync.log' for details. Attempting to continue."
            );

        Logger.LogDebug("Fetching server version");
        var versionTask = server.GetModSyncVersion();
        yield return new WaitUntil(() => versionTask.IsCompleted);
        try
        {
            var version = versionTask.Result;

            Logger.LogInfo($"ModSync found server version: {version}");

            selfUpdatePending = version != Info.Metadata.Version.ToString();
            if (selfUpdatePending)
                Logger.LogWarning(
                    $"ModSync server version ({version}) does not match this plugin ({Info.Metadata.Version}). "
                    + "Updating ModSync itself first - mods will sync on the next launch.");
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
                        // Seed the toggle DEFAULT from the installed state: an opt-in mod the
                        // player already has defaults to CHECKED (kept, not flagged for removal);
                        // one they don't have defaults to unchecked. `enabled:true` paths stay on.
                        // Only the FIRST bind uses this default - the saved value wins afterwards.
                        syncPath.enabled || (!IsHeadless && IsInstalledLocally(syncPath.path)),
                        new ConfigDescription(
                            $"Should the mod attempt to sync files from {syncPath.path.Replace("\\", "/")}",
                            null,
                            new ConfigurationManagerAttributes
                            {
                                // Opt-in mods, enforced paths and ModSync's own components are
                                // shown (see IsVisibleInMenu); the catch-alls are not. Of those
                                // shown, only opt-in paths are an actual choice, so they keep a
                                // real checkbox - the rest are drawn as a one-line explainer
                                // with no control, rather than a dead checkbox to click at.
                                Browsable = IsVisibleInMenu(syncPath),
                                ReadOnly = !IsOptional(syncPath),
                                HideDefaultButton = !IsOptional(syncPath),
                                CustomDrawer = IsOptional(syncPath) ? null : InfoOnlyDrawer(syncPath),
                            }
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

        // Per-install denylist - paths/globs the user (or admin, on a headless instance)
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

        // A headless never runs ModSync.Updater.exe (the patcher applies its updates), so the
        // file is pure dead weight there. Drop it unconditionally: excluding it keeps the next
        // sync from pulling it back (it's non-enforced for headless, see ResolveEnforced), and
        // the direct delete covers a FRESH install where the exe shipped in the zip and was
        // never tracked in PreviousSync - the removal path alone would never touch it. Nothing
        // loads the exe on headless, so it's never locked.
        if (IsHeadless)
        {
            localExclusions.Add(UPDATER_WIRE_PATH);
            try
            {
                if (File.Exists(UPDATER_PATH))
                {
                    File.Delete(UPDATER_PATH);
                    Logger.LogInfo("ModSync: headless - removed unused ModSync.Updater.exe.");
                }
            }
            catch (Exception e)
            {
                // Non-fatal: a leftover exe is harmless, so never block startup over it.
                Logger.LogWarning($"ModSync: could not remove unused ModSync.Updater.exe: {e.Message}");
            }
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
            syncPaths, // ALL paths, so disabled overrides claim (carve out) their own files
            exclusions.Select(Glob.Create).ToList(),
            // Must match ProcessedSyncPaths membership. Headless treats every path as active;
            // players use toggle/enforced, PLUS any deselected path they previously synced (so
            // its local files are hashed and can be uninstalled).
            isActive: syncPath => IsHeadless || configSyncPathToggles[syncPath.path].Value || syncPath.enforced || previousSync.ContainsKey(syncPath.path)
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
            Logger.LogError($"ModSync: HashLocalFiles threw - {e.GetType().Name}: {e.Message}");
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
            remoteModFiles = ProcessedSyncPaths
                .Select(
                    (syncPath) =>
                    {
                        // Deselected paths were NOT requested from the server (only EnabledSyncPaths
                        // is), so they get an empty remote - that's what makes GetRemovedFiles
                        // uninstall the files ModSync previously installed for them.
                        if (!remoteHashes.TryGetValue(syncPath.path, out var remotePathHashes))
                            return new KeyValuePair<string, Dictionary<string, ModFile>>(
                                syncPath.path, new Dictionary<string, ModFile>(StringComparer.OrdinalIgnoreCase));

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

    // These three drive what the update window DRAWS, so they must span the same set the diff
    // ran over (ProcessedSyncPaths) - not just the enabled set. A deselected path only ever
    // contributes REMOVED lines, but if we listed it over EnabledSyncPaths its removals would
    // count towards UpdateCount (opening the window) while rendering nothing: an empty prompt.
    private List<string> _optional;
    private List<string> optional =>
        _optional ??= ProcessedSyncPaths
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
        _required ??= ProcessedSyncPaths
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
        _noRestart ??= ProcessedSyncPaths
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
