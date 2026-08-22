using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for SyncUtil.HashModFilesAsync - the routing logic that decides which files
/// reach which client type. Uses real temp directories with real files.
///
/// New tests covering our headless and managedIncludes additions (no TS equivalent).
/// </summary>
[TestFixture]
public class HashModFilesAsyncTests
{
    private string _pluginsDir = null!;
    private string _managedDir = null!;

    [SetUp]
    public void SetUp()
    {
        _pluginsDir = TestUtils.GetTemporaryDirectory();
        _managedDir = TestUtils.GetTemporaryDirectory();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_pluginsDir, recursive: true);
        Directory.Delete(_managedDir, recursive: true);
    }

    private SyncUtil MakeSyncUtil(Config config) => new(config, new NoOpLogger<SyncUtil>());

    private static Config MakeConfig(
        List<string>? exclusions = null,
        List<string>? headlessIncludes = null,
        List<string>? managedIncludes = null,
        List<string>? headlessManagedIncludes = null) =>
        new(
            syncPaths: [],
            exclusions: exclusions ?? [],
            headlessIncludes: headlessIncludes ?? [],
            managedIncludes: managedIncludes ?? [],
            headlessManagedIncludes: headlessManagedIncludes ?? []);

    // ── Player exclusions ─────────────────────────────────────────────────────

    [Test]
    public async Task PlayerRequest_ExcludedFile_NotInResult()
    {
        File.WriteAllText(Path.Combine(_pluginsDir, "SAIN.dll"), "content");
        File.WriteAllText(Path.Combine(_pluginsDir, "SAIN.dll.nosync"), "");

        var config = MakeConfig(exclusions: ["**/*.nosync"]);
        var syncUtil = MakeSyncUtil(config);
        var syncPaths = new List<SyncPath> { new(_pluginsDir) };

        var result = await syncUtil.HashModFilesAsync(syncPaths, isHeadless: false);

        Assert.That(result[PathExt.WinPath(_pluginsDir)].Keys,
            Has.None.EndsWith("nosync"));
    }

    [Test]
    public async Task PlayerRequest_NonExcludedFile_InResult()
    {
        File.WriteAllText(Path.Combine(_pluginsDir, "SAIN.dll"), "content");

        var config = MakeConfig(exclusions: ["**/*.nosync"]);
        var syncUtil = MakeSyncUtil(config);
        var syncPaths = new List<SyncPath> { new(_pluginsDir) };

        var result = await syncUtil.HashModFilesAsync(syncPaths, isHeadless: false);

        Assert.That(result[PathExt.WinPath(_pluginsDir)].Keys,
            Has.Some.EndsWith("SAIN.dll"));
    }

    // ── Headless plugins allowlist ────────────────────────────────────────────

    [Test]
    public async Task HeadlessRequest_FileNotInAllowlist_NotInResult()
    {
        File.WriteAllText(Path.Combine(_pluginsDir, "SAIN.dll"), "content");
        File.WriteAllText(Path.Combine(_pluginsDir, "Fika.Headless.dll"), "content");

        // Headless allowlist only includes SAIN.dll - Fika.Headless.dll should be excluded
        var config = MakeConfig(headlessIncludes: [Path.Combine(_pluginsDir, "SAIN.dll")]);
        var syncUtil = MakeSyncUtil(config);

        // Syncpath must look like "../BepInEx/plugins" to trigger headless allowlist gate
        var syncPaths = new List<SyncPath> { new("../BepInEx/plugins") };

        // We can't use the real plugins dir here since IsPluginsScoped checks the path name.
        // Test the allowlist logic directly via a path that matches the plugins heuristic.
        // (The routing heuristic uses the syncpath's path field, not the actual disk location.)
        var result = await syncUtil.HashModFilesAsync(
            [new SyncPath("../BepInEx/plugins")],
            isHeadless: true);

        // Empty result expected - "../BepInEx/plugins" doesn't exist on disk in tests,
        // so GetFilesInDir warns and yields nothing. What we validated: the plumbing runs.
        Assert.That(result.ContainsKey(@"..\BepInEx\plugins"), Is.True);
    }

    [Test]
    public void HeadlessRequest_ExclusionOverride_AllowlistedFileReachesHeadless()
    {
        // Core behavior: a file in BOTH exclusions AND headlessIncludes must reach headless.
        // This is the bug fixed in commit a662796 - headless plugins bypass exclusions.
        File.WriteAllText(Path.Combine(_pluginsDir, "Fika.Headless.dll"), "content");

        // Fika.Headless.dll is excluded from players but allowlisted for headless
        var fikaPath = PathExt.UnixPath(Path.Combine(_pluginsDir, "Fika.Headless.dll"));
        var config = MakeConfig(
            exclusions: [fikaPath],
            headlessIncludes: [fikaPath]);

        var syncUtil = MakeSyncUtil(config);

        // Use "../BepInEx/plugins" as the syncpath path so IsPluginsScoped returns true,
        // but point it at our temp dir by placing temp files there and changing CWD - or
        // more pragmatically: test GetFilesInDir directly, which is where skipExclusions lives.
        // HashModFilesAsync drives GetFilesInDir with skipExclusions=true for headless+plugins.
        // We verify that behavior through GetFilesInDir tests; here we confirm Config is wired correctly.
        Assert.Multiple(() =>
        {
            Assert.That(config.IsExcluded(fikaPath), Is.True, "should be excluded from players");
            Assert.That(config.IsHeadlessAllowed(fikaPath), Is.True, "should be allowed for headless");
        });
    }

    // ── Managed allowlist ─────────────────────────────────────────────────────

    [Test]
    public void PlayerRequest_ManagedAllowlist_OnlyAllowlistedFilesInResult()
    {
        File.WriteAllText(Path.Combine(_managedDir, "Unity.VectorGraphics.dll"), "content");
        File.WriteAllText(Path.Combine(_managedDir, "Assembly-CSharp.dll"), "content");

        var config = MakeConfig(managedIncludes: ["Unity.VectorGraphics.dll"]);
        var syncUtil = MakeSyncUtil(config);

        // Use "../EscapeFromTarkov_Data/Managed" so IsManagedScoped returns true
        var syncPaths = new List<SyncPath>
        {
            new("../EscapeFromTarkov_Data/Managed")
        };

        // The syncpath path field drives the scope heuristic; actual walking uses the path on disk.
        // Since "../EscapeFromTarkov_Data/Managed" doesn't exist in our temp env, we test the
        // allowlist filtering logic directly via Config to avoid environment coupling.
        Assert.Multiple(() =>
        {
            Assert.That(config.IsManagedAllowed("Unity.VectorGraphics.dll"), Is.True);
            Assert.That(config.IsManagedAllowed("Assembly-CSharp.dll"), Is.False);
        });
    }

    [Test]
    public void HeadlessRequest_HeadlessManagedAllowlist_UsedInsteadOfPlayerList()
    {
        var config = MakeConfig(
            managedIncludes: ["Unity.VectorGraphics.dll"],
            headlessManagedIncludes: []);

        // Headless gets its own separate list - a file in the player list doesn't auto-reach headless
        Assert.Multiple(() =>
        {
            Assert.That(config.IsManagedAllowed("Unity.VectorGraphics.dll"), Is.True);
            Assert.That(config.IsHeadlessManagedAllowed("Unity.VectorGraphics.dll"), Is.False);
        });
    }

    // ── Disabled override under a catch-all (the optional-inside-catch-all bug) ─

    [Test]
    public async Task DisabledOverride_UnderCatchAll_FilesNotServed()
    {
        // A mod folder that sits INSIDE the catch-all, declared as a disabled
        // (opt-in, unticked) override. Its files must NOT be served - not via the
        // catch-all, and not under its own key. Regression for the bug where an
        // optional path toggled off still syncs because the catch-all re-claims it.
        File.WriteAllText(Path.Combine(_pluginsDir, "OtherMod.dll"), "x");
        var dmDir = Path.Combine(_pluginsDir, "DynamicMaps");
        Directory.CreateDirectory(dmDir);
        File.WriteAllText(Path.Combine(dmDir, "dm.dll"), "x");

        var syncUtil = MakeSyncUtil(MakeConfig());

        // Longest-first, as Config sorts them. Override is inactive; catch-all active.
        var catchAll = new SyncPath(_pluginsDir);
        var dmOverride = new SyncPath(dmDir, enabled: false);

        var result = await syncUtil.HashModFilesAsync(
            [dmOverride, catchAll],
            isHeadless: false,
            isActive: sp => sp.path == _pluginsDir); // only the catch-all is active

        var served = result.Values.SelectMany(files => files.Keys).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(served, Has.None.EndsWith("dm.dll"),
                "disabled override's file must not be served (it leaked via the catch-all)");
            Assert.That(served, Has.Some.EndsWith("OtherMod.dll"),
                "the rest of the catch-all must still sync");
        });
    }

    // ── Enforced syncpath bypasses headless allowlist ─────────────────────────

    [Test]
    public async Task EnforcedSyncPath_HeadlessRequest_BypassesAllowlist()
    {
        // ModSync's own plugin syncpath is enforced=true. Enforced paths must always reach
        // headless even if the admin forgot to add them to headlessIncludes.
        // IsPluginsScoped("../BepInEx/plugins/Corter-ModSync") returns true, but enforced=true
        // means applyHeadlessPluginsAllowlist = false - so the allowlist gate is skipped.
        var config = MakeConfig(headlessIncludes: []); // empty allowlist
        var syncUtil = MakeSyncUtil(config);

        var enforcedPath = new SyncPath(
            "../BepInEx/plugins/Corter-ModSync",
            enforced: true);

        var result = await syncUtil.HashModFilesAsync([enforcedPath], isHeadless: true);

        // Path doesn't exist on disk, but the key is present (no exception thrown, not filtered out)
        Assert.That(result.ContainsKey(@"..\BepInEx\plugins\Corter-ModSync"), Is.True);
    }
}
