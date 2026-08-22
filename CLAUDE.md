# CLAUDE.md - ModSync for SPT 4.1

Project-level instructions for Claude Code. Loaded automatically in every session opened from this directory.

## What this is

Fork of [c-orter/ModSync](https://github.com/c-orter/ModSync), ported from SPT 3.11 to SPT 4. Fork lives at [Dildz/ModSync-for-SPT4](https://github.com/Dildz/ModSync-for-SPT4).

**This worktree is the `SPT4.1.x` branch** - the line still in active development, and the only one that gets new SPT support until SPT 5 lands. The 4.0 line is frozen at SPT 4.0.13 (the final 4.0 release) and lives in the sibling worktree `../ModSync-for-SPT4.0`; the two share one `.git`, so `git worktree list` from either shows both.

Upstream's last release (`v0.11.1`, Mar 2025) targets SPT 3.11. No upstream SPT 4 work exists. License is WTFPL - keep Corter attribution but otherwise unrestricted.

## Branch strategy

- `main` - mirror of upstream `c-orter/ModSync` main. Do not modify directly. Used to pull future upstream changes cleanly.
- `SPT4.1.x` - **the branch this worktree is on, and the maintained one.** All new work lands here.
- `SPT4.0.x` - the 4.0 line, frozen at SPT 4.0.13. Bug fixes only; features are ported here from 4.1 rather than developed on it. Checked out in the sibling worktree, so git will refuse to check it out here.
- Other inherited branches (`dev`, `SPT/3.10`, `SPT-3.8.3`) - leave alone, they're upstream history.

## Project structure and targets

SPT 4.1 runs on **.NET 10**, and .NET does not roll forward across major versions - anything loaded by the
server must be net10, and the Updater must be net10 or it won't start on a clean 4.1 install. The BepInEx
side stays on net472 because that is what the Unity/Mono client loads.

| Project | What it is | Target |
|---|---|---|
| `ModSync/` | BepInEx client plugin | `net472`. Builds against the SPT/EFT DLLs vendored in `References/`. |
| `ModSync.Server/` | SPT server mod, C# (was TypeScript on SPT 3.x) | `net10.0`, against `SPTarkov.Server.Core` |
| `ModSync.Patcher/` | BepInEx preloader patcher, applies staged updates on headless | `net472` |
| `ModSync.Updater/` | Standalone WinForms exe | `net10.0-windows`. Signing referenced `Corter-Signing.snk`, which we don't have. |
| `ModSync.HashTester/` | Console harness for the hashers | `net10.0` |
| `ModSync.MetroHash/` | Rust crate (native hash lib) | untouched by the port |
| `ModSync.Tests/` | NUnit, client-side | `net10.0` |
| `ModSync.Server.Tests/` | NUnit, server-side | `net10.0` |
| `ModSync.Utility/` | Shared `.projitems`, compiled into both sides | follows its host project |

⚠ `Corter-ModSync.sln` **cannot be built as a whole on this Linux host** - `ModSync.Updater` is
`net10.0-windows` and fails with `NETSDK1100: To build a project targeting Windows on this operating
system`. Build `ModSync.Server.csproj` on its own and run the two test projects; that covers everything
Linux can check, and CI (Windows) covers the rest.

## Reference dirs (sibling to this repo)

- `..\_spt_decompile\SPTarkov.Server.Core.decompiled.cs` - decompiled SPT 4 server. Look here for service names, DI tokens, method signatures.
- `..\server-mod-examples\` - official SPT 4 server-mod examples (24 mods). Pattern reference for the C# server rewrite.

## Build-time reference DLLs

The client plugin builds against the DLLs vendored in `References/`, not against a live game install.
**`References/README.md` is the authoritative record** of where each one came from and when it was last
refreshed - read it before touching them. Current state: the four `spt-*.dll` come from the SPT **4.1.1**
release archive, `Assembly-CSharp.dll` is Fika-Plugin's hollowed 4.1-era copy (EFT build 40743), and the
Unity/BSG/Newtonsoft set tracks the game build rather than the SPT version, so it usually survives an SPT
bump untouched.

Path note: SPT 4.1 **renamed the server folder from `SPT/` to `SPT_Runtime/`** inside the game root. Every
path in `config.jsonc` is relative to that folder, so configs carry over between the lines untouched - but
anything that hardcodes the folder name is a bug. See `ModSync.Server/PathExt.cs`, which derives it from
the server's working directory for exactly this reason.

## Code style preferences

- **Comments:** Teaching-oriented. User is new to both C# and TypeScript. Explain concepts/patterns when first introduced (DI, async/await, properties vs fields, etc.). Don't comment trivially obvious code.
- **Commits:** Short, concise messages. No author trailers, no Co-Authored-By, no body unless necessary.
- **Tone in chat:** Casual, direct.

## FIKA compatibility

In scope. FIKA 4.x exists (Fika-Plugin v2.2.6, separate `Fika-Server-CSharp` repo). Headless client paths should be preserved/ported, not stripped.

## Build & test

- **CI (`ci.yml`) runs on `windows-latest`** - every push to `SPT4.0.x` **or `SPT4.1.x`** runs `Build` + `Run client tests` (`ModSync.Tests`) + `Run server tests` (`ModSync.Server.Tests`). **CI is the authoritative correctness gate; it matches the real target (the BepInEx client is Windows-only).** Local runs are still worth doing for fast iteration - see below.
- On this Linux host the working SDK is **.NET 10 at `/home/ubuntu/.dotnet/dotnet`** (plain `dotnet` is an 8.0-only system install and cannot build net10). Build OOMs on default parallelism - use `MSBUILDDISABLENODEREUSE=1 ... -m:1 -p:BuildInParallel=false`.

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
1. Push changes to `SPT4.1.x`, wait for CI green.
2. Bump the version in **all five places** - missing one isn't fatal but shows up in logs as the wrong build (e.g. `Loaded 1 patcher method from [Corter-ModSync-Prepatch 0.12.5.0]` after a 0.12.6 deploy):
   - `ModSync.Server/ModSyncMod.cs` - `Version`
   - `ModSync/Properties/AssemblyInfo.cs` - `AssemblyVersion` + `AssemblyFileVersion`
   - `ModSync/Plugin.cs` - the `[BepInPlugin(...)]` attribute
   - `ModSync.Patcher/ModSync.Patcher.csproj` - `Version` + `AssemblyVersion` + `FileVersion`
   - `ModSync.Updater/ModSync.Updater.csproj` - `Version`

   Verify with: `grep -rn "0\.13\.[0-9]" --include=*.cs --include=*.csproj . | grep -viE "/bin/|/obj/|test"`
   (the 4.1 line is versioned `0.13.x`; the 4.0 line is `0.12.x`, and the major tracks the SPT line)

   Then update the `release.yml` body (see its "UPDATE BEFORE TAGGING" note).
3. `git tag vX.Y.Z && git push --tags` (use `-preN` suffix for a pre-release trial). Release workflow attaches the zip.
