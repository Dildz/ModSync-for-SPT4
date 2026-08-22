namespace ModSync.Server.Test;

/// <summary>
/// Tests for SyncUtil.GetFilesInDir - the recursive file walker that feeds HashModFilesAsync.
/// Uses real temp directories (same pattern as ModSync.Tests/IntegrationTests.cs).
/// Ported from the original TypeScript sync.test.ts "hashModFiles" describe block.
/// </summary>
[TestFixture]
public class GetFilesInDirTests
{
    private string _tempDir = null!;
    private SyncUtil _syncUtil = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = TestUtils.GetTemporaryDirectory();

        var config = new Config(
            syncPaths: [],
            exclusions: ["**/*.nosync", "**/*.nosync.txt"],
            headlessIncludes: [],
            managedIncludes: [],
            headlessManagedIncludes: []);

        _syncUtil = new SyncUtil(config, new NoOpLogger<SyncUtil>());
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDir, recursive: true);

    // ── Non-existent path ─────────────────────────────────────────────────────

    [Test]
    public void NonExistentDir_YieldsNothing()
    {
        var result = _syncUtil.GetFilesInDir(Path.Combine(_tempDir, "doesNotExist")).ToList();
        Assert.That(result, Is.Empty);
    }

    // ── Single-file syncpath ──────────────────────────────────────────────────

    [Test]
    public void SingleFileSyncPath_YieldsThatFile()
    {
        var file = Path.Combine(_tempDir, "ModSync.Updater.exe");
        File.WriteAllText(file, "content");

        var result = _syncUtil.GetFilesInDir(file).ToList();

        Assert.That(result, Is.EqualTo(new[] { file }));
    }

    [Test]
    public void SingleFileSyncPath_ExcludedFile_YieldsNothing()
    {
        var file = Path.Combine(_tempDir, "excluded.nosync");
        File.WriteAllText(file, "");

        var result = _syncUtil.GetFilesInDir(file).ToList();

        Assert.That(result, Is.Empty);
    }

    // ── Directory walk ────────────────────────────────────────────────────────

    [Test]
    public void DirectoryWithFiles_YieldsAllNonExcludedFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "SAIN.dll"), "content");
        File.WriteAllText(Path.Combine(_tempDir, "SAIN.dll.nosync"), "");

        var result = _syncUtil.GetFilesInDir(_tempDir).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result, Contains.Item(Path.Combine(_tempDir, "SAIN.dll")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_tempDir, "SAIN.dll.nosync")));
        });
    }

    [Test]
    public void RecursiveWalk_YieldsFilesInSubdirectories()
    {
        var subDir = Path.Combine(_tempDir, "SAIN");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "SAIN.dll"), "content");
        File.WriteAllText(Path.Combine(subDir, "config.json"), "{}");

        var result = _syncUtil.GetFilesInDir(_tempDir).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result, Contains.Item(Path.Combine(subDir, "SAIN.dll")));
            Assert.That(result, Contains.Item(Path.Combine(subDir, "config.json")));
        });
    }

    // ── Empty directory handling ──────────────────────────────────────────────

    [Test]
    public void EmptyDirectory_YieldsDirItself()
    {
        // Empty dirs are yielded so clients know to recreate them
        var emptyDir = Path.Combine(_tempDir, "EmptyMod");
        Directory.CreateDirectory(emptyDir);

        var result = _syncUtil.GetFilesInDir(_tempDir).ToList();

        Assert.That(result, Contains.Item(emptyDir));
    }

    [Test]
    public void DirectoryWithAllFilesExcluded_YieldsDirItself()
    {
        File.WriteAllText(Path.Combine(_tempDir, "everything.nosync"), "");

        var result = _syncUtil.GetFilesInDir(_tempDir).ToList();

        Assert.That(result, Is.EqualTo(new[] { _tempDir }));
    }

    // ── skipExclusions ────────────────────────────────────────────────────────

    [Test]
    public void SkipExclusions_IncludesFilesMatchingExclusionGlobs()
    {
        File.WriteAllText(Path.Combine(_tempDir, "SAIN.dll"), "content");
        File.WriteAllText(Path.Combine(_tempDir, "SAIN.dll.nosync"), "");

        var result = _syncUtil.GetFilesInDir(_tempDir, skipExclusions: true).ToList();

        Assert.That(result, Contains.Item(Path.Combine(_tempDir, "SAIN.dll.nosync")));
    }

    [Test]
    public void SkipExclusions_DoesNotFilterAnyFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Fika.Headless.dll"), "content");

        var configWithFikaExclusion = new Config(
            syncPaths: [],
            exclusions: ["../BepInEx/plugins/Fika/Fika.Headless.dll"],
            headlessIncludes: [],
            managedIncludes: [],
            headlessManagedIncludes: []);

        var syncUtil = new SyncUtil(configWithFikaExclusion, new NoOpLogger<SyncUtil>());
        var result = syncUtil.GetFilesInDir(_tempDir, skipExclusions: true).ToList();

        Assert.That(result, Contains.Item(Path.Combine(_tempDir, "Fika.Headless.dll")));
    }
}
