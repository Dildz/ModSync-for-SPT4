using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for ModSyncHttpListener.BuildPathsResponse - the /modsync/paths payload.
///
/// These exist because of a real shipped-then-caught bug: /modsync/paths advertised
/// `headless:false` paths that /modsync/hashes then refused to serve. A client dutifully
/// requested one, got no entry back, and clients &lt;=0.12.5 index that dictionary directly
/// (Plugin.cs:590, no TryGetValue) → KeyNotFoundException → ModSync fails to load on every
/// headless. Nothing in the suite caught it because this endpoint had no coverage at all.
///
/// So the load-bearing test here is <see cref="AdvertisedPaths_AreAlwaysServable_ForHeadless"/>:
/// the two endpoints must agree about audience filtering, forever.
/// </summary>
[TestFixture]
public class PathsResponseTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = TestUtils.GetTemporaryDirectory();
        File.WriteAllText(Path.Combine(_dir, "mod.dll"), "x");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private static Config MakeConfig(List<SyncPath> syncPaths) =>
        new(syncPaths, exclusions: [], headlessIncludes: [], managedIncludes: [], headlessManagedIncludes: []);

    // ── audience filtering ────────────────────────────────────────────────────

    [Test]
    public void HeadlessFalsePath_IsHiddenFromHeadless_ButShownToPlayers()
    {
        var playerOnly = new SyncPath("../BepInEx/patchers/TarkovDLSS45", enabled: false, headless: false);
        var normal = new SyncPath("../BepInEx/plugins");

        var toPlayer = ModSyncHttpListener.BuildPathsResponse([playerOnly, normal], isHeadless: false);
        var toHeadless = ModSyncHttpListener.BuildPathsResponse([playerOnly, normal], isHeadless: true);

        Assert.Multiple(() =>
        {
            Assert.That(toPlayer.Select(p => p.path), Has.Some.Contains("TarkovDLSS45"),
                "players must still be offered the mod");
            Assert.That(toHeadless.Select(p => p.path), Has.None.Contains("TarkovDLSS45"),
                "a headless must never even be told this path exists");
            Assert.That(toHeadless, Has.Count.EqualTo(1), "the ordinary path must survive");
        });
    }

    [Test]
    public void HeadlessDefaultTrue_IsShownToEveryone()
    {
        // Every config written before this option existed omits it. They must be unaffected.
        var path = new SyncPath("../BepInEx/plugins");

        Assert.Multiple(() =>
        {
            Assert.That(ModSyncHttpListener.BuildPathsResponse([path], isHeadless: true), Has.Count.EqualTo(1));
            Assert.That(ModSyncHttpListener.BuildPathsResponse([path], isHeadless: false), Has.Count.EqualTo(1));
        });
    }

    // ── the invariant that was missing ────────────────────────────────────────

    [Test]
    public async Task AdvertisedPaths_AreAlwaysServable_ForHeadless()
    {
        // THE regression guard: every path /modsync/paths advertises to a headless must come
        // back with an entry from /modsync/hashes for that same headless. If these two ever
        // disagree again, older clients crash on the missing key.
        var syncPaths = new List<SyncPath>
        {
            new(_dir),                                                        // ordinary
            new(_dir, headless: false),                                       // player-only
            new(_dir, enabled: false, headless: false, baseFiles: ["../x.dll"]), // player-only + baseFiles
        };

        var advertised = ModSyncHttpListener.BuildPathsResponse(syncPaths, isHeadless: true)
            .Select(p => p.path)
            .ToList();

        // Mirror what the listener does on /hashes: a headless requests everything it was
        // offered, so isActive is true for all of them.
        var syncUtil = new SyncUtil(MakeConfig(syncPaths), new NoOpLogger<SyncUtil>());
        var served = await syncUtil.HashModFilesAsync(syncPaths, isHeadless: true, isActive: _ => true);
        var servedKeys = served.Keys.Select(PathExt.ToWirePath).ToList();

        Assert.That(advertised, Is.SubsetOf(servedKeys),
            "/modsync/paths advertised a path that /modsync/hashes will not serve - "
            + "clients <=0.12.5 index the hashes dictionary directly and will throw KeyNotFoundException");
    }

    // ── version gate: protects clients too old to protect themselves ──────────

    [Test]
    public void VersionMismatch_ServesOnlyModSyncsOwnComponents()
    {
        // A pre-0.12.6 client sends no version at all. It builds its ENTIRE diff - including
        // removals - from the paths we hand it, so handing it only builtins makes it
        // structurally incapable of proposing to delete a mod it doesn't understand.
        var syncPaths = new List<SyncPath>
        {
            new(Builtins.UpdaterPath),
            new(Builtins.PatcherPath),
            new(Builtins.PluginPath),
            new("../BepInEx/plugins"),
            new("../BepInEx/plugins/DynamicMaps", enabled: false,
                baseFiles: ["../EscapeFromTarkov_Data/Managed/Unity.VectorGraphics.dll"]),
        };

        var served = ModSyncHttpListener.BuildPathsResponse(syncPaths, isHeadless: false, versionMatches: false);
        var paths = served.ConvertAll(p => p.path);

        Assert.Multiple(() =>
        {
            Assert.That(served, Has.Count.EqualTo(3), "only ModSync's own components may be served");
            Assert.That(paths, Has.None.Contains("DynamicMaps"),
                "an out-of-date client must never be told about an opt-in mod - it would offer to delete it");
            Assert.That(paths, Has.None.EqualTo(@"BepInEx\plugins"),
                "nor about the catch-all");
            foreach (var builtin in Builtins.All)
                Assert.That(paths, Does.Contain(Builtins.ToWire(builtin)));
        });
    }

    [Test]
    public void VersionMatches_ServesEverything()
    {
        var syncPaths = new List<SyncPath>
        {
            new(Builtins.PluginPath),
            new("../BepInEx/plugins"),
            new("../BepInEx/plugins/DynamicMaps", enabled: false),
        };

        var served = ModSyncHttpListener.BuildPathsResponse(syncPaths, isHeadless: false, versionMatches: true);

        Assert.That(served, Has.Count.EqualTo(3));
    }

    [Test]
    public void VersionGate_StillAppliesTheHeadlessFilter()
    {
        // The two gates must compose: a mismatched headless gets builtins, and a player-only
        // builtin-adjacent path is still filtered on its own merits.
        var syncPaths = new List<SyncPath>
        {
            new(Builtins.PluginPath),
            new("../BepInEx/patchers/TarkovDLSS45", enabled: false, headless: false),
        };

        var served = ModSyncHttpListener.BuildPathsResponse(syncPaths, isHeadless: true, versionMatches: false);

        Assert.That(served.ConvertAll(p => p.path), Has.None.Contains("TarkovDLSS45"));
    }

    // ── wire shape ────────────────────────────────────────────────────────────

    [Test]
    public void BaseFiles_AreTranslatedToWireForm()
    {
        // The client resolves baseFiles against its own cwd (game root), so the leading
        // server-relative "../" must be stripped exactly as it is for `path`.
        var syncPath = new SyncPath(
            "../BepInEx/patchers/TarkovDLSS45",
            baseFiles: ["../EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"]);

        var dto = ModSyncHttpListener.BuildPathsResponse([syncPath], isHeadless: false).Single();

        Assert.Multiple(() =>
        {
            Assert.That(dto.baseFiles.Single(), Does.Not.StartWith(".."),
                "baseFiles must be game-root-relative on the wire");
            Assert.That(dto.baseFiles.Single().Replace('\\', '/'),
                Is.EqualTo("EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"));
            Assert.That(dto.path, Does.Not.StartWith(".."));
        });
    }

    [Test]
    public void EmptyBaseFiles_SerialiseAsEmptyList_NotNull()
    {
        // The client dereferences baseFiles without a null check.
        var dto = ModSyncHttpListener.BuildPathsResponse([new SyncPath("../BepInEx/plugins")], isHeadless: false).Single();

        Assert.That(dto.baseFiles, Is.Not.Null.And.Empty);
    }

    [Test]
    public void Optional_ReachesTheClient()
    {
        // `optional` only ever does anything in the client's F12 menu, so it has to survive the
        // wire. If it silently didn't, an admin's opt-out mod would just look like every other
        // enabled path: synced, and invisible in the menu.
        var optOut = new SyncPath("user/mods", name: "Server mods", optional: true);
        var ordinary = new SyncPath("../BepInEx/plugins");

        var dtos = ModSyncHttpListener.BuildPathsResponse([optOut, ordinary], isHeadless: false);

        Assert.Multiple(() =>
        {
            Assert.That(dtos[0].optional, Is.True);
            Assert.That(dtos[0].enabled, Is.True, "optional is independent of enabled");
            Assert.That(dtos[1].optional, Is.False, "a plain path must stay out of the menu");
        });
    }

    [Test]
    public void PerAudienceEnforcement_IsAppliedToBuiltins()
    {
        // The Updater is enforced for players and relaxed for headless; the patcher is the
        // reverse. This is what lets each side trim the component it never runs.
        var updater = new SyncPath(ConfigUtil.UpdaterSyncPath);
        var patcher = new SyncPath(ConfigUtil.PatcherSyncPath);

        var toPlayer = ModSyncHttpListener.BuildPathsResponse([updater, patcher], isHeadless: false);
        var toHeadless = ModSyncHttpListener.BuildPathsResponse([updater, patcher], isHeadless: true);

        Assert.Multiple(() =>
        {
            Assert.That(toPlayer[0].enforced, Is.True, "Updater enforced for players");
            Assert.That(toPlayer[1].enforced, Is.False, "patcher relaxed for players");
            Assert.That(toHeadless[0].enforced, Is.False, "Updater relaxed for headless");
            Assert.That(toHeadless[1].enforced, Is.True, "patcher enforced for headless");
        });
    }
}
