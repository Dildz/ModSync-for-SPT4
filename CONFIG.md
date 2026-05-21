# Corter-ModSync — Configuration Guide (SPT 4.0.x)

This document explains how to set up `config.jsonc` for an SPT 4 server, plus a
starter mod-routing list you can paste in for a typical Fika headless setup.

The on-disk config lives at `SPT/user/mods/Corter-ModSync/config.jsonc` and is
written with defaults the first time the server starts.

---

## Schema at a glance

```jsonc
{
    "syncPaths":        [ ... ],   // folders to walk
    "globalExclusions": [ ... ],   // universal denylist
    "clientExclusions": [ ... ],   // player-only denylist
    "headlessIncludes": [ ... ]    // headless-only allowlist (BepInEx/plugins scope)
}
```

Four top-level keys, that's it. Each is explained below.

---

## `syncPaths`

The folders ModSync walks and serves to clients. Just the three BepInEx folders:

```jsonc
"syncPaths": [
    "../BepInEx/plugins",
    "../BepInEx/patchers",
    "../BepInEx/config"
]
```

> **Why no `user/mods/`?** Server-side mods stay on the server. Their
> client-facing components ship as separate BepInEx plugins which DO sync via
> the three folders above.

> **Why `../`?** SPT 4 starts its server from `<game>/SPT/`. To reach the
> game-root `BepInEx/` folder, the server needs to go up one level. SPT 3 used
> plain `BepInEx/...` because the server ran at the game root.

Entries can be plain strings or objects if you need to override defaults:

```jsonc
{
    "path": "../BepInEx/plugins",
    "name": "BepInEx Plugins",   // display name in the client UI
    "enabled": true,             // false = client skips this folder entirely
    "enforced": false,           // true = client can't opt out
    "silent": false,             // true = no UI prompt before syncing
    "restartRequired": true      // true = client restart needed after update
}
```

The string form `"../BepInEx/plugins"` is shorthand for an object with all
defaults. Object form gives you the knobs.

---

## `globalExclusions`

Skipped for **every** sync, regardless of client kind. Use for SPT internals
and universal opt-out patterns.

```jsonc
"globalExclusions": [
    "../BepInEx/plugins/spt",            // SPT installer DLLs, not "mods"
    "../BepInEx/patchers/spt-prepatch.dll",
    "**/*.nosync",                       // per-file opt-out
    "**/*.nosync.txt",
    "**/.git"                            // git-cloned mod metadata
]
```

**The `.nosync` mechanism**: drop an empty `.nosync` file (or `.nosync.txt`)
next to any mod file or folder to exclude it from sync without editing this
file. Handy for per-machine state files some mods write into their own folder.

---

## `clientExclusions`

Skipped only when a **regular player** client syncs. Headless clients ignore
this list.

**Default:**
```jsonc
"clientExclusions": [
    "../BepInEx/plugins/Fika/Fika.Headless.dll"
]
```

> **Most setups leave this at the default.** There's no harm in syncing every
> BepInEx mod to all players — even mods that headless also uses. If a player
> ever wants to host their own raid (instead of having headless host), they'll
> need all the same plugins headless needs (bot AI, pathfinding, etc.).
>
> The only case where you'd add entries here: a strict "headless is the only
> raid host" setup where you want to keep player installs lean. Then you'd add
> mods like `SAIN`, `DrakiaXYZ-BigBrain.dll`, `DrakiaXYZ-Waypoints` to keep
> them off players. Be aware: doing this commits players to never hosting.

---

## `headlessIncludes`

**Allowlist** for `BepInEx/plugins`. A Fika headless client gets ONLY the paths
listed here from `plugins/` — nothing else. Outside of `plugins/`, headless
gets the same files a player would (patchers and config are not filtered by
this allowlist).

```jsonc
"headlessIncludes": [
    "../BepInEx/plugins/Fika",
    "../BepInEx/plugins/SAIN",
    "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
    "../BepInEx/plugins/DrakiaXYZ-Waypoints",
    "../BepInEx/plugins/QuestingBots"
]
```

Each entry can be:
- **A folder** (`../BepInEx/plugins/SAIN`) — matches that folder and every file
  inside it. Directory boundary check applies: `SAIN` does NOT match `SAINFoo`.
- **An exact file** (`../BepInEx/plugins/Fika/Fika.Headless.dll`) — matches
  just that one file. Sibling files in the same folder are NOT pulled in.

> **Why an allowlist (not a denylist) for headless?** Headless is a
> specialized instance with no human at the keyboard. Most UI/HUD/visual mods
> would either crash it or just waste disk space. The Fika project documents
> that some mods actively break a headless instance, so the safer default is
> "give it nothing, list what's needed."

---

## "Where does my mod go?" heuristics

1. **Is it a server-only mod** (lives in `user/mods/`, no BepInEx component)?
   → Don't list it anywhere. ModSync never syncs `user/mods/`.

2. **Is it a BepInEx mod the headless needs to run raids properly**
   (bot AI, pathfinding, networking, server-driven gameplay)?
   → Add to `headlessIncludes`.

3. **Is it a BepInEx mod that's purely client-facing**
   (HUD, UI overlays, visual effects, item info, hotkeys)?
   → Don't list it anywhere — players get it by default, headless skips it
   because it's not in `headlessIncludes`.

4. **Is it `Fika.Headless.dll`**?
   → Already in `clientExclusions` by default. Don't add anywhere else.

5. **Lean-player-install setup** (rare): players never host raids, you want
   smaller player installs?
   → Mirror the parts of `headlessIncludes` that players don't need into
   `clientExclusions`. Most setups DON'T need this.

---

## Starter `config.jsonc` for a Fika headless setup

Paste this in and trim to taste. Mod names are based on a real-world install;
yours will differ.

```jsonc
{
    "syncPaths": [
        "../BepInEx/plugins",
        "../BepInEx/patchers",
        "../BepInEx/config"
    ],

    "globalExclusions": [
        "../BepInEx/plugins/spt",
        "../BepInEx/patchers/spt-prepatch.dll",
        "**/*.nosync",
        "**/*.nosync.txt",
        "**/.git"
    ],

    // Most setups leave this at just Fika.Headless.dll. See CONFIG.md for the
    // rare "lean player install" pattern.
    "clientExclusions": [
        "../BepInEx/plugins/Fika/Fika.Headless.dll"
    ],

    // Headless allowlist. Adjust to match the bot-AI / networking / patcher
    // mods you actually run.
    "headlessIncludes": [
        // Fika components — Fika.Core for the protocol, Fika.Headless for the host
        "../BepInEx/plugins/Fika",

        // Bot AI + behavior
        "../BepInEx/plugins/SAIN",
        "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll",
        "../BepInEx/plugins/DrakiaXYZ-Waypoints",
        "../BepInEx/plugins/QuestingBots",
        "../BepInEx/plugins/MoreBotsAPI",

        // Bot-affecting gameplay
        "../BepInEx/plugins/DontShootTheBus.dll",
        "../BepInEx/plugins/NerfBotGrenades.dll",
        "../BepInEx/plugins/Shibdib.SniperBros.dll",
        "../BepInEx/plugins/skwizzy.LootingBots.dll",

        // Misc that headless runs alongside bots
        "../BepInEx/plugins/RUAFComeHome",
        "../BepInEx/plugins/tacticaltoaster-untargohome",
        "../BepInEx/plugins/SamSWAT.HeliCrash.ArysReloaded",
        "../BepInEx/plugins/Terkoiz.FlareEventNotifier.dll",
        "../BepInEx/plugins/MergeConsumables",

        // Networking / patcher dependencies
        "../BepInEx/plugins/Tyfon.UIFixes.dll",
        "../BepInEx/plugins/Tyfon.UIFixes.Net.dll",
        "../BepInEx/plugins/UnityToolkit",
        "../BepInEx/plugins/s8_SPT_LoadBundleEvenFaster",
        "../BepInEx/plugins/s8_SPT_PatchCRC32",
        "../BepInEx/plugins/UseItemsFromAnywhere.dll",

        // WTT shared content + cosmetics worn in-raid (visible to clients)
        "../BepInEx/plugins/7Bpencil.WeaponCamoAndStickers",
        "../BepInEx/plugins/acidphantasm-armbandsforall",
        "../BepInEx/plugins/acidphantasm-botplacementsystem",
        "../BepInEx/plugins/acidphantasm-temporaryfixes",
        "../BepInEx/plugins/BlackDiv",
        "../BepInEx/plugins/WTT-ArmoryClient",
        "../BepInEx/plugins/WTT-ClientCommonLib",
        "../BepInEx/plugins/WTT-ContentBackportClient",
        "../BepInEx/plugins/WTT-PackNStrap"
    ]
}
```

---

## Reference: Fika

Fika is the multiplayer layer ModSync is designed to coexist with.

- **Wiki:** https://github.com/project-fika/Wiki
- **Headless documentation:** see the Wiki's "Headless" section for the canonical
  list of which mods are known to work / break on a headless instance.
- **Fika-Server-CSharp:** https://github.com/project-fika/Fika-Server-CSharp

When in doubt about whether a particular mod is safe on headless, check the
Fika wiki or ask in the Fika Discord.
