# Usage Examples

Worked `config.jsonc` examples, from the simplest setup to a real server's. Each is complete and
can be adapted directly - see [Configuration](Configuration) for what every option means.

- [Example 1: standard setup (no enforced rules)](#example-1-standard-setup-no-enforced-rules)
- [Example 2: catch-alls + enforced and optional overrides](#example-2-catch-alls--enforced-and-optional-overrides)
- [Example 3: player-only optional mods (a real server)](#example-3-player-only-optional-mods-a-real-server)

## Example 1: standard setup (no enforced rules)

The common case, and all most servers need - nothing is forced. The three catch-alls sync everything in
those folders by default. Individual mods don't need their own entries: SAIN (and its BigBrain + Waypoints
dependencies), Raid Review, Dynamic Maps and everything else in `../BepInEx/plugins` all sync automatically
via the catch-all. Clients can toggle any of these off themselves, since nothing here is enforced.

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

> [!TIP]
> **Making individual mods selectable in the F12 menu.** Each syncPath is its own on/off toggle in the
> client's in-game F12 sync menu, labelled by its [`name`](#options) (or the raw path if unnamed). In
> Example 1 that's just the three folders - a client can turn a whole folder on or off, but not pick out
> a single mod. To give one mod its own labelled toggle, break it out into its own syncPath entry (as
> Example 2 does with SAIN). Leave it at defaults to keep it synced-by-default but individually
> toggleable, or add `"enabled": false` to make it **opt-in** (off until the client ticks the box).
> Everything you don't break out still rides the catch-all.

## Example 2: catch-alls + enforced and optional overrides

The same catch-alls, plus per-mod overrides on top - the setup for a server that wants certain mods
locked to the server (`enforced`) and others available but off by default (`enabled: false`). Each
object overrides just its own path; everything else still rides the catch-alls. Note there is **no Fika
entry** - Fika deliberately stays on the non-enforced catch-all so its headless DLL is never deleted
(see the [`enforced`](#options) warning). A plugin that ships a config file is paired with it (`(1/2)`,
`(2/2)`) so both stay in lockstep.

```jsonc
{
	"syncPaths": [
		// Catch-all defaults (non-enforced) - anything not named below rides these:
		"../BepInEx/plugins",
		"../BepInEx/patchers",
		"../BepInEx/config",

		// Enforced overrides - every client must match the server exactly.
		// SAIN needs BigBrain + Waypoints, so enforce them alongside it:
		{
			"enforced": true,
			"name": "(Enforced) BigBrain",
			"path": "../BepInEx/plugins/DrakiaXYZ-BigBrain.dll"
		},
		{
			"enforced": true,
			"name": "(Enforced) Waypoints (1/2)",
			"path": "../BepInEx/plugins/DrakiaXYZ-Waypoints"
		},
		{
			"enforced": true,
			"name": "(Enforced) Waypoints Config (2/2)",
			"path": "../BepInEx/config/xyz.drakia.waypoints.cfg"
		},
		{
			"enforced": true,
			"name": "(Enforced) SAIN (1/2)",
			"path": "../BepInEx/plugins/SAIN"
		},
		{
			"enforced": true,
			"name": "(Enforced) SAIN Config (2/2)",
			"path": "../BepInEx/config/me.sol.sain.cfg"
		},

		// Optional override - opt-in (off unless the client ticks the box):
		{
			"enabled": false,
			"name": "(Optional) Raid Review",
			"path": "../BepInEx/plugins/RAID_REVIEW.dll"
		}
	],
	// ...
}
```

## Example 3: player-only optional mods (a real server)

Taken from a live Fika server. It enforces nothing - the three catch-alls carry the shared modset, and every
*optional* entry is a player-facing extra that a headless has no use for. Note the pattern that repeats:
**`"enabled": false` + `"headless": false` together**, and a plugin paired with its own config file so both
follow the same toggle.

```jsonc
{
	"syncPaths": [
		// Catch-alls carry the shared modset:
		"../BepInEx/plugins",
		"../BepInEx/patchers",
		"../BepInEx/config",

		// Replaces a base-game file - see the section below:
		{
			"path": "../BepInEx/patchers/TarkovDLSS45",
			"name": "(Optional) Tarkov DLSS 4.5",
			"enabled": false,
			"headless": false,
			"baseFiles": [
				"../EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"
			]
		},

		// Plugin + its config, kept in lockstep on one toggle each:
		{
			"path": "../BepInEx/plugins/PlayerEncumbranceBar",
			"name": "(Optional) Player Encumbrance Bar",
			"enabled": false,
			"headless": false
		},
		{
			"path": "../BepInEx/config/com.mpstark.PlayerEncumbranceBar.cfg",
			"name": "(Optional) Player Encumbrance Bar - config",
			"enabled": false,
			"headless": false
		},

		// A single-file mod works the same way:
		{
			"path": "../BepInEx/plugins/NoInsurance.dll",
			"name": "(Optional) No Insurance",
			"enabled": false,
			"headless": false
		}
	],
	// ...
}
```

> [!TIP]
> **`enabled: false` alone is not enough to keep a mod off a headless.** A headless has no F12 menu, so it can't
> tick anything - but it also can't be relied on to skip the path for the right reason. `headless: false` is the
> explicit gate. For anything a headless renders or displays (UI, maps, graphics), set both.
