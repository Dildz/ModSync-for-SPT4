# References

This folder bundles SPT/EFT/Unity DLLs needed at **build time** by the BepInEx client plugin (`ModSync/`). They are not shipped in releases — at runtime, the user's own SPT install provides them.

Bundling this way means the repo is self-contained: `git clone` + `dotnet build` works without needing an SPT install on the build machine.

## What's here

| DLL(s) | Source | Notes |
|---|---|---|
| `spt-common.dll`, `spt-core.dll`, `spt-custom.dll`, `spt-reflection.dll` | `<SPT>/BepInEx/plugins/spt/` | Open-source SPT modules |
| `UnityEngine.*.dll`, `Unity.TextMeshPro.dll` | `<SPT>/EscapeFromTarkov_Data/Managed/` | Unity engine — freely redistributable |
| `Newtonsoft.Json.dll` | `<SPT>/EscapeFromTarkov_Data/Managed/` | Open source; matches whatever EFT ships |
| `Comfort.dll`, `Comfort.Unity.dll`, `bsg.console.core.dll`, `Sirenix.Serialization.dll` | `<SPT>/EscapeFromTarkov_Data/Managed/` | Small BSG-adjacent DLLs (accepted norm in the SPT modding community) |
| `Assembly-CSharp.dll` | **Hollowed** copy borrowed from [`project-fika/Fika-Plugin`](https://github.com/project-fika/Fika-Plugin/tree/main/References) | Method bodies stripped — signatures only. Fika keeps this current. |

BepInEx itself is **not** here — it comes via NuGet (`BepInEx.Core 5.*` from `https://nuget.bepinex.dev/v3/index.json`, configured in `../nuget.config`).

## Refreshing when SPT bumps

When a new SPT version requires updated references:

1. Update your local SPT install.
2. Re-copy from `<SPT>/BepInEx/plugins/spt/` and `<SPT>/EscapeFromTarkov_Data/Managed/` into this folder (overwrite existing files).
3. For `Assembly-CSharp.dll`: pull the latest `hollowed.dll` from a fresh `Fika-Plugin` clone (`References/hollowed.dll` there → `References/Assembly-CSharp.dll` here).
4. Build, test, commit, ship.

Most SPT patch versions (e.g. 4.0.13 → 4.0.14) won't require this — the existing compiled `Corter-ModSync.dll` stays binary-compatible. Only refresh on detected breakage or when you need a new API.

## Current state (SPT 4.1)

A **major** version bump does require it — SPT 4.1 changed all four `spt-*.dll` and deobfuscated the client.

| DLL(s) | Refreshed for 4.1? | From |
|---|---|---|
| `spt-common.dll`, `spt-core.dll`, `spt-custom.dll`, `spt-reflection.dll` | ✅ yes | SPT **4.1.1** release archive, `BepInEx/plugins/spt/` |
| `Assembly-CSharp.dll` | ✅ yes | `Fika-Plugin` `References/hollowed.dll`, 4.1-era |
| `Comfort*.dll`, `bsg.console.core.dll`, `Sirenix.Serialization.dll`, `Newtonsoft.Json.dll`, `Unity*.dll` | ✅ yes | a real **4.1.1** game install, `EscapeFromTarkov_Data/Managed/` |

The Unity/BSG/Newtonsoft set tracks the **game build**, not the SPT version, so it usually survives an SPT bump untouched — and 4.0.13 → 4.1.1 bears that out almost exactly. Comparing the two installs' `Managed` folders (EFT builds **40087** and **40743**): the file inventory is identical, 169 files in both, and only **two** files differ in size at all — `Assembly-CSharp.dll` and `FilesChecker.dll`.

Of the DLLs vendored here, the eight `Unity*` ones were already **byte-identical** and needed no copy. The five BSG/Newtonsoft ones changed hash while keeping their exact size, differing by a near-constant ~518-521 bytes each regardless of file size — the signature of a plain rebuild (MVID, PE timestamp, debug directory), not an API change. So this refresh is provenance hygiene; it is not expected to alter a single byte of compiled output, which is consistent with the solution having built green against the 4.0-era copies all along.

A note on `Assembly-CSharp.dll`: this is Fika-Plugin's hollowed copy, and it is confirmed 4.1-era on two independent counts. Fika release **v2.4.0** (2026-08-03) states "Compatible with EFT 0.16.9.**40743** — Updated to SPT 4.1.X", and 40743 is exactly the EFT build in SPT's own `SPT-4.1.1-40743-e18bd1e` release. The binary agrees: the old copy carried 3,309 `GClass*` and 406 `GStruct*` symbols, the new one has 3 and 0, and it contains deobfuscated names such as `ABotProfileCreator` straight out of SPT's 4.0→4.1 rename table.

That matters less than it sounds for this mod, mind: the reference is **build-time only** (at runtime the player's own install provides the real assembly), and ModSync touches just `EFT.UI.PreloaderUI` and `EFT.UI.ConsoleScreen` — neither of which appears in the rename table, i.e. both kept their names through the deobfuscation.
