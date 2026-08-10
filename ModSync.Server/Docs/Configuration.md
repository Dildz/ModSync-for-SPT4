# Configuration

ModSync has two configuration surfaces:

1. **Server** `config.jsonc` — what the server walks, what to never sync, what reaches a Fika headless install, and which Unity Managed DLLs to push.
2. **Each install's** `<game>/ModSync_Data/Exclusions.jsonc` — personal per-install opt-outs (player or headless).

Both files are created on first run with sensible defaults and a comment header you can hand-edit without consulting docs.

- [How ModSync decides what you get](#how-modsync-decides-what-you-get) — **start here**
- [Server](#server)
  - [`syncPaths`](#syncpaths)
    - [Options](#options)
    - [Examples](#examples) → [Usage Examples](UsageExamples)
  - [Mods that replace base-game files](#mods-that-replace-base-game-files)
    - [When ModSync won&#39;t remove a mod](#when-modsync-wont-remove-a-mod)
  - [`exclusions`](#exclusions)
  - [`headlessIncludes`](#headlessincludes)
  - [`managedIncludes`](#managedincludes)
  - [`headlessManagedIncludes`](#headlessmanagedincludes)
  - [Setting up a Fika headless install](#setting-up-a-fika-headless-install)
- [Client](#client)
  - [BepInEx Configuration Manager](#bepinex-configuration-manager)
    - [Options](#options-1)
  - [Exclusions.jsonc](#exclusionsjsonc)
- [Where does my mod go?](#where-does-my-mod-go)
- [Reference: Fika](#reference-fika)

# How ModSync decides what you get

**New here? Read this first — it's the whole model in one page.**

ModSync isn't two competing systems. It's **one model with a dial**, and everything else is just how far you turn it.

## The dial: lazy ↔ curated

Every file the server offers is governed by a **syncPath**. You choose how granular to be:

- **Lazy end** — keep the three catch-all syncPaths (`../BepInEx/plugins`, `../BepInEx/patchers`, `../BepInEx/config`). Everything syncs by default; a player removes what they don't want on their own machine. Best for **large modsets** — zero per-mod setup.
- **Curated end** — *also* name individual mods as objects and mark the extras `"enabled": false`. Those show up in each player's **F12 menu** as opt-in toggles. Best for **slim / choose-your-loadout sets** — players pick what they run.
- **Anywhere between** — name only the handful you want optional; the catch-alls cover the rest. This is the sweet spot for most servers.

There's no mode switch to flip. Adding a named override on top of the catch-alls just moves the dial — you can mix freely.

## The two player levers (one per end of the dial)

However you set the dial, a **player** has one lever to decline a mod — and *which* lever depends on your style:

| Your style                            | The player's lever                               | How it works                                    |
| ------------------------------------- | ------------------------------------------------ | ----------------------------------------------- |
| **Curated** (you named the mod) | the **F12 "Synced Paths" menu**             | tick / untick that mod's toggle                 |
| **Lazy** (only catch-alls)      | their **`ModSync_Data/Exclusions.jsonc`** | add the file or glob to their personal denylist |

Same idea, different granularity: the F12 menu is *admin-curated* choice (only the paths you exposed), the local `Exclusions.jsonc` is *player-arbitrary* choice (any file, no admin setup needed). The local file always works and layers on top, so a curated setup can still use it for one-off overrides — but you rarely need both.

> [!TIP]
> If your players use the **F12 menu**, they do **not** also need to list those mods in `Exclusions.jsonc` — the toggle already handles it. Reaching for both levers at once is the single most common source of confusion.

## Headless is different: a recipe, not a menu

A Fika **headless** client has no F12 menu, so it can't use opt-in toggles. Instead you set its contents centrally with [`headlessIncludes`](#headlessincludes) — an allowlist of the plugins headless should receive. Two consequences worth knowing:

- **Optional (`enabled: false`) paths never reach headless.** With no menu to tick, a headless client skips them. If headless *must* have a mod, either keep it on the (enabled) catch-all and list it in `headlessIncludes`, or `enforce` it — don't mark a headless-needed mod optional.
- **Headless receives every config and patcher** regardless of `headlessIncludes` (which only gates plugins). Harmless — see the note under [`headlessIncludes`](#headlessincludes).

## Where to go next

- [`syncPaths`](#syncpaths) — define what's synced and how (this is the dial).
- [`exclusions`](#exclusions) — the server-wide "never sync this" list (applies to everyone).
- [`headlessIncludes`](#headlessincludes) — the headless recipe.
- [Client `Exclusions.jsonc`](#exclusionsjsonc) — the player's personal denylist.

# Server

The serverside is really the heart of ModSync configuration, here you can pretty extensively customize your server's syncing behavior.
The main place you'll look to configure the server is the ModSync mod folder inside your server
directory, which is named differently depending on which SPT line you run:

| SPT line | ModSync version | Server folder | Config file |
| --- | --- | --- | --- |
| **4.0.x** | v0.12.x | `SPT/` | `SPT/user/mods/Corter-ModSync/config.jsonc` |
| **4.1.x** | v0.13.0+ | `SPT_Runtime/` | `SPT_Runtime/user/mods/Corter-ModSync/config.jsonc` |

> [!NOTE]
> **SPT 4 path layout.** The SPT 4 server runs from a subfolder of the game root, not the game root
> itself — `<gameRoot>/SPT/` on 4.0.x, renamed to `<gameRoot>/SPT_Runtime/` in 4.1.
> This means paths to client-side files (BepInEx, Managed, etc.) must use `../` to step up to the game root.
> `user/mods/` and `ModSync.Updater.exe` are exceptions — they live under the server folder and need no prefix.
> SPT 3 used plain `BepInEx/...` because the server ran at the game root.
>
> **The rename does not change your config.** Every path in `config.jsonc` is written relative to the
> server folder, so `../BepInEx/plugins` means the same thing on both lines. A 4.0 config carries over
> to 4.1 untouched — only the folder you find the file in differs.

## `syncPaths`

The `syncPaths` section is where you'll configure what files to sync and how they should be synced. Below, you'll find the default `syncPath` value
and a breakdown of what each option does.

```jsonc
{
	"syncPaths": [
		"../BepInEx/plugins",
		"../BepInEx/patchers",
		"../BepInEx/config"
	],
    // ...
}
```

> [!NOTE]
> **Why no `user/mods/`?** Server-side mods stay on the server. Their client-facing components ship as separate BepInEx plugins, which DO sync via the three folders above.

> [!NOTE]
> <details>
> <summary>There are two hardcoded syncPaths built into the server mod. They are defined as follows</summary>
>
> ```json
> {
> 	"enabled": true,
> 	"enforced": true,
> 	"silent": true,
> 	"restartRequired": false,
> 	"path": "../ModSync.Updater.exe",
> 	"name": "(Builtin) ModSync Updater"
> },
> {
> 	"enabled": true,
> 	"enforced": true,
> 	"silent": true,
> 	"restartRequired": true,
> 	"path": "../BepInEx/plugins/Corter-ModSync",
> 	"name": "(Builtin) ModSync Plugin"
> }
> ```
>
> </details>

### Options

If all you want is to add a new path and keep the [default sync behavior](./How-Sync-Works), you can simply specify the path as a string.

```jsonc
// Adding a new sync path
{
    "syncPaths": [
        // ...
        "../BepInEx/plugins"
    ]
}
```

For more control over *how* files are synced, you can specify an object for the path. Any of the following options can be specified to customize
behavior, or they can be omitted to inherit the default value.

- `path` (string, required) - What path will be synced by this entry
  - Path must be relative to the SPT server root (`<gameRoot>/SPT/` on 4.0.x, `<gameRoot>/SPT_Runtime/` on 4.1.x). BepInEx paths therefore start with `../BepInEx/` on both.
  - These paths can be to folders, recursively including all children, or individual files.
  - [Exclusions](#exclusions) can be overridden by more specific syncPaths (ie. An exclusion of `../BepInEx/plugins/Hollywood*` can be overridden by adding `../BepInEx/plugins/HollywoodGraphics` explicitly as a syncPath)
  - Globs are **not** permitted
- `name` (string, optional) Default: value of path - Name shown to clients in F12 sync menu
  - Allows admins to specify a more user-friendly name for syncPaths such as the name of the mod or things like "client-side mods", "server mods (self-hosting only)".
- `enabled` (boolean, optional) Default: `true` - Will clients sync this path by default
  - Clients are able to toggle all non-enforced syncPaths individually on their end, this option sets the default state for a given entry
  - `"enabled": true` makes a syncPath opt-out, while a value of `false` makes it opt-in.
  - *A value of false does not prevent syncing, it just requires users to check a box before syncing will occur*
- `enforced` (boolean, optional) Default: `false` - Will clients be forced to match the server exactly
  - This [changes sync behavior](./How-Sync-Works) so that clients must strictly match the files present on the server
  - Files that are modified or deleted by the client will be redownloaded and files added by clients will be removed
  - Enforced syncPaths will override any client side exclusions users have set
    > Enforced paths can cause issues when files are generated client-side or excluded server-side. Ensure that all files a client
    > needs for mods in enforced paths are either present on the server or added to the [exclusions](#exclusions) list!
    >
  - **Never enforce a folder that holds a file you exclude from players** (e.g. `../BepInEx/plugins/Fika`, which contains `Fika.Headless.dll`) — see the warning below.
- `restartRequired` (boolean, optional) Default: `true` - Will clients have to restart their game after updating these files
  - Some files, like server mods, do not require a restart when syncing to the client
  - If a user is attempting to sync an update with any files marked `"restartRequired": true` will be required to restart
    > Files synced with `"restartRequired": true` are downloaded directly into the client's SPT folder, so make sure they won't be
    > in use when the update is applied, otherwise it ***will*** fail.
    >
- `silent` (boolean, optional) Default: `false` - Will clients receive a prompt when this path is updated
  - When this option is `true` and updates are available they will be automatically applied in the background as the game loads
  - If a user is attempting to sync an update with any files marked `"silent": false` a prompt will be shown with all changes visible
  - This option plays well with `"restartRequired": false` and updates will be applied in the background while the game loads as usual
- `headless` (boolean, optional) Default: `true` - Will this path be offered to Fika headless clients
  - `"headless": false` withholds the path from headless entirely — it is never advertised, never hashed, never sent
  - Use it for anything a headless has no use for: graphics mods, map overlays, UI tweaks. A headless renders nothing, so these are pure disk and bandwidth cost, and some (like replacement graphics runtimes) can actively break it
  - This is independent of [`headlessIncludes`](#headlessincludes), which only allowlists plugins. `headless: false` wins over everything
  - A path marked `headless: false` still **claims** its files, so a catch-all can't quietly re-serve them to headless by the back door
- `baseFiles` (array of strings, optional) Default: `[]` - Base-game files this mod replaces
  - See [Mods that replace base-game files](#mods-that-replace-base-game-files) below — read it before using this

> [!WARNING]
> **Never enforce the `../BepInEx/plugins/Fika` folder** (or any folder that contains a file you exclude from players).
> `enforced` makes a client an exact mirror of the server and deletes anything the client has that the server's list
> doesn't. On a **headless** client, enforcing a folder re-applies the server `exclusions` to it — which hides
> `Fika.Headless.dll` from the list — so ModSync then deletes the headless's own `Fika.Headless.dll` on every sync,
> breaking the headless. Leave Fika on a **non-enforced** entry (e.g. the `../BepInEx/plugins` catch-all): there the
> [`headlessIncludes`](#headlessincludes) allowlist delivers `Fika.Headless.dll` to headless while `exclusions` keeps it
> from players, and it is never deleted. Enforce **individual mods** that every client must match — not a whole folder
> holding a headless-only or player-excluded file.

> [!TIP]
> Sync Paths are processed from most specific to most general, so its possible to override settings for some
> files in a directory. For instance, to enforce only a subset of `../BepInEx/config`.
>
> **Recommended pattern:** keep the broad folders (`../BepInEx/plugins`, `../BepInEx/patchers`, `../BepInEx/config`)
> as plain **non-enforced** catch-alls that set the default rules, then add specific entries only for the mods you
> want to lock down. Every file belongs to exactly one syncPath — the most-specific match — so anything you don't
> name explicitly falls back to the catch-all. Removing the catch-alls means every mod must be listed individually
> and there is no safe default (this is the usual cause of a headless deleting `Fika.Headless.dll` — see the
> warning under [`enforced`](#options) above).

### Examples

Worked configurations — from the minimal setup to a real server's — **[Usage Examples](UsageExamples)**.

## Mods that replace base-game files

Most mods only add files. A few **replace files that ship with the game** — DLSS swaps the NVIDIA runtime DLL, DynamicMaps swaps two Unity assemblies. These need `baseFiles`, because the mod's own folder is only half of it: without the replaced base file the mod does nothing, and a player who opts out is left with a modified game.

List the base-game files the mod overwrites and ModSync handles both halves as one unit:

```jsonc
{
    "path": "../BepInEx/patchers/TarkovDLSS45",
    "name": "(Optional) Tarkov DLSS 4.5",
    "enabled": false,
    "headless": false,
    "baseFiles": [
        "../EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"
    ]
}
```

DynamicMaps replaces two Unity assemblies instead of one DLL — same shape, more entries:

```jsonc
{
    "path": "../BepInEx/plugins/DynamicMaps",
    "name": "(Optional) Dynamic Maps",
    "enabled": false,
    "headless": false,
    "baseFiles": [
        "../EscapeFromTarkov_Data/Managed/Unity.VectorGraphics.dll",
        "../EscapeFromTarkov_Data/Managed/Unity.InternalAPIEngineBridge.003.dll"
    ]
}
```

> [!WARNING]
> **DynamicMaps is the riskier of the two.** The DLSS runtime DLL can be re-downloaded from NVIDIA if a player loses it. DynamicMaps' Unity assemblies **cannot** — they ship inside the game and aren't available separately, so a player who ends up with the modified copies and no backup has no way to get the originals except verifying or reinstalling their game files. This is exactly why ModSync refuses to remove a hand-installed copy (see below) rather than guessing.

**What ModSync does with them:**

1. **On install** — before overwriting a base file, it copies the player's original alongside it as `<file>.modsync-bak`.
2. **On removal** — it restores that `.modsync-bak` over the replacement and deletes the backup, putting the game back exactly as it was.

Because the original is preserved, opting out of a `baseFiles` mod is safe and reversible.

> [!NOTE]
> **Mods that ADD files rather than replace them (v0.13.0+, SPT 4.1 line).**
> Not every `baseFiles` entry is a replacement. DynamicMaps ships two Unity assemblies the game does
> **not** include, so there is no original to back up.
>
> Up to and including v0.12.6, removal keyed off the backup being present *and* treated a missing
> `.modsync-bak` as proof the file belonged to the game, so it refused to delete it. Those added files
> were left behind when the mod was removed, and the mod's F12 entry locked itself so it could not be
> re-enabled either.
>
> From **v0.13.0**, removal keys off the backup alone:
>
> - a `.modsync-bak` exists → the mod **replaced** a game file → restore the original
> - no `.modsync-bak` → the mod **added** the file → remove it like any other synced file
>
> Genuine replacements such as DLSS's `nvngx_dlss.dll` are unaffected: they still get a backup and
> still restore on opt-out. If you ran 0.12.6 with an adding-type mod, the stragglers are inert and
> get cleaned up the next time that mod is un-ticked.
>
> This fix currently ships on the **4.1 line only** (v0.13.0+). The 4.0.x line (v0.12.6) still has the
> older behaviour.

> [!IMPORTANT]
> **Put `headless: false` on every `baseFiles` mod unless you are certain otherwise.** These mods replace *game* files; a headless client runs no graphics and has no use for them, and handing a headless a replacement rendering runtime is a good way to break it.

### When ModSync won't remove a mod

If a player installed one of these mods **by hand before ModSync ever saw it**, there is no `.modsync-bak` — ModSync never took the original, so it has nothing to restore. Deleting the mod would leave the player running the mod's modified base files with the mod itself gone, which typically means an infinite load or a broken client.

Rather than risk that, ModSync **locks the entry**: it appears in the F12 menu as read-only with the note *"wasn't installed by ModSync and can't be removed safely."*

To get out of that state, the player should:

1. Verify or reinstall their game files so the originals are back in place (Steam/BSG launcher file verification, or a clean SPT install).
2. Relaunch — ModSync now sees a clean baseline and the entry becomes a normal toggle it can install and remove.

> [!NOTE]
> This lock only applies to mods with `baseFiles`. Ordinary mods, which only add files, are always freely removable.

### Known issue: Tarkov DLSS 4.5 and `Graphics.ini`

Not a ModSync bug, but you will get support questions about it. The mod extends the game's DLSS presets, and a preset of **"Default"** gets written to `user/sptSettings/Graphics.ini` in a form the game cannot read back — the client then hangs on the loading screen with no error.

- **Prevention:** tell players to pick an explicit DLSS preset (anything except *Default*) **before** the mod installs.
- **Recovery:** open `user/sptSettings/Graphics.ini` and change the `DLSSPreset` line to a real preset:
  ```jsonc
  "DLSSPreset": "Default",   // ← the problem
  "DLSSPreset": "K",         // ← any explicit preset
  ```

  This keeps every other graphics setting intact. Only if that doesn't clear it, rename the whole file and let the game rebuild it — that works but discards resolution, quality and keybind-adjacent settings, so note them first.
- **Also applies on removal** — the preset stays on a mod-added value after the mod is gone, so the same edit may be needed.

## `exclusions`

The antithesis to the [`syncPaths`](#syncpaths) setting is `exclusions`. This section of the config lets you specify files not to sync.
Exclusions can be specified as a list of paths or patterns not to include. You can find the default configuration for this setting below.

```jsonc
{
    // ...
    "exclusions": [
        // SPT / BepInEx baseline — not mods. Ship with SPT (or are generated
        // per-machine by BepInEx); every client has its own, and an SPT update
        // replaces them. Never push the host's copies over a client's.
        "../BepInEx/plugins/spt",
        "../BepInEx/patchers/spt-prepatch.dll",
        "../BepInEx/config/BepInEx.cfg",
        "../BepInEx/config/com.bepis.bepinex.configurationmanager.cfg",

        // Fika headless DLL — must never reach regular players
        "../BepInEx/plugins/Fika/Fika.Headless.dll",

        // Universal per-file opt-out — drop a .nosync or .nosync.txt file
        // next to any mod folder/file to skip it
        "**/*.nosync",
        "**/*.nosync.txt",

        // Git repo metadata
        "**/.git"
    ]
}
```

> [!TIP]
> **The `.nosync` sentinel:** drop an empty `.nosync` or `.nosync.txt` file inside any mod folder to exclude that mod from sync without editing `config.jsonc`. Handy for mods that write per-machine state into their own folder — add it to the mod folder on the server and every client will skip that folder automatically.

> [!TIP]
> If you ever run into a file that changes every time you try to sync, such as a log file or a configuration,
> it can be easily added here to prevent clients being bombarded by update prompts.

> [!TIP]
> **Excluding from players but still delivering to headless:** add the path to `exclusions` AND to [`headlessIncludes`](#headlessincludes). Entries in `headlessIncludes` override `exclusions`, so headless clients still receive the file while regular players never do. This is the correct pattern for mods like RAID_REVIEW that should only run on headless:
>
> ```jsonc
> "exclusions": [
>     "../BepInEx/plugins/RAID_REVIEW.dll"  // blocked for regular players
> ],
> "headlessIncludes": [
>     "../BepInEx/plugins/RAID_REVIEW.dll"  // override: headless still receives it
> ]
> ```

> [!TIP]
> Sync Paths can override exclusions if they are more specific. For instance, if you wanted to exclude all files
> in the `../BepInEx/plugins/SAIN` directory except the plugin itself *(don't do this)*. You could add an exclusion of
> `../BepInEx/plugins/SAIN` and a sync path of `../BepInEx/plugins/SAIN/SAIN.dll`

## `headlessIncludes`

When a Fika headless client connects, ModSync serves it only the plugins explicitly listed in `headlessIncludes` (scoped to `../BepInEx/plugins` only). Patchers and config files pass through to headless unfiltered — only plugins are gated.

**An empty list means the headless client receives zero plugins.** You must populate this if you run a headless instance.

```jsonc
{
    // ...
    "headlessIncludes": [
        // Fika (Core + Headless.dll both live in this folder) and ModSync itself
        "../BepInEx/plugins/Fika",
        "../BepInEx/plugins/Corter-ModSync",

        // Bot AI — add whatever your headless needs:
        // "../BepInEx/plugins/SAIN",
        // "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
        // "../BepInEx/plugins/DrakiaXYZ-Waypoints"
    ]
}
```

Entries can target a **folder** or an **exact file**:

- `../BepInEx/plugins/SAIN` — matches the folder and everything inside it. Directory boundary is respected: `SAIN` does NOT match `SAINFoo`.
- `../BepInEx/plugins/DrakiaXYZ-BigBrain.dll` — matches just that one DLL. Sibling files in the same folder are not pulled in.

> [!NOTE]
> Entries in `headlessIncludes` override `exclusions` — if a file appears in both lists it **will** be sent to headless.
> This is intentional: it lets you keep `Fika.Headless.dll` in `exclusions` (so regular players never receive it)
> while still delivering it to headless clients via `headlessIncludes`.

> [!NOTE]
> **`enforced` paths bypass this allowlist.** An enforced mod reaches a headless client whether or not it is listed
> here — the allowlist only gates **non-enforced** plugins. So enforcing a mod is a second, implicit way to put it on
> your headless; if you don't want it there, use [`headless: false`](#options) instead, which overrides everything.

> [!WARNING]
> Do not use globs (e.g. `*.dll`) here. The allowlist exists to be deliberate about what reaches headless — a broad glob
> defeats the purpose and can accidentally re-include files you meant to exclude. An allowlist is also much shorter than
> its denylist equivalent (~20 entries vs. hundreds of cosmetic mods), and you only have to update it when headless's
> mod set changes, not when the player ecosystem moves.

> [!TIP]
> Watch the commas when you uncomment an example entry. The last active entry has **no** trailing comma, so add one to it before uncommenting the line below — otherwise you get two values with no separator and malformed JSON:
>
> ```jsonc
> "headlessIncludes": [
>     "../BepInEx/plugins/Fika",
>     "../BepInEx/plugins/Corter-ModSync"   // ← add a comma here before uncommenting below
>
>     // "../BepInEx/plugins/SAIN"
> ]
> ```

> [!NOTE]
> **Known behaviour — headless receives every mod's config, not just the ones it runs.** `headlessIncludes` only gates
> the `../BepInEx/plugins` folder; `../BepInEx/config` passes through unfiltered, so a headless pulls the config file
> for mods it doesn't even load.
>
> **Why it works this way:** config files are flat files named by the plugin's **GUID** (e.g. `me.sol.sain.cfg`), not by
> its folder name (`SAIN`), so there's no reliable way to match a config back to a plugin the way a folder can be matched.
> It's also the safe default — an unused config is a few harmless KB, and it means a headless *does* pick up any config
> change you make on the server for the mods it actually runs.
>
> **If you really don't want a specific config on headless**, exclude it per-file: drop a `.nosync` next to it, or add its
> path to [`exclusions`](#exclusions). Worth it for one noisy/large file — not for trimming configs wholesale.

## `managedIncludes`

Some mods ship Unity assemblies that must be installed into `EscapeFromTarkov_Data/Managed/` on every player client. List the **filenames** (not full paths) of those DLLs here.

```jsonc
{
    // ...
    "managedIncludes": [
        // Example — DynamicMaps ships these Unity assemblies. Replace with your own:
        // "Unity.VectorGraphics.dll",
        // "Unity.InternalAPIEngineBridge.003.dll"
    ]
}
```

The server reads from `../EscapeFromTarkov_Data/Managed/` but only serves files you list here. This is intentionally restrictive:

- **Docker/Linux server:** that folder is a staging area containing only the mod DLLs you placed there — nothing vanilla.
- **Windows host-is-also-a-player:** that folder contains the full EFT install (169+ Unity DLLs). Without the allowlist, vanilla Unity DLLs would be synced to all clients.

**Backup behaviour:**

- On install: if the file already exists on the client, the original is backed up as `<filename>.modsync-bak` before being replaced.
- On removal (entry deleted from config, server restarted): if a `.modsync-bak` exists, the original is restored automatically. If no backup was made (the file was new — not present in vanilla), it is deleted.

## `headlessManagedIncludes`

The headless equivalent of [`managedIncludes`](#managedincludes). Headless runs the game simulation without the rendering stack, so Managed assemblies are almost never needed there. Leave this empty unless a mod's install instructions specifically say it is required on headless.

```jsonc
{
    // ...
    "headlessManagedIncludes": []
}
```

## Setting up a Fika headless install

1. **On the server**, populate `headlessIncludes` in `config.jsonc` with the plugins your headless needs. Below is a full working example — adjust to your own mod set:

   ```jsonc
   "headlessIncludes": [
       // Fika (Core + Headless.dll both live in this folder) and ModSync
       "../BepInEx/plugins/Fika",
       "../BepInEx/plugins/Corter-ModSync",

       // Bot AI + behavior
       "../BepInEx/plugins/SAIN",
       "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
       "../BepInEx/plugins/DrakiaXYZ-Waypoints",

       // Bot gameplay tweaks
       "../BepInEx/plugins/DontShootTheBus.dll",
       "../BepInEx/plugins/NerfBotGrenades.dll",
       "../BepInEx/plugins/Shibdib.SniperBros.dll",

       // Misc gameplay + raid content
       "../BepInEx/plugins/acidphantasm-temporaryfixes",
       "../BepInEx/plugins/BlackDiv",
       "../BepInEx/plugins/MergeConsumables",
       "../BepInEx/plugins/RUAFComeHome",
       "../BepInEx/plugins/tacticaltoaster-untargohome",
       "../BepInEx/plugins/Terkoiz.FlareEventNotifier.dll",
       "../BepInEx/plugins/UseItemsFromAnywhere.dll",

       // Bundle / CRC loader helpers (raid load path)
       "../BepInEx/plugins/s8_SPT_LoadBundleEvenFaster",
       "../BepInEx/plugins/s8_SPT_PatchCRC32",

       // Networking / interop libs
       "../BepInEx/plugins/Tyfon.UIFixes.dll",
       "../BepInEx/plugins/Tyfon.UIFixes.Net.dll",

       // Worn cosmetics visible to other players in raid
       "../BepInEx/plugins/7Bpencil.WeaponCamoAndStickers",
       "../BepInEx/plugins/acidphantasm-armbandsforall",

       // WTT content libs
       "../BepInEx/plugins/WTT-ArmoryClient",
       "../BepInEx/plugins/WTT-ClientCommonLib",
       "../BepInEx/plugins/WTT-ContentBackportClient",
       "../BepInEx/plugins/WTT-PackNStrap"
   ]
   ```
2. **On the headless install** — nothing to configure. It detects `Fika.Headless.dll` at startup, sends `?headless=1` to the server, and gets only the allowlisted plugins back. `Exclusions.jsonc` stays at its empty default.

When in doubt about whether a particular mod is safe on headless, check the [Fika wiki](https://github.com/project-fika/Wiki) or ask in the Fika Discord.

# Client

Clients are also given a few powerful tools for customizing how sync works for them. Plenty of clients will want to customize their config with tweaks to all sorts of client-side plugins.

> <small>Be sure to familiarize yourself with  if you run into any edge cases, but by and large the following tools are all you should need to make sure you can keep all your tricked out configs as a client.</small>

## BepInEx Configuration Manager

If you are playing SPT with any mods, you are likely already familiar with the BepInEx Configuration Manager. ModSync makes use of this excellent system to expose client-side configuration. Clients can access the menu with `F12` by default and scroll to where they see ModSync in the list.

### Options

- `Delete Removed Files` (boolean) Default: `true` - Will files that get deleted on the server be automatically removed
  - Enforced sync paths will have their files removed regardless of what the client sets this to
  - To understand how removed files are determined, review [how syncing works](./How-Sync-Works)
- `Synced Paths` (list) - What syncPaths from the server will the client sync
  - Clients can toggle on and off any non-enforced paths
  - This might be good if server admins want to allow clients to download optional mods only when they want them

## Exclusions.jsonc

Lives at `<game>/ModSync_Data/Exclusions.jsonc` on each install. Created on first run with an empty array and a comment header explaining the format.

**One-line mental model:** *"files I don't want ModSync to install or keep installed on this machine."*

### Behavior

For each path or glob you list:

| How the file got onto this install                    | What happens on next sync                                  |
| ----------------------------------------------------- | ---------------------------------------------------------- |
| ModSync installed it on a previous sync               | **Deleted** locally                                  |
| You never had it                                      | Stays absent — not downloaded                             |
| You copied it in by hand (ModSync never installed it) | **Stays** — ModSync only touches files it installed |

This is an **uninstall + don't-reinstall** list, not a "freeze locally" list. Add something you currently have, and ModSync will remove it on the next sync (assuming ModSync had installed it in the first place).

### Player vs headless usage

- **Player install:** use this for personal opt-outs — visual mods you don't want, hotkey mods that conflict with yours, etc. Typically just a handful of entries per player.
- **Headless install:** **usually empty.** The server's `headlessIncludes` allowlist already controls what reaches headless. Use this file only for per-headless overrides on top of the allowlist (rare).

### Trimming ModSync's own components (optional)

ModSync ships three of its own files: the plugin, the desktop **Updater** (`ModSync.Updater.exe`), and the headless **patcher** (`BepInEx/patchers/Corter-ModSync-Prepatch.dll`). By default all three sync to every client — which is harmless, but the Updater is dead weight on headless and the patcher is dead weight on players. If you want leaner installs, you can trim the one each side never runs:

| To remove…                                           | Add this to that install's`Exclusions.jsonc`   |
| ----------------------------------------------------- | ------------------------------------------------ |
| The **Updater** from a **headless** client | `ModSync.Updater.exe`                          |
| The **patcher** from a **player** client   | `BepInEx/patchers/Corter-ModSync-Prepatch.dll` |

ModSync only honours these on the side that doesn't need the file. Each component stays **enforced** on the side that *does*: a player can't exclude the Updater, and a headless can't exclude the patcher — the exclusion is silently ignored so you can't break your own apply mechanism. The plugin itself is always enforced everywhere. Leaving all three in place is completely fine; this is purely for a minimal install.

### Edits aren't live

`Exclusions.jsonc` is read once at game startup. Mid-game edits do nothing until you restart EFT.

### Format

```jsonc
[
    // Examples — uncomment / edit as needed:
    "BepInEx/plugins/NoInsurance.dll",
    "BepInEx/plugins/HollywoodGraphics/**",
    "BepInEx/config/com.author.somemod.cfg"
]
```

Entries can be:

- **An exact file path** — `BepInEx/plugins/SomeMod.dll`
- **A folder path** — `BepInEx/plugins/DynamicMaps` matches the folder and everything inside it
- **A glob** — `BepInEx/plugins/DynamicMaps/**`, `BepInEx/config/*.cfg`

Paths are written game-root-relative with forward slashes.

### Safety hatches

- **`enforced` syncpaths bypass local exclusions.** If the admin marked a syncpath as `enforced: true`, the client can't opt out of files inside it. This keeps `Corter-ModSync.dll` itself uninstall-proof.
- **Manually-installed files are immune.** ModSync only deletes files it recognises from a previous sync (tracked in `ModSync_Data/PreviousSync.json`). If you hand-dropped a file into BepInEx, ModSync won't touch it.
- **The "X files to remove" confirm appears before anything deletes.** If a bad glob is about to delete 20 things, click Cancel and fix the glob.

> [!TIP]
> If you were using the older `Exclusions.json` (from v0.12.0 or earlier), ModSync will automatically migrate it to
> `Exclusions.jsonc` on first run. You don't need to do anything manually.

# Where does my mod go?

1. **Server-only mod** (lives in `user/mods/`, no BepInEx component)?
   → Don't list anywhere. ModSync never syncs `user/mods/`.
2. **BepInEx mod every player needs?**
   → It syncs automatically via `syncPaths`. No config needed.
3. **BepInEx mod the headless needs to run raids properly** (bot AI, pathfinding, networking, server-driven gameplay)?
   → Add to `headlessIncludes` on the server.
4. **BepInEx mod that's purely client-facing** (HUD, UI overlays, visual effects, item info, hotkeys)?
   → Don't list anywhere — players get it by default, headless skips it because it's not in `headlessIncludes`.
5. **Mod that ships Unity assemblies into `EscapeFromTarkov_Data/Managed/`?**
   → Add the DLL filenames to `managedIncludes`. Place the DLL files in `../EscapeFromTarkov_Data/Managed/` on the server (a staging folder on Docker, or the existing EFT folder on Windows — the allowlist keeps vanilla files out either way).
6. **A particular player doesn't want a particular mod?**
   → That player adds it to their own `Exclusions.jsonc`.
7. **`Fika.Headless.dll`** → already in `exclusions` by default. Don't add anywhere else.

# Reference: Fika

Fika is the multiplayer layer ModSync is designed to coexist with.

- **Wiki:** https://github.com/project-fika/Wiki
- **Headless docs:** see the Wiki's "Headless" section for the canonical list of which mods are known to work / break on a headless instance.
- **Fika-Server-CSharp:** https://github.com/project-fika/Fika-Server-CSharp
