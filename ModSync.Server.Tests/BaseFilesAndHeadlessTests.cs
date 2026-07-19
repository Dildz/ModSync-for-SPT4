using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for the two syncpath object-form options added in v0.12.6:
///
///   • baseFiles — base-game files a mod REPLACES, living outside its own folder. They must
///     follow the syncpath's active state, so a player who opted out never receives them.
///     This replaced a hardcoded DynamicMaps special case; these tests are what stop it
///     regressing into "every opt-out client silently gets the mod's engine DLLs anyway".
///
///   • headless — false means never send this path (or its baseFiles) to a Fika headless.
///     Needed because a headless has no F12 menu and therefore IGNORES the opt-in toggles,
///     syncing everything it's offered. `enabled:false` alone cannot keep a mod off it.
///
/// Real temp directories and real files throughout, matching HashModFilesAsyncTests.
/// </summary>
[TestFixture]
public class BaseFilesAndHeadlessTests
{
    private string _pluginsDir = null!;
    private string _baseDir = null!;
    private string _baseFile = null!;

    [SetUp]
    public void SetUp()
    {
        _pluginsDir = TestUtils.GetTemporaryDirectory();
        _baseDir = TestUtils.GetTemporaryDirectory();

        // Stands in for EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll — a base-game
        // file the mod replaces, deliberately OUTSIDE the mod's own folder.
        _baseFile = Path.Combine(_baseDir, "nvngx_dlss.dll");
        File.WriteAllText(_baseFile, "base-game-file");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_pluginsDir, recursive: true);
        Directory.Delete(_baseDir, recursive: true);
    }

    private SyncUtil MakeSyncUtil() =>
        new(new Config(
            syncPaths: [],
            exclusions: [],
            headlessIncludes: [],
            managedIncludes: [],
            headlessManagedIncludes: []),
            new NoOpLogger<SyncUtil>());

    /// <summary>The mod's own folder, with one file in it.</summary>
    private string MakeModDir(string name = "TarkovDLSS45")
    {
        var dir = Path.Combine(_pluginsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".dll"), "mod");
        return dir;
    }

    private static List<string> Served(Dictionary<string, Dictionary<string, ModFile>> result) =>
        result.Values.SelectMany(files => files.Keys).ToList();

    // ── baseFiles follow the syncpath's active state ──────────────────────────

    [Test]
    public async Task BaseFiles_PathActive_AreServed()
    {
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, baseFiles: [_baseFile]);

        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: false, isActive: _ => true);

        Assert.That(Served(result), Has.Some.EndsWith("nvngx_dlss.dll"),
            "an opted-IN player must receive the base-game file the mod replaces");
    }

    [Test]
    public async Task BaseFiles_PathInactive_AreNotServed()
    {
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, baseFiles: [_baseFile]);

        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: false, isActive: _ => false);

        Assert.That(Served(result), Has.None.EndsWith("nvngx_dlss.dll"),
            "an opted-OUT player must NOT receive the mod's replacement for a base-game file");
    }

    [Test]
    public async Task BaseFiles_InactivePath_StillClaimedSoCatchAllCannotReServe()
    {
        // The real hazard: the base file also sits under an enclosing catch-all. Ownership is
        // claimed across ALL paths (active or not), so the opt-out carves it out rather than
        // letting the catch-all hand it over anyway — the same rule that makes opt-in mods
        // work under ../BepInEx/plugins.
        var modDir = MakeModDir();
        var optOut = new SyncPath(modDir, enabled: false, baseFiles: [_baseFile]);
        var catchAll = new SyncPath(_baseDir);

        var result = await MakeSyncUtil().HashModFilesAsync(
            [optOut, catchAll],
            isHeadless: false,
            isActive: sp => sp.path == _baseDir); // only the catch-all is active

        Assert.That(Served(result), Has.None.EndsWith("nvngx_dlss.dll"),
            "the opted-out mod's base file leaked back in via the enclosing catch-all");
    }

    [Test]
    public async Task BaseFiles_MissingOnDisk_IsSkippedNotFatal()
    {
        // An admin can list a baseFile they haven't staged yet. That's a config mistake worth
        // a warning, but it must not throw and take the whole hash request down.
        var modDir = MakeModDir();
        var syncPath = new SyncPath(
            modDir,
            enabled: false,
            baseFiles: [Path.Combine(_baseDir, "does-not-exist.dll")]);

        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: false, isActive: _ => true);

        Assert.Multiple(() =>
        {
            Assert.That(Served(result), Has.None.EndsWith("does-not-exist.dll"));
            Assert.That(Served(result), Has.Some.EndsWith("TarkovDLSS45.dll"),
                "the rest of the syncpath must still be served");
        });
    }

    // ── baseFiles must be DOWNLOADABLE, not just offered ──────────────────────

    [Test]
    public void BaseFiles_AreDownloadable_EvenThoughTheyLiveOutsideTheSyncPath()
    {
        // Caught on a live stack: the server offered nvngx_dlss.dll in the hash list, then
        // answered the download with 400 "not in any enabled sync path" — because baseFiles
        // sit OUTSIDE their syncpath's folder and so fail the containment check. The client
        // retried forever and the mod could never install.
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, baseFiles: [_baseFile]);

        Assert.DoesNotThrow(
            () => SyncUtil.SanitizeDownloadPath(_baseFile, [syncPath]),
            "a declared baseFile must be downloadable or the mod can never be installed");
    }

    [Test]
    public void UndeclaredFileOutsideSyncPaths_IsStillRejected()
    {
        // The baseFiles allowance must widen the allowlist by EXACTLY the declared files —
        // it must not open a traversal hole to anything else outside the syncpaths.
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, baseFiles: [_baseFile]);
        var neighbour = Path.Combine(_baseDir, "not-declared.dll");
        File.WriteAllText(neighbour, "x");

        Assert.Throws<HttpError>(
            () => SyncUtil.SanitizeDownloadPath(neighbour, [syncPath]),
            "a file merely sitting next to a baseFile must NOT be downloadable");
    }

    // ── headless:false keeps a player-only mod off headless ───────────────────

    [Test]
    public async Task HeadlessFalse_HeadlessRequest_PathAndBaseFilesBothSkipped()
    {
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, headless: false, baseFiles: [_baseFile]);

        // isActive is deliberately true-for-everything: a headless requests every path it's
        // offered, so `headless:false` is the ONLY thing that can keep this off it.
        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: true, isActive: _ => true);

        Assert.Multiple(() =>
        {
            Assert.That(Served(result), Has.None.EndsWith("TarkovDLSS45.dll"),
                "headless:false path must not reach a headless client");
            Assert.That(Served(result), Has.None.EndsWith("nvngx_dlss.dll"),
                "headless:false must cover the path's baseFiles too");
        });
    }

    [Test]
    public async Task HeadlessFalsePath_InsideCatchAll_DoesNotLeakViaCatchAll()
    {
        // Regression: `headless:false` used to skip the path outright, BEFORE claiming its
        // files for ownership — so an enclosing catch-all (../BepInEx/patchers for a
        // prepatcher-based mod like Tarkov DLSS 4.5) walked over them and served them to the
        // headless anyway. Caught on a live stack: the headless had TarkovDLSS45 installed.
        var modDir = MakeModDir();
        var playerOnly = new SyncPath(modDir, enabled: false, headless: false);
        var catchAll = new SyncPath(_pluginsDir);
        File.WriteAllText(Path.Combine(_pluginsDir, "OtherMod.dll"), "x");

        // longest-first, as Config sorts them
        var result = await MakeSyncUtil().HashModFilesAsync([playerOnly, catchAll], isHeadless: true, isActive: _ => true);
        var served = Served(result);

        Assert.Multiple(() =>
        {
            Assert.That(served, Has.None.EndsWith("TarkovDLSS45.dll"),
                "headless:false path leaked to headless via the enclosing catch-all");
            Assert.That(served, Has.Some.EndsWith("OtherMod.dll"),
                "the rest of the catch-all must still reach headless");
        });
    }

    [Test]
    public async Task HeadlessFalse_PlayerRequest_StillServed()
    {
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir, enabled: false, headless: false, baseFiles: [_baseFile]);

        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: false, isActive: _ => true);

        Assert.Multiple(() =>
        {
            Assert.That(Served(result), Has.Some.EndsWith("TarkovDLSS45.dll"),
                "headless:false must not affect players");
            Assert.That(Served(result), Has.Some.EndsWith("nvngx_dlss.dll"));
        });
    }

    [Test]
    public async Task HeadlessDefaultsTrue_HeadlessRequest_StillServed()
    {
        // Default must stay `true` — every existing config predates this option and must
        // keep behaving exactly as before.
        var modDir = MakeModDir();
        var syncPath = new SyncPath(modDir);

        Assert.That(syncPath.headless, Is.True, "headless must default to true");

        var result = await MakeSyncUtil().HashModFilesAsync([syncPath], isHeadless: true, isActive: _ => true);

        Assert.That(Served(result), Has.Some.EndsWith("TarkovDLSS45.dll"));
    }

    [Test]
    public void BaseFiles_DefaultsToEmpty_NotNull()
    {
        // Bare-string syncpaths construct with no baseFiles; the client dereferences this
        // list without a null check, so an empty list (never null) is part of the contract.
        var syncPath = new SyncPath("../BepInEx/plugins");

        Assert.That(syncPath.baseFiles, Is.Not.Null.And.Empty);
    }
}
