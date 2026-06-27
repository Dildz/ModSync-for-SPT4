using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for ConfigUtil.LoadAsync — config file creation, loading, built-in syncpath
/// injection, object-form parsing, and validation.
/// Ported from the original TypeScript config.test.ts "ConfigUtil" describe block.
///
/// LoadAsync resolves config.jsonc from the directory containing the server assembly.
/// In tests that directory is the test output bin — we write our test config there
/// and clean up in TearDown.
/// </summary>
[TestFixture]
public class ConfigLoadTests
{
    // ConfigUtil reads config.jsonc from the same directory as the server assembly.
    private static string ConfigPath =>
        Path.Combine(Path.GetDirectoryName(typeof(ConfigUtil).Assembly.Location)!, "config.jsonc");

    private static ConfigUtil MakeUtil() => new(new NoOpLogger<ConfigUtil>());

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
    }

    // ── File creation ─────────────────────────────────────────────────────────

    [Test]
    public async Task CreatesDefaultConfigWhenFileMissing()
    {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);

        await MakeUtil().LoadAsync();

        Assert.That(File.Exists(ConfigPath), Is.True);
    }

    // ── Built-ins always present ──────────────────────────────────────────────

    [Test]
    public async Task AlwaysIncludesModSyncUpdaterInSyncPaths()
    {
        // Matches the TS test: "Should always include ModSync.Updater.exe in syncPaths"
        File.WriteAllText(ConfigPath, @"{ ""syncPaths"": [], ""exclusions"": [] }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.Some.Matches<SyncPath>(sp =>
            sp.path.Contains("ModSync.Updater")));
    }

    [Test]
    public async Task AlwaysIncludesModSyncPluginInSyncPaths()
    {
        File.WriteAllText(ConfigPath, @"{ ""syncPaths"": [], ""exclusions"": [] }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.Some.Matches<SyncPath>(sp =>
            sp.path.Contains("Corter-ModSync")));
    }

    [Test]
    public async Task BuiltInsAreEnforcedAndSilent()
    {
        File.WriteAllText(ConfigPath, @"{ ""syncPaths"": [], ""exclusions"": [] }");

        var config = await MakeUtil().LoadAsync();

        var updater = config.SyncPaths.First(sp => sp.path.Contains("ModSync.Updater"));
        Assert.Multiple(() =>
        {
            Assert.That(updater.enforced, Is.True);
            Assert.That(updater.silent, Is.True);
        });
    }

    [Test]
    public async Task AlwaysIncludesModSyncPatcherInSyncPaths()
    {
        File.WriteAllText(ConfigPath, @"{ ""syncPaths"": [], ""exclusions"": [] }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.Some.Matches<SyncPath>(sp =>
            sp.path.Contains("Corter-ModSync-Prepatch")));
    }

    // ── Per-audience enforcement (ResolveEnforced) ────────────────────────────

    [Test]
    public void Updater_EnforcedForPlayers_RelaxedForHeadless()
    {
        var updater = new SyncPath(ConfigUtil.UpdaterSyncPath, enforced: true);
        Assert.Multiple(() =>
        {
            Assert.That(ConfigUtil.ResolveEnforced(updater, isHeadless: false), Is.True);   // players keep it
            Assert.That(ConfigUtil.ResolveEnforced(updater, isHeadless: true), Is.False);   // headless can trim it
        });
    }

    [Test]
    public void Patcher_EnforcedForHeadless_RelaxedForPlayers()
    {
        var patcher = new SyncPath(ConfigUtil.PatcherSyncPath, enforced: false);
        Assert.Multiple(() =>
        {
            Assert.That(ConfigUtil.ResolveEnforced(patcher, isHeadless: true), Is.True);    // headless keeps it
            Assert.That(ConfigUtil.ResolveEnforced(patcher, isHeadless: false), Is.False);  // players can trim it
        });
    }

    [Test]
    public void OtherPaths_KeepTheirConfiguredEnforcement()
    {
        var plugin = new SyncPath("../BepInEx/plugins/Corter-ModSync", enforced: true);
        var userPath = new SyncPath("../BepInEx/plugins", enforced: false);
        Assert.Multiple(() =>
        {
            Assert.That(ConfigUtil.ResolveEnforced(plugin, isHeadless: true), Is.True);
            Assert.That(ConfigUtil.ResolveEnforced(plugin, isHeadless: false), Is.True);
            Assert.That(ConfigUtil.ResolveEnforced(userPath, isHeadless: true), Is.False);
            Assert.That(ConfigUtil.ResolveEnforced(userPath, isHeadless: false), Is.False);
        });
    }

    // ── User syncpath loading ─────────────────────────────────────────────────

    [Test]
    public async Task LoadsUserSyncPaths()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [""../BepInEx/plugins""],
            ""exclusions"": []
        }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.Some.Matches<SyncPath>(sp =>
            sp.path == "../BepInEx/plugins"));
    }

    [Test]
    public async Task LoadsExclusions()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [],
            ""exclusions"": [""**/*.nosync""]
        }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.Exclusions, Contains.Item("**/*.nosync"));
    }

    // ── Object-form syncpath ──────────────────────────────────────────────────

    [Test]
    public async Task ObjectFormSyncPathParsesAllOptions()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [{ ""path"": ""../BepInEx/plugins"", ""enabled"": false, ""enforced"": true, ""silent"": true, ""restartRequired"": false }],
            ""exclusions"": []
        }");

        var config = await MakeUtil().LoadAsync();

        var sp = config.SyncPaths.First(s => s.path == "../BepInEx/plugins");
        Assert.Multiple(() =>
        {
            Assert.That(sp.enabled, Is.False);
            Assert.That(sp.enforced, Is.True);
            Assert.That(sp.silent, Is.True);
            Assert.That(sp.restartRequired, Is.False);
        });
    }

    [Test]
    public async Task ObjectFormSyncPathUsesDefaultsForMissingFields()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [{ ""path"": ""../BepInEx/plugins"", ""enabled"": false }],
            ""exclusions"": []
        }");

        var config = await MakeUtil().LoadAsync();

        var sp = config.SyncPaths.First(s => s.path == "../BepInEx/plugins");
        Assert.Multiple(() =>
        {
            Assert.That(sp.enforced, Is.False);
            Assert.That(sp.silent, Is.False);
            Assert.That(sp.restartRequired, Is.True);
        });
    }

    // ── Managed syncpath injection ────────────────────────────────────────────

    [Test]
    public async Task InjectsManagedSyncPathWhenManagedIncludesPopulated()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [],
            ""exclusions"": [],
            ""managedIncludes"": [""Unity.VectorGraphics.dll""]
        }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.Some.Matches<SyncPath>(sp =>
            sp.path.Contains("Managed")));
    }

    [Test]
    public async Task DoesNotInjectManagedSyncPathWhenBothListsEmpty()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [],
            ""exclusions"": [],
            ""managedIncludes"": [],
            ""headlessManagedIncludes"": []
        }");

        var config = await MakeUtil().LoadAsync();

        Assert.That(config.SyncPaths, Has.None.Matches<SyncPath>(sp =>
            sp.path.Contains("Managed")));
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Test]
    public void ThrowsOnAbsolutePath()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [""/etc/shadow""],
            ""exclusions"": []
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }

    [Test]
    public void ThrowsOnWindowsAbsolutePath()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [""C:\\Windows\\System32""],
            ""exclusions"": []
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }

    [Test]
    public void ThrowsOnDuplicateSyncPaths()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [""../BepInEx/plugins"", ""../BepInEx/plugins""],
            ""exclusions"": []
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }

    [Test]
    public void ThrowsWhenSyncPathIsAlsoExclusion()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [""../BepInEx/plugins""],
            ""exclusions"": [""../BepInEx/plugins""]
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }

    [Test]
    public void ThrowsOnInvalidNameCharacters()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [{ ""path"": ""../BepInEx/plugins"", ""name"": ""bad\\name"" }],
            ""exclusions"": []
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }

    [Test]
    public void ThrowsOnNonStringNameType()
    {
        File.WriteAllText(ConfigPath, @"{
            ""syncPaths"": [{ ""path"": ""../BepInEx/plugins"", ""name"": 42 }],
            ""exclusions"": []
        }");

        Assert.ThrowsAsync<InvalidOperationException>(() => MakeUtil().LoadAsync());
    }
}
