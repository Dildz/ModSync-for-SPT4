# CLAUDE.md — ModSync for SPT 4.0

Project-level instructions for Claude Code. Loaded automatically in every session opened from this directory.

## What this is

Fork of [c-orter/ModSync](https://github.com/c-orter/ModSync) being ported from SPT 3.11 to **SPT 4.0.x**. Fork lives at [Dildz/ModSync-for-SPT4.0](https://github.com/Dildz/ModSync-for-SPT4.0).

Upstream's last release (`v0.11.1`, Mar 2025) targets SPT 3.11. No upstream SPT 4 work exists. License is WTFPL — keep Corter attribution but otherwise unrestricted.

## Branch strategy

- `main` — mirror of upstream `c-orter/ModSync` main. Do not modify directly. Used to pull future upstream changes cleanly.
- `SPT4.0.x` — primary working branch for the port. All porting commits land here. Naming matches Dildz's convention used in other ports (`Southern-Hemisphere-Seasons-SPT4.0.x`, etc.).
- Other inherited branches (`dev`, `SPT/3.10`, `SPT-3.8.3`) — leave alone, they're upstream history.

## Project structure (post-port targets)

| Project | Original | After port |
|---|---|---|
| `ModSync/` | BepInEx client plugin (.NET 4.7.2) | Same target, references updated for SPT 4 DLLs at `D:\SPTarkov4.0\BepInEx\plugins\spt\` |
| `ModSync.Server/` | **TypeScript** mod for SPT 3.x server | **Rewrite to C#** against `SPTarkov.Server.Core` (SPT 4's new C# server). Use `..\server-mod-examples\` as pattern reference. |
| `ModSync.Updater/` | Standalone .NET 8 WinForms exe | Bumped to **net9.0-windows**. Signing currently uses `Corter-Signing.snk` which we don't have — needs replacing or disabling. |
| `ModSync.HashTester/` | .NET 8 console | Bumped to **net9.0**. |
| `ModSync.MetroHash/` | Rust crate (native hash lib) | Probably untouched. |
| `ModSync.Tests/` | NUnit on .NET 8 | Bumped to **net9.0**. Server-side tests now exist in `ModSync.Server.Tests/` (also net9.0). |
| `ModSync.Utility/` | Shared projitems | May survive or be folded into the server rewrite. |

## Reference dirs (sibling to this repo)

- `..\_spt_decompile\SPTarkov.Server.Core.decompiled.cs` — decompiled SPT 4 server. Look here for service names, DI tokens, method signatures.
- `..\server-mod-examples\` — official SPT 4 server-mod examples (24 mods). Pattern reference for the C# server rewrite.

## SPT install for reference (DO NOT modify)

`D:\SPTarkov4.0\` — read-only reference. Key paths:
- `BepInEx\plugins\spt\` — `spt-common.dll`, `spt-core.dll` (new in SPT 4), `spt-custom.dll`, `spt-debugging.dll`, `spt-reflection.dll`, `spt-singleplayer.dll`
- `EscapeFromTarkov_Data\Managed\` — EFT assemblies (Assembly-CSharp.dll, UnityEngine.*, Comfort, Newtonsoft.Json, etc.)
- `SPT\` — SPT-specific server stuff

## Code style preferences

- **Comments:** Teaching-oriented. User is new to both C# and TypeScript. Explain concepts/patterns when first introduced (DI, async/await, properties vs fields, etc.). Don't comment trivially obvious code.
- **Commits:** Short, concise messages. No author trailers, no Co-Authored-By, no body unless necessary.
- **Tone in chat:** Casual, direct.

## FIKA compatibility

In scope. FIKA 4.x exists (Fika-Plugin v2.2.6, separate `Fika-Server-CSharp` repo). Headless client paths should be preserved/ported, not stripped.
