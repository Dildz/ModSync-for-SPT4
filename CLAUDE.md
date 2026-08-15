# CLAUDE.md - ModSync for SPT 4.0

Project-level instructions for Claude Code. Loaded automatically in every session opened from this directory.

## What this is

Fork of [c-orter/ModSync](https://github.com/c-orter/ModSync) being ported from SPT 3.11 to **SPT 4.0.x**. Fork lives at [Dildz/ModSync-for-SPT4](https://github.com/Dildz/ModSync-for-SPT4).

Upstream's last release (`v0.11.1`, Mar 2025) targets SPT 3.11. No upstream SPT 4 work exists. License is WTFPL - keep Corter attribution but otherwise unrestricted.

## Branch strategy

- `main` - mirror of upstream `c-orter/ModSync` main. Do not modify directly. Used to pull future upstream changes cleanly.
- `SPT4.0.x` - primary working branch for the port. All porting commits land here. Naming matches Dildz's convention used in other ports (`Southern-Hemisphere-Seasons-SPT4.0.x`, etc.).
- Other inherited branches (`dev`, `SPT/3.10`, `SPT-3.8.3`) - leave alone, they're upstream history.

## Project structure (post-port targets)

| Project | Original | After port |
|---|---|---|
| `ModSync/` | BepInEx client plugin (.NET 4.7.2) | Same target, references updated for SPT 4 DLLs at `D:\SPTarkov4.0\BepInEx\plugins\spt\` |
| `ModSync.Server/` | **TypeScript** mod for SPT 3.x server | **Rewrite to C#** against `SPTarkov.Server.Core` (SPT 4's new C# server). Use `..\server-mod-examples\` as pattern reference. |
| `ModSync.Updater/` | Standalone .NET 8 WinForms exe | Bumped to **net9.0-windows**. Signing currently uses `Corter-Signing.snk` which we don't have - needs replacing or disabling. |
| `ModSync.HashTester/` | .NET 8 console | Bumped to **net9.0**. |
| `ModSync.MetroHash/` | Rust crate (native hash lib) | Probably untouched. |
| `ModSync.Tests/` | NUnit on .NET 8 | Bumped to **net9.0**. Server-side tests now exist in `ModSync.Server.Tests/` (also net9.0). |
| `ModSync.Utility/` | Shared projitems | May survive or be folded into the server rewrite. |

## Reference dirs (sibling to this repo)

- `..\_spt_decompile\SPTarkov.Server.Core.decompiled.cs` - decompiled SPT 4 server. Look here for service names, DI tokens, method signatures.
- `..\server-mod-examples\` - official SPT 4 server-mod examples (24 mods). Pattern reference for the C# server rewrite.

## SPT install for reference (DO NOT modify)

`D:\SPTarkov4.0\` - read-only reference. Key paths:
- `BepInEx\plugins\spt\` - `spt-common.dll`, `spt-core.dll` (new in SPT 4), `spt-custom.dll`, `spt-debugging.dll`, `spt-reflection.dll`, `spt-singleplayer.dll`
- `EscapeFromTarkov_Data\Managed\` - EFT assemblies (Assembly-CSharp.dll, UnityEngine.*, Comfort, Newtonsoft.Json, etc.)
- `SPT\` - SPT-specific server stuff

## Code style preferences

- **Comments:** Teaching-oriented. User is new to both C# and TypeScript. Explain concepts/patterns when first introduced (DI, async/await, properties vs fields, etc.). Don't comment trivially obvious code.
- **Commits:** Short, concise messages. No author trailers, no Co-Authored-By, no body unless necessary.
- **Tone in chat:** Casual, direct.

## FIKA compatibility

In scope. FIKA 4.x exists (Fika-Plugin v2.2.6, separate `Fika-Server-CSharp` repo). Headless client paths should be preserved/ported, not stripped.

## Build & test

- **CI (`ci.yml`) runs on `windows-latest`** - every push to `SPT4.0.x` runs `Build` + `Run client tests` (`ModSync.Tests`) + `Run server tests` (`ModSync.Server.Tests`). **CI is the authoritative correctness gate; it matches the real target (the BepInEx client is Windows-only).** Local runs are still worth doing for fast iteration - see below.
- On this Linux host the working SDK is **.NET 9 at `/home/ubuntu/.dotnet/dotnet`** (plain `dotnet` is an 8.0-only system install that can't build net9). Build OOMs on default parallelism - use `MSBUILDDISABLENODEREUSE=1 ... -m:1 -p:BuildInParallel=false`.

### Running tests on this host

The test host used to die with `Test host process crashed: Out of memory` (or `errno 11` on fork) even with ~40GB free. Cause: this is a **Virtuozzo container** (`/dev/ploop`) reporting **10 cores**, and .NET's default **server GC reserves a heap per core** - that blows the container's per-process limit regardless of what `free` says. Force workstation GC:

```bash
export DOTNET_ROOT=/home/ubuntu/.dotnet MSBUILDDISABLENODEREUSE=1
export DOTNET_gcServer=0 DOTNET_GCHeapCount=1 DOTNET_GCHeapHardLimit=0x20000000
/home/ubuntu/.dotnet/dotnet test <proj> -m:1 -p:BuildInParallel=false
```

With that, **both suites run locally in well under a second.** Don't fall back to "client tests can't run here, trust CI" - they can.

**The GC vars above are for `dotnet test` ONLY. Never export them for `dotnet build`** - `DOTNET_GCHeapHardLimit=0x20000000` (512 MB) is too small for `csc` and it dies at startup with a bogus `error : Out of memory.` (stack trace bottoming out in `PrintLogo`). Build with just:

```bash
DOTNET_ROOT=/home/ubuntu/.dotnet MSBUILDDISABLENODEREUSE=1 \
  /home/ubuntu/.dotnet/dotnet build <proj> -c Release -m:1 -p:BuildInParallel=false
```

### The real resource limit here: PIDs, not memory

`Failed to create CoreCLR, HRESULT: 0x80070008` and `Failed to launch testhost: System.OutOfMemoryException` are both **PID exhaustion**, not RAM. This user slice has a hard **`TasksMax=363`** (cgroup `pids:/user.slice/user-1000.slice`) covering every process and thread you own. When it's near the cap, a new .NET runtime can't create its threads, and CoreCLR reports that as the generic `ERROR_NOT_ENOUGH_MEMORY`. **`free` is useless here - it will show 40+ GB available while builds fail.** Check:

```bash
C=/sys/fs/cgroup/pids/user.slice/user-1000.slice
cat $C/pids.current $C/pids.max     # need ~100 free to build/test
```

`/sys/fs/cgroup/pids.max` does NOT exist - reading it yields a misleading 1100 from another cgroup. Use the path above.

**Top cause of exhaustion: leaked `docker compose logs -f`.** Each holds ~13 threads and never exits; the harness leaves them SIGSTOP'd (state `Tl`), so `pkill`/SIGTERM won't clear them - they need `kill -9`. Eight of them ate 104 of the 363 slots and made every build fail for hours.

**So: never run `docker compose logs -f`. Use `--tail=N` without `-f`.** Killing these CLI processes is safe for running containers - they're clients in a separate tree from `dockerd` and cannot signal a container.

Stopping the SPT stack is **not** required to build or test (that was a misdiagnosis - `SPT.Server.Linux`'s ~197 GB VSZ is a harmless reservation, not a commit). Free PIDs first; only stop the stack if that genuinely isn't enough.

- **Known-failing on Linux (18 total): these are Windows-only path fixtures, NOT regressions.** Compare against this list before assuming you broke something:
  - `ModSync.Server.Tests` - **4 fails**, all `SanitizeDownloadPathTests` (Windows-absolute-path / path-resolution checks)
  - `ModSync.Tests` - **14 fails**: `TestHashLocalFiles`; 9 IntegrationTests (`TestCreateEmptyDirectories`, `TestDoNotUpdateWhenLocalChanges`, `TestEnforcedBypassesLocalExclusions`, `TestEnforcedOnlySyncedWhenUpdated`, `TestInitialEmptyManyFiles`, `TestInitialEmptySingleFile`, `TestMismatchedCases`, `TestRemoveSingleFile`, `TestUpdateSingleFile`); 4 MigratorTests (`TestMigrateModSyncDirectoryFrom`, `TestMigrateNoModSync`, `TestMigrateOldModSyncFile`, `TestMigrateVersionedModSyncFile`)
  - So a clean local run is **server 4 fails / client 14 fails**. Any other failure is real.
  - (An earlier version of this file claimed "Integration passes on both" - it does not; those fixtures use backslash paths.)

## Releasing

`release.yml` is **tag-triggered** (`v*`); tagging only builds + publishes the zip, it does NOT re-run tests - so **CI must be green on the branch HEAD before you tag.** Steps:
1. Push changes to `SPT4.0.x`, wait for CI green.
2. Bump the version in **all five places** - missing one isn't fatal but shows up in logs as the wrong build (e.g. `Loaded 1 patcher method from [Corter-ModSync-Prepatch 0.12.5.0]` after a 0.12.6 deploy):
   - `ModSync.Server/ModSyncMod.cs` - `Version`
   - `ModSync/Properties/AssemblyInfo.cs` - `AssemblyVersion` + `AssemblyFileVersion`
   - `ModSync/Plugin.cs` - the `[BepInPlugin(...)]` attribute
   - `ModSync.Patcher/ModSync.Patcher.csproj` - `Version` + `AssemblyVersion` + `FileVersion`
   - `ModSync.Updater/ModSync.Updater.csproj` - `Version`

   Verify with: `grep -rn "0\.12\.[0-9]" --include=*.cs --include=*.csproj . | grep -viE "/bin/|/obj/|test"`

   Then update the `release.yml` body (see its "UPDATE BEFORE TAGGING" note).
3. `git tag vX.Y.Z && git push --tags` (use `-preN` suffix for a pre-release trial). Release workflow attaches the zip.
