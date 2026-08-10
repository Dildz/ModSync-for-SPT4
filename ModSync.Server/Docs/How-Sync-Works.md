# How ModSync Works
> **As of v0.12.6**

# Typical Workflow
## Start Up

### Server Version Check

In order to make sure client & server versions are compatible, the first step taken is to check the ModSync server version. If the
client and server differ by at least one minor version, the server will enter [Rescue Mode](#rescue-mode) for that client.

### Get Sync Paths

Next, the client will request info on all of the [paths the server is requesting to sync](Configuration#syncpaths). This determines which files the client will hash
and compare later on.

### Migrator

As an intermediary step, the client will check what version of ModSync was previously used to perform a sync. If it finds an older version
it will attempt to update any previously saved configuration files and the PreviousSync.json

### Load Previous Sync

The PreviousSync.json file keeps track of what files were previously reported by the server. This information is used by the client for non-enforced
sync paths to enable some flexibility in changing local files without the server immediately overriding them. When [enforced is true](#enforced-sync-paths)
slightly different logic is used for checking modified files.

At this stage, the JSON file is loaded, if it exists.

### Load Client Configuration

Then, the [`BepInEx/config/corter.modsync.cfg`](Configuration#bepinex-configuration-manager) and [local exclusions are loaded from `ModSync_Data/Exclusions.jsonc`](Configuration#exclusionsjsonc).

### Get Server Exclusions

[Exclusions set on the server](Configuration#exclusions) are requested and cached by the client.

## File Discovery

Next, starting with the [provided sync paths](Configuration#syncpaths), the client will recursively search for files. Both [server exclusions](Configuration#exclusions)
and [local exclusions](Configuration#exclusionsjsonc) are taken into account while search and matching files or folders will be ignored (files are not hashed and children are ignored).

> Several popular mods ship with empty folders and will crash if they are not present. Because of this ModSync has special handling for empty folders and will sync them as if they were files.

## File Hashing

[Hashing](https://en.wikipedia.org/wiki/File_verification) is an integral of how ModSync works. Generated hashes are used to compare files from the server and client without manually comparing
each byte of the file.

### Client Hashing

For each discovered file ModSync computes the file's hash. The exact way this occurs depends on the size of the file being hashed.

- Files `< 10MB` are read straight into memory and hashed in their entirety using the [MetroHash algorithm](https://github.com/jandrewrogers/MetroHash)[^1][^2].
- Files `> 10MB` (usually bundles) are hashed using a really interesting "constant-time hashing algorithm" called [imohash](https://github.com/kalafut/imohash).
  imohash works by sampling 32KB chunks of the file at the beginning, middle, and end. This hash is augmented with the size of file to form a heuristic hash.

### Get Server Hashes

Now the client requests file hashes for [enabled sync paths](Configuration#options-1) to compare.

## Comparison Process

The comparison is the heart of ModSync, the ~~secret~~ sauce, if you will. By comparing the list of files and hashes from the server with the one the client generates, ModSync can determine
which files have been [added](#added-files), [updated](#updated-files), [removed](#removed-files), and [empty directories that have been created](#created-directories). 

### Added Files

Perhaps the simplest comparison done by ModSync is added files. To determine added files, ModSync finds file entries on the server that aren't present on the client.

### Updated Files

> [Alternative behavior when `"enforced": true`](#enforced-sync-paths)

To determine updated files, ModSync filters for files that are present on both the client and the server. The server hashes for these files are compared
to those found in [PreviousSync.json](#load-previous-sync). If the previous sync and the server hashes don't match then the client and server hashes
are compared. If these hashes don't match then, and only then, the files are considered updated and queued for download.

### Removed Files

> [Alternative behavior when `"enforced": true`](#enforced-sync-paths)

A file will be considered removed when it was previously synced (therefore is present in the [PreviousSync.json](#load-previous-sync)) but no longer present on the server. This is done as a safety net against deleting files that get created on the client, but are not present on the server.

### Created Directories

Another simple comparison, created directories are found by comparing folders present on the server that aren't present on the client.

## Downloading Updates

During this stage, ModSync will download any added/updated files 2 at a time. (Upstream used 8; this fork lowered it because slow uplinks combined with Mono's TLS stack proved fragile under heavy concurrency — handshakes timed out and retry storms didn't recover. A shared `HttpClient` keeps the TCP/TLS sessions warm, so 2 is plenty.) These files are downloaded into the `ModSync_Data/PendingUpdates` directory with the notable exception of sync paths configured with `"restartRequired": false` and created empty directories. These
are placed directly into the client's SPT installation.

> [!NOTE]
> In the event of a download failure, ModSync will retry the download up to 5 times to prevent a failure causing the need for a full redownload.

## Saving State

Once all files have been downloaded, ModSync saves its updated `PreviousSync.json` and any removed files to `ModSync_Data/RemovedFiles.json`.

## Applying Update

How updates are applied depends on the client type:

**Windows desktop players**

ModSync launches the ModSync Updater (`ModSync.Updater.exe`). This helper program waits until Tarkov has closed, moves updated files from `ModSync_Data/PendingUpdates` into the correct locations, and deletes any removed files.

> [!NOTE]
> The ModSync Updater writes logs to `ModSync_Data/ModSync.log`. If you get any errors during the update, check there first. (As of v0.12.4 the preloader patcher writes to the same file, so this log covers updates on every platform.)

**Headless clients (Docker/Linux)**

The Updater cannot run on headless because it requires a Windows UI and the EFT executable to relaunch. Instead:

1. The ModSync plugin stages all downloaded files into `ModSync_Data/PendingUpdates` as normal, then **quits immediately** — no in-process apply is attempted.
2. On the next boot, the **BepInEx preloader patcher** (`Corter-ModSync-Prepatch.dll`) runs before any plugin DLLs are loaded into memory. It moves all pending updates into their final locations and restores any `.modsync-bak` backup files for removed managed DLLs. Each file is applied independently — if one file fails, the rest still apply and only the failed file is retried on the next boot.
3. Once the patcher finishes, BepInEx loads plugins normally with all updates already in place.

This two-step approach avoids the locked-file errors that would occur if the plugin tried to overwrite DLLs that are already loaded in memory.

> [!NOTE]
> The patcher logs everything it does to `ModSync_Data/ModSync.log` — the same file the Windows Updater writes. Unlike `BepInEx/LogOutput.log`, which is overwritten on every boot, this file persists across restarts, making it the first place to check when diagnosing update problems on headless.

One category of file *is* still locked when the patcher runs: preloader patcher DLLs themselves, since BepInEx loads all of them into memory before running any of them. A locked DLL can't be overwritten or deleted, but it *can* be renamed — so the patcher renames the old copy to `*.modsync-old`, puts the new file in its place, and deletes the leftover on the next boot. If you spot a `.modsync-old` file in your `BepInEx` folders, that's this mechanism mid-cycle — it cleans itself up automatically, no action needed.

# Alternative Workflows

Several features of ModSync will let you as an admin or user modify the mod's behavior. The sections below break down some of the common use cases and how
operation of the mod changes with them active.

## Rescue Mode

Rescue mode is activated when the ModSync client is incompatible with the current server version. In rescue mode, when the client requests the server's list
of sync paths and file hashes, the server will substitute the actual values with placeholders designed to force an update to the newer version of ModSync.

Typically this means forcing the client to download an updated `../BepInEx/plugins/Corter-ModSync/Corter-ModSync.dll` and `../ModSync.Updater.exe`.

## Enforced Sync Paths

Enforced sync paths modify behavior in a couple of key ways to help ensure client installations are as similar to the server as possible. These modifications
in behavior work together to achieve that goal.

### Local Exclusions

When working with enforced paths, local exclusions are **ignored** entirely.

### Updated Files

The comparison used to determine updated files is actually much simpler when enforced is true. The server's hash for a given file is simply compared to
the client's. If the two don't match, then the file is marked as updated and queued for download.

### Removed Files

Similar to [updated files](#updated-files-1), when dealing with enforced paths the comparison for removed files is simplified.
Removed files are simply any files that exist on client, but not on the server. In addition, the [client configuration to disable file deletion](Configuration#options-1)
is ignored entirely for files in an enforced path.

> [!WARNING]
> This can be a bit of a footgun. Admins must be careful to add all files the client will need or generate to their install or add them as
> [server-side exclusions](Configuration#exclusions)

### ModSync's own components are enforced per-audience

ModSync enforces its own files differently depending on who's connecting, so a client can't accidentally break its own update mechanism:

- The **Updater** (`ModSync.Updater.exe`) is enforced for **desktop players** (who run it) and *not* enforced for **headless** (which never does).
- The **patcher** (`Corter-ModSync-Prepatch.dll`) is the reverse — enforced for **headless** (which applies updates with it) and *not* enforced for **players**.
- The **plugin** is always enforced for everyone.

Because enforced paths ignore local exclusions, a player can't exclude the Updater and a headless can't exclude the patcher — but each *can* trim the component it doesn't run via [`Exclusions.jsonc`](Configuration#exclusionsjsonc). The server resolves this per request using the same `?headless=1` flag it uses for plugin filtering. Note the **server** always keeps all three files so it can serve and update them to clients — this trimming is per-client only.

## Headless Clients

When a headless client connects, ModSync uses modified sync behavior throughout:

### Plugin filtering

The server detects headless clients via a `?headless=1` query parameter that the plugin appends when the `Fika.Headless` plugin is loaded. For the `../BepInEx/plugins` scope, the server only returns hashes for files listed in [`headlessIncludes`](Configuration#headlessincludes) — the full plugin list is never exposed to headless. Patchers and config pass through unfiltered (minus global exclusions).

This means a headless client will only ever download the bot-AI and Fika DLLs you explicitly allow — it will never accidentally pull down UI mods or GPU-side plugins it can't use.

### Update application

Headless clients cannot run `ModSync.Updater.exe` (it requires a Windows UI environment). Instead, ModSync quits after staging downloads and the preloader patcher handles the apply on the next boot. See [Applying Update](#applying-update) for the full details.