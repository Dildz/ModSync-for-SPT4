# Corter-ModSync — Configuration Guide (SPT 4.0.x)

ModSync has two configuration surfaces:

1. **Server** `config.jsonc` — what the server walks, what to never sync,
   what reaches a Fika headless install, and which Unity Managed DLLs to push.
2. **Each install's** `<game>/ModSync_Data/Exclusions.jsonc` — personal
   per-install opt-outs (player or headless).

Both files are created on first run with sensible defaults and a comment
header you can hand-edit without consulting docs.

---

## Server config (`config.jsonc`)

Lives at `SPT/user/mods/Corter-ModSync/config.jsonc`. Five top-level keys:

```jsonc
{
    "syncPaths":              [ ... ],   // folders to walk and serve
    "exclusions":             [ ... ],   // universal denylist (every client)
    "headlessIncludes":       [ ... ],   // plugins allowlist for Fika headless
    "managedIncludes":        [ ... ],   // EscapeFromTarkov_Data/Managed allowlist (players)
    "headlessManagedIncludes":[ ... ]    // same, for Fika headless
}
```

### `syncPaths`

The folders ModSync walks and serves. Just the three BepInEx folders:

```jsonc
"syncPaths": [
    "../BepInEx/plugins",
    "../BepInEx/patchers",
    "../BepInEx/config"
]
```

> **Why no `user/mods/`?** Server-side mods stay on the server. Their
> client-facing components ship as separate BepInEx plugins, which DO sync
> via the three folders above.

> **Why `../`?** SPT 4 starts its server from `<game>/SPT/`. To reach the
> game-root `BepInEx/` folder, the server goes up one level. SPT 3 used plain
> `BepInEx/...` because the server ran at the game root.

Entries can be plain strings or objects when you need to override defaults:

```jsonc
{
    "path": "../BepInEx/plugins",
    "name": "BepInEx Plugins",   // display name in the client UI
    "enabled": true,             // false = client skips this folder entirely
    "enforced": false,           // true = client can't opt out of this folder
    "silent": false,             // true = no UI prompt before syncing
    "restartRequired": true      // true = client restart needed after update
}
```

The string form is shorthand for an object with all defaults.

### `exclusions`

Universal denylist — applied to **every** client, player and headless alike.
For SPT internals, files that should never be synced, and the universal
`.nosync` sentinel pattern.

Default contents (written on first run):

```jsonc
"exclusions": [
    "../BepInEx/plugins/spt",              // SPT installer DLLs
    "../BepInEx/patchers/spt-prepatch.dll",
    "../BepInEx/plugins/Fika/Fika.Headless.dll",  // never give players the headless DLL
    "**/*.nosync",                         // per-file/folder opt-out sentinel
    "**/*.nosync.txt",
    "**/.git"                              // git-cloned mod metadata
]
```

**The `.nosync` sentinel**: drop an empty `.nosync` or `.nosync.txt` file
inside any mod folder to exclude that mod from sync without editing this
file. Handy for mods that write per-machine state into their own folder.

### `headlessIncludes`

**Allowlist** scoped to `../BepInEx/plugins` only. A Fika headless client
gets ONLY the plugin paths matching an entry here. Outside of `plugins/`
(patchers, config) headless gets the same files a player would, filtered
only by the universal `exclusions` above.

**Empty array is the default — and means headless gets ZERO plugins.**
If you run a headless instance, you MUST populate this list.

Entries should be **explicit** — either a mod folder or an individual DLL.
**Don't use globs here.** The whole point of an allowlist is being deliberate
about what reaches headless; a glob like `*.dll` can accidentally
re-include everything you meant to keep out.

- **A folder**: `../BepInEx/plugins/SAIN` matches the folder and everything
  inside it. Directory boundary check applies: `SAIN` does NOT match `SAINFoo`.
- **An exact DLL**: `../BepInEx/plugins/DrakiaXYZ-BigBrain.dll` matches just
  that one file. Sibling files in the same folder are NOT pulled in.

> **Why an allowlist (not a denylist) for headless?** Headless has no human
> at the keyboard — most UI/HUD/visual mods would crash it or waste disk.
> An allowlist is much shorter than its denylist equivalent (~20 entries vs.
> hundreds of cosmetic mods), and you only have to update it when *headless's*
> mod set changes, not when the player ecosystem moves.

> **Trailing commas:** ModSync's config parser accepts trailing commas (the
> `.jsonc` format). It is good practice to leave a trailing comma on the last
> active entry so that uncommenting or adding a new line below it doesn't
> accidentally produce malformed JSON:
> ```jsonc
> "headlessIncludes": [
>     "../BepInEx/plugins/Fika",
>     "../BepInEx/plugins/Corter-ModSync",   // ← trailing comma is fine here
>
>     // "../BepInEx/plugins/SAIN",           // safe to uncomment — comma already present above
> ]
> ```

### `managedIncludes`

**Allowlist** for files inside `EscapeFromTarkov_Data/Managed/`. Some mods
ship Unity assemblies that must be placed in that folder on every player
client (e.g. DynamicMaps). List the **filenames only** — no folder path.

The server reads from `../EscapeFromTarkov_Data/Managed/` but only serves
the files you explicitly list here. This makes it safe in both setups:

- **Docker/Linux server:** that folder is a staging area containing only
  the mod DLLs you placed there — nothing vanilla.
- **Windows (host is also a player):** that folder contains the full EFT
  install (169+ Unity DLLs). The allowlist prevents vanilla files from
  being synced to other clients.

**Client backup behaviour:**
- **On install:** if the file already exists on the client, it is backed up
  as `<filename>.modsync-bak` before being replaced.
- **On removal** (entry deleted from config, server restarted): if a
  `.modsync-bak` exists, the original is restored automatically. If no
  backup was made (the file was new — not present in vanilla), it is deleted.

```jsonc
// DynamicMaps example:
"managedIncludes": [
    "Unity.VectorGraphics.dll",
    "Unity.InternalAPIEngineBridge.003.dll"
]
```

### `headlessManagedIncludes`

Same allowlist as `managedIncludes` but applied only to Fika headless
clients. **Default empty** — headless runs the game simulation without the
rendering stack, so Managed DLLs are almost never needed there. Only add
entries if a mod's install instructions specifically require it on headless.

---

## Per-install config (`ModSync_Data/Exclusions.jsonc`)

Lives at `<game>/ModSync_Data/Exclusions.jsonc` on each install. Created on
first run with an empty array and a comment header explaining the format.

**One-line mental model:** *"files I don't want ModSync to install or keep
installed on this machine."*

### Behavior

For each path or glob you list:

| How the file got onto this install | What happens on next sync |
| --- | --- |
| ModSync installed it on a previous sync | **Deleted** locally |
| You never had it | Stays absent — not downloaded |
| You copied it in by hand (ModSync never installed it) | **Stays** — ModSync only touches files it installed |

Translation: this is an **uninstall + don't-reinstall** list, not a "freeze
locally" list. Add something you currently have, and ModSync will remove it
on the next sync (assuming ModSync had installed it in the first place).

### Player vs headless usage

- **Player install:** use this for personal opt-outs — visual mods you don't
  want, hotkey mods that conflict with yours, etc. Typically just a handful
  of entries per player.
- **Headless install:** **usually empty.** The server's `headlessIncludes`
  allowlist already controls what reaches headless. Use this file only for
  per-headless overrides on top of the allowlist (rare).

### Edits aren't live

`Exclusions.jsonc` is read once at game startup. Mid-game edits do nothing
until you restart EFT.

### Format

```jsonc
// Examples — uncomment / edit as needed
[
    "BepInEx/plugins/AmandsGraphics.dll",
    "BepInEx/plugins/DynamicMaps/**",
    "BepInEx/config/com.author.somemod.cfg"
]
```

Entries can be:
- **An exact file path** — `BepInEx/plugins/SomeMod.dll`
- **A folder path** — `BepInEx/plugins/DynamicMaps` matches the folder and
  everything inside it
- **A glob** — `BepInEx/plugins/DynamicMaps/**`, `BepInEx/config/*.cfg`

Paths are written game-root-relative with forward slashes.

### Safety hatches

- **`enforced` syncpaths bypass local exclusions.** If the admin marked a
  syncpath as `enforced: true`, the client can't opt out of files inside
  it. This keeps `Corter-ModSync.dll` itself uninstall-proof.
- **Manually-installed files are immune.** ModSync only deletes files it
  recognises from a previous sync (tracked in `ModSync_Data/PreviousSync.json`).
  If you hand-dropped a file into BepInEx, ModSync won't touch it.
- **The "X files to remove" confirm appears before anything deletes.** If a
  bad glob is about to delete 20 things, click Cancel and fix the glob.

---

## Setting up a Fika headless install

1. **On the server**, populate `headlessIncludes` in `config.jsonc` with the
   plugins your headless needs. Below is a full working example — adjust to
   your own mod set:

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

2. **On the headless install** — nothing to configure. It detects
   `Fika.Headless.dll` at startup, sends `?headless=1` to the server, and
   gets only the allowlisted plugins back. `Exclusions.jsonc` stays at its
   empty default.

When in doubt about whether a particular mod is safe on headless, check the
[Fika wiki's Headless section](https://github.com/project-fika/Wiki) or ask
in the Fika Discord.

---

## "Where does my mod go?" decision tree

1. **Server-only mod** (lives in `user/mods/`, no BepInEx component)?
   → Don't list anywhere. ModSync never syncs `user/mods/`.

2. **BepInEx mod every player needs?**
   → It syncs automatically via `syncPaths`. No config needed.

3. **BepInEx mod the headless needs to run raids properly** (bot AI,
   pathfinding, networking, server-driven gameplay)?
   → Add to `headlessIncludes` on the server.

4. **BepInEx mod that's purely client-facing** (HUD, UI overlays, visual
   effects, item info, hotkeys)?
   → Don't list anywhere — players get it by default, headless skips it
   because it's not in `headlessIncludes`.

5. **Mod that ships Unity assemblies into `EscapeFromTarkov_Data/Managed/`?**
   → Add the DLL filenames to `managedIncludes`. Place the DLL files in
   `../EscapeFromTarkov_Data/Managed/` on the server (a staging folder on
   Docker, or the existing EFT folder on Windows — the allowlist keeps
   vanilla files out either way).

6. **A particular player doesn't want a particular mod?**
   → That player adds it to their own `Exclusions.jsonc`.

7. **`Fika.Headless.dll`** → already in `exclusions` by default. Don't add
   anywhere else.

---

## Reference: Fika

Fika is the multiplayer layer ModSync is designed to coexist with.

- **Wiki:** https://github.com/project-fika/Wiki
- **Headless docs:** see the Wiki's "Headless" section for the canonical
  list of which mods are known to work / break on a headless instance.
- **Fika-Server-CSharp:** https://github.com/project-fika/Fika-Server-CSharp
