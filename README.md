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

> The ModSync Updater (client-only) requires the
> [.NET 9.0 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
> If you use the SPT launcher, you'll have it; headless clients may need to install it manually.

1. Download the latest version of the mod from the [GitHub Releases](https://github.com/Dildz/ModSync-for-SPT4.0/releases) page
2. Extract into your SPT folder like any other mod
3. Start the server

> [!NOTE]
> Make sure you install all the files to both the server and the client. They are all required!
>
> ***Yes. Even the .exe***

## Configuration

For information about modifying the ModSync config, see [the configuration page on the wiki](https://github.com/c-orter/ModSync/wiki/Configuration).

> [!NOTE]
> **SPT 4 path change.** Default syncPaths now use `../BepInEx/...` (note the `../`)
> because the SPT 4 server runs from `<gameRoot>/SPT/` rather than the game root
> directly. The upstream wiki still shows the SPT 3 (no-prefix) format.

## Frequently Asked Questions

Checkout some [frequently asked questions](https://github.com/c-orter/ModSync/wiki/FAQ) on the wiki!

## [How Sync Works](https://github.com/c-orter/ModSync/wiki/How-Sync-Works)

If you are looking to understand how syncing works, take a look at the technical writeup on the wiki. It goes into detail on the different stages of the sync process
and different modes of operation.

## Roadmap

- [x] Initial release
- [x] Super nifty GUI for notifying user of mod changes and monitoring download progress
- [x] Ability to exclude files/folders from syncing from both client and server
- [x] Custom folder sync support (May be useful for cached bundles? or mods that add files places that aren't BepInEx/plugins, BepInEx/config, or user/mods)
- [x] Maybe cooler progress bar/custom UI (low priority)
- [x] External updater to prevent file-in-use issues
- [ ] Allow user to upload their local mods folders to host. (Needs some form of authorization, could be cool though)
- [ ] Buttons to sync from the BepInEx config menu (F12)
- [x] Real tests?!? (low priority)
