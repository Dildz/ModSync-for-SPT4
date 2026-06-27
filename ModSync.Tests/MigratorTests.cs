using ModSync.Utility;
using Newtonsoft.Json;

namespace ModSync.Test;

using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

[TestFixture]
public class MigratorTests
{
    private static void CopyFilesRecursively(string source, string target) => CopyFilesRecursively(new DirectoryInfo(source), new DirectoryInfo(target));

    private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var dir in source.GetDirectories())
            CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name), true);
    }

    [Test]
    public void TestMigrateNoModSync()
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\MigratorTests", "NoModSync"));
        var testDirectory = TestUtils.GetTemporaryDirectory();

        if (!Directory.Exists(sourceDirectory))
            Directory.CreateDirectory(sourceDirectory);

        CopyFilesRecursively(sourceDirectory, testDirectory);

        List<SyncPath> syncPaths = [new(@"BepInEx\plugins"), new(@"BepInEx\patchers")];

        var migrator = new Migrator(testDirectory);
        migrator.TryMigrate(Version.Parse("0.8.0"), syncPaths);

        Assert.That(
            Directory.GetFiles(testDirectory, "*", SearchOption.AllDirectories),
            Is.EquivalentTo(new List<string> { Path.Combine(testDirectory, @"ModSync_Data\Version.txt") })
        );

        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestMigrateOldModSyncFile()
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\MigratorTests", "OldModSyncFile"));
        var testDirectory = TestUtils.GetTemporaryDirectory();

        CopyFilesRecursively(sourceDirectory, testDirectory);

        List<SyncPath> syncPaths = [new(@"BepInEx\plugins"), new(@"BepInEx\patchers")];

        var migrator = new Migrator(testDirectory);
        migrator.TryMigrate(Version.Parse("0.8.0"), syncPaths);

        Assert.That(
            Directory.GetFiles(testDirectory, "*", SearchOption.AllDirectories),
            Is.EquivalentTo(new List<string> { Path.Combine(testDirectory, @"ModSync_Data\Version.txt") })
        );

        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestMigrateVersionedModSyncFile()
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\MigratorTests", "VersionedModSyncFile"));
        var testDirectory = TestUtils.GetTemporaryDirectory();

        CopyFilesRecursively(sourceDirectory, testDirectory);

        List<SyncPath> syncPaths = [new(@"BepInEx\plugins"), new(@"BepInEx\patchers")];

        var migrator = new Migrator(testDirectory);
        migrator.TryMigrate(Version.Parse("0.9.0"), syncPaths);

        var modSyncDir = Path.Combine(testDirectory, "ModSync_Data");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(testDirectory, ".modsync")), Is.False);
            Assert.That(Directory.Exists(modSyncDir), Is.True);
            Assert.That(File.Exists(Path.Combine(modSyncDir, "PreviousSync.json")), Is.True);
        });

        var previousSync = JsonConvert.DeserializeObject<SyncPathModFiles>(File.ReadAllText(Path.Combine(modSyncDir, "PreviousSync.json")));

        Assert.Multiple(() =>
        {
            Assert.That(previousSync!.Keys, Is.EquivalentTo(syncPaths.Select(syncPath => syncPath.path)));
            Assert.That(
                previousSync[@"BepInEx\plugins"].Keys,
                Is.EquivalentTo(new List<string> { @"BepInEx\plugins\SAIN.dll", @"BepInEx\plugins\Corter-ModSync.dll" })
            );

            Assert.That(previousSync[@"BepInEx\plugins"][@"BepInEx\plugins\SAIN.dll"].hash, Is.EqualTo(""));
            Assert.That(previousSync[@"BepInEx\plugins"][@"BepInEx\plugins\Corter-ModSync.dll"].hash, Is.EqualTo(""));

            Assert.That(File.Exists(Path.Combine(modSyncDir, "Version.txt")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(modSyncDir, "Version.txt")), Is.EqualTo("0.9.0"));
        });

        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestVersionTxtAdvancesOnPatchBump()
    {
        // Regression: a >=0.9.0 client on the same minor as the plugin (e.g. 0.12.0 -> 0.12.4)
        // hit the warn-only branch and never rewrote Version.txt, so it stayed "older than the
        // server" and re-warned every boot. Version.txt must advance to the running plugin version.
        var testDirectory = TestUtils.GetTemporaryDirectory();
        var modSyncDir = Directory.CreateDirectory(Path.Combine(testDirectory, "ModSync_Data")).FullName;
        File.WriteAllText(Path.Combine(modSyncDir, "Version.txt"), "0.12.0");

        List<SyncPath> syncPaths = [new(@"BepInEx\plugins"), new(@"BepInEx\patchers")];

        new Migrator(testDirectory).TryMigrate(Version.Parse("0.12.4"), syncPaths);

        Assert.That(File.ReadAllText(Path.Combine(modSyncDir, "Version.txt")), Is.EqualTo("0.12.4"));

        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestMigrateModSyncDirectoryFrom080()
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\MigratorTests", "ModSyncDirectory"));
        var testDirectory = TestUtils.GetTemporaryDirectory();

        CopyFilesRecursively(sourceDirectory, testDirectory);

        List<SyncPath> syncPaths = [new(@"BepInEx\plugins"), new(@"BepInEx\patchers")];

        var migrator = new Migrator(testDirectory);
        migrator.TryMigrate(Version.Parse("0.8.0"), syncPaths);

        Assert.That(
            Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories).Select(file => file.Replace(sourceDirectory, "")).ToList(),
            Is.EquivalentTo(Directory.GetFiles(testDirectory, "*", SearchOption.AllDirectories).Select(file => file.Replace(testDirectory, "")).ToList())
        );

        var modSyncDir = Path.Combine(testDirectory, "ModSync_Data");
        Assert.That(File.ReadAllText(Path.Combine(modSyncDir, "Version.txt")), Is.EqualTo("0.8.0"));

        var previousSync = JsonConvert.DeserializeObject<SyncPathModFiles>(File.ReadAllText(Path.Combine(modSyncDir, "PreviousSync.json")))!;

        Assert.Multiple(() =>
        {
            Assert.That(previousSync[@"BepInEx\plugins"].Keys, Contains.Item(@"BepInEx\plugins\SAIN.dll"));
            Assert.That(previousSync[@"BepInEx\plugins"][@"BepInEx\plugins\SAIN.dll"].directory, Is.False);
        });
    }
}
