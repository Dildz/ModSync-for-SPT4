# Corter's Mod Sync For SPT & Fika

> [!IMPORTANT]
> **SPT 4.0 Port.** This is a fork of [c-orter/ModSync](https://github.com/c-orter/ModSync)
> ported from SPT 3.11 to SPT 4.0.x. Upstream's last release (v0.11.1, Mar 2025)
> targets SPT 3.11 and has not been updated for SPT 4. All credit for the original
> project goes to [Corter](https://github.com/c-orter). Released under WTFPL.

## About The Project

This project allows clients to easily add/update/remove mods, keeping them in sync with the host when playing on a remote SPT/Fika server.

<table>
<tbody>
<tr>
<td>

![Updater Complete](https://github.com/user-attachments/assets/f66a09b5-e133-418f-abf9-466b619cd2c9)</td>
</tr>
<tr>
<td>Updater completed</td>
</tr>
</tbody>
</table>

<table>
<tbody>
<tr>
<td>

![Update Required](https://github.com/user-attachments/assets/03c3ed36-f6d3-4067-b1dc-48fe726ed489)</td>
<td>

![Update Progress](https://github.com/user-attachments/assets/c4ca8953-03be-4d3b-af96-6ee7f1ee3ce2)</td>
</tr>
<tr>
<td>Prompt to update</td>
<td>Update progress</td>
</tr>
</tbody>
</table>

## Getting Started

### Installation

> The ModSync Updater requires the [.NET 9.0 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
> The SPT launcher bundles it — if you installed SPT manually, make sure it's installed.

1. Download the latest version of the mod from the [GitHub Releases](https://github.com/Dildz/ModSync-for-SPT4/releases) page
2. Extract into your **game root** (the folder containing `EscapeFromTarkov.exe`) — the zip contains both `BepInEx/` and `SPT/` directories that land in the right places automatically
3. Start the server

> [!NOTE]
> Make sure you install all the files to both the server and the client. They are all required!
>
> ***Yes. Even the .exe***

## Configuration

For information about modifying the ModSync config, see [the configuration page on the wiki](https://github.com/Dildz/ModSync-for-SPT4/wiki/Configuration).

## Frequently Asked Questions

Checkout some [frequently asked questions](https://github.com/Dildz/ModSync-for-SPT4/wiki/FAQ) on the wiki!

## [How Sync Works](https://github.com/Dildz/ModSync-for-SPT4/wiki/How-Sync-Works)

If you are looking to understand how syncing works, take a look at the technical writeup on the wiki. It goes into detail on the different stages of the sync process
and different modes of operation.

## Roadmap

- [x] Initial release
- [x] Super nifty GUI for notifying user of mod changes and monitoring download progress
- [x] Ability to exclude files/folders from syncing from both client and server
- [x] Custom folder sync support (BepInEx/plugins, BepInEx/patchers, BepInEx/config, and custom paths)
- [x] Maybe cooler progress bar/custom UI (low priority)
- [x] External updater to prevent file-in-use issues on Windows
- [x] Headless client support — `headlessIncludes` allowlist serves only the plugins a headless instance needs
- [x] BepInEx preloader patcher — applies staged updates on headless before any DLLs are locked, replacing the Updater for Docker/Linux
- [x] `managedIncludes` — sync `EscapeFromTarkov_Data/Managed/` assemblies to players via filename allowlist
- [x] `baseFiles` — sync mods that replace base-game files, backing the original up and restoring it on removal
- [x] Per-syncPath `headless` gate — withhold graphics-only mods from headless clients entirely
- [x] Real tests?!? (low priority)
- [ ] Allow user to upload their local mods folders to host (needs some form of authorization)
- [ ] Buttons to sync from the BepInEx config menu (F12)
