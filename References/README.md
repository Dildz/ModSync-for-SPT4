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
