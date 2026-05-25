using System.Text.RegularExpressions;
using ModSync.Utility;
using Newtonsoft.Json;

namespace ModSync.Test;

using SyncPathFileList = Dictionary<string, List<string>>;
using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

[TestFixture]
public class IntegrationTests
{
    private static (SyncPathModFiles, List<string>) RunPlugin(
        string testPath,
        List<SyncPath> syncPaths,
        bool configDeleteRemovedFiles,
        out SyncPathFileList addedFiles,
        out SyncPathFileList updatedFiles,
        out SyncPathFileList removedFiles,
        out SyncPathFileList createdDirectories,
        ref List<string> downloadedFiles
    )
    {
        var remotePath = Path.Combine(testPath, "remote");
        if (!Directory.Exists(remotePath))
            Directory.CreateDirectory(remotePath);

        var localPath = Path.Combine(testPath, "local");
        if (!Directory.Exists(localPath))
            Directory.CreateDirectory(localPath);

        var previousSyncPath = Path.Combine(localPath, "ModSync_Data", "PreviousSync.json");
        var previousSync = File.Exists(previousSyncPath) ? JsonConvert.DeserializeObject<SyncPathModFiles>(File.ReadAllText(previousSyncPath)) : [];

        var localExclusionsPath = Path.Combine(localPath, "ModSync_Data", "Exclusions.json");
        var localExclusions = File.Exists(localExclusionsPath)
            ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(localExclusionsPath))!.Select(Glob.Create).ToList()
            : [];

        List<Regex> remoteExclusions = [Glob.Create("**/*.nosync"), Glob.Create("**/*.nosync.txt")];

        var remoteModFiles = Sync.HashLocalFiles(remotePath, syncPaths, remoteExclusions, localExclusions).Result;
        var localModFiles = Sync.HashLocalFiles(localPath, syncPaths, remoteExclusions, localExclusions).Result;

        Sync.CompareModFiles(
            Path.Combine(testPath, "local"),
            syncPaths,
            localModFiles,
            remoteModFiles,
            previousSync,
            out addedFiles,
            out updatedFiles,
            out removedFiles,
            out createdDirectories
        );

        downloadedFiles.AddRange(addedFiles.SelectMany(kvp => kvp.Value).Concat(updatedFiles.SelectMany(kvp => kvp.Value)));

        return (remoteModFiles, configDeleteRemovedFiles ? removedFiles.SelectMany(kvp => kvp.Value).ToList() : []);
    }

    [Test]
    public void TestInitialEmptySingleFile()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "InitialEmptySingleFile"));

        List<string> downloadedFiles = [];

        var (previousSync, filesToDelete) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("SAIN.dll")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["SAIN.dll"], Has.Count.EqualTo(1));
            Assert.That(updatedFiles["SAIN.dll"], Is.Empty);
            Assert.That(removedFiles["SAIN.dll"], Is.Empty);

            Assert.That(downloadedFiles, Has.Count.EqualTo(1));
            Assert.That(downloadedFiles, Does.Contain("SAIN.dll"));

            Assert.That(filesToDelete, Is.Empty);

            Assert.That(previousSync["SAIN.dll"], Has.Count.EqualTo(1));
            Assert.That(previousSync.Keys, Does.Contain("SAIN.dll"));
        });
    }

    [Test]
    public void TestInitialEmptyManyFiles()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "InitialEmptyManyFiles"));

        List<string> downloadedFiles = [];

        var (previousSync, filesToDelete) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["plugins"], Has.Count.EqualTo(2));
            Assert.That(updatedFiles["plugins"], Is.Empty);
            Assert.That(removedFiles["plugins"], Is.Empty);

            Assert.That(downloadedFiles, Has.Count.EqualTo(2));

            Assert.That(downloadedFiles, Is.EquivalentTo(new List<string> { @"plugins\SAIN.dll", @"plugins\Corter-ModSync.dll" }));

            Assert.That(filesToDelete, Is.Empty);

            Assert.That(previousSync["plugins"], Has.Count.EqualTo(2));
            Assert.That(previousSync["plugins"].Keys, Is.EquivalentTo(new List<string> { @"plugins\SAIN.dll", @"plugins\Corter-ModSync.dll" }));
        });
    }

    [Test]
    public void TestUpdateSingleFile()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "UpdateSingleFile"));

        List<string> downloadedFiles = [];

        var (previousSync, filesToDelete) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("SAIN.dll")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["SAIN.dll"], Is.Empty);
            Assert.That(updatedFiles["SAIN.dll"], Has.Count.EqualTo(1));
            Assert.That(removedFiles["SAIN.dll"], Is.Empty);

            Assert.That(downloadedFiles, Has.Count.EqualTo(1));
            Assert.That(downloadedFiles, Does.Contain("SAIN.dll"));

            Assert.That(filesToDelete, Is.Empty);

            Assert.That(previousSync["SAIN.dll"], Has.Count.EqualTo(1));
            Assert.That(previousSync["SAIN.dll"].Keys, Does.Contain("SAIN.dll"));
        });
    }

    [Test]
    public void TestDoNotUpdateWhenLocalChanges()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "DoNotUpdateWhenLocalChanges"));

        List<string> downloadedFiles = [];

        var (previousSync, _) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("SAIN.dll")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["SAIN.dll"], Is.Empty);
            Assert.That(updatedFiles["SAIN.dll"], Is.Empty);
            Assert.That(removedFiles["SAIN.dll"], Is.Empty);

            Assert.That(downloadedFiles, Is.Empty);
            Assert.That(previousSync["SAIN.dll"]["SAIN.dll"].hash, Is.EqualTo("00d1413dcaf30500b65fc68446b10646"));
        });
    }

    [Test]
    public void TestRemoveSingleFile()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "RemoveSingleFile"));

        List<string> downloadedFiles = [];

        var (_, filesToDelete) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("SAIN.dll")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["SAIN.dll"], Is.Empty);
            Assert.That(updatedFiles["SAIN.dll"], Is.Empty);
            Assert.That(removedFiles["SAIN.dll"], Has.Count.EqualTo(1));

            Assert.That(downloadedFiles, Is.Empty);
            Assert.That(filesToDelete, Is.EquivalentTo(new List<string> { "SAIN.dll" }));
        });
    }

    [Test]
    public void TestMismatchedCases()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "MismatchedCases"));

        List<string> downloadedFiles = [];

        RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["plugins"], Is.Empty);
            Assert.That(updatedFiles["plugins"], Has.Count.EqualTo(1));
            Assert.That(removedFiles["plugins"], Is.Empty);

            Assert.That(downloadedFiles, Has.Count.EqualTo(1));
            Assert.That(downloadedFiles[0], Is.EqualTo(@"plugins\sain.dll"));
        });
    }

    [Test]
    public void TestClientNoSync()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "ClientNoSync"));

        List<string> downloadedFiles = [];

        var (_, filesToDelete) = RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins")],
            configDeleteRemovedFiles: true,
            out var addedFiles,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(addedFiles["plugins"], Is.Empty);
            Assert.That(updatedFiles["plugins"], Is.Empty);
            Assert.That(removedFiles["plugins"], Is.Empty);
            Assert.That(filesToDelete, Is.Empty);
        });
    }

    [Test]
    public void TestCreateEmptyDirectories()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "CreateEmptyDirectories"));

        Directory.CreateDirectory(Path.Combine(testPath, @"remote\plugins\TestMod\SuperImportantEmptyFolder"));

        List<string> downloadedFiles = [];

        RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins")],
            configDeleteRemovedFiles: true,
            out _,
            out _,
            out _,
            out var createdDirectories,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(createdDirectories["plugins"], Has.Count.EqualTo(1));
            Assert.That(createdDirectories["plugins"][0], Is.EqualTo(@"plugins\TestMod\SuperImportantEmptyFolder"));
        });
    }

    [Test]
    public void TestIgnoreCreateDirectoryThatIsNotEmpty()
    {
        var testPath = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "EmptyDirectoryOnServerButPopulatedOnClient")
        );

        Directory.CreateDirectory(Path.Combine(testPath, @"remote\plugins\EmptyDirectory"));

        List<string> downloadedFiles = [];

        RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins")],
            configDeleteRemovedFiles: true,
            out _,
            out _,
            out _,
            out var createdDirectories,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(createdDirectories["plugins"], Has.Count.EqualTo(0));
            Assert.That(createdDirectories["plugins"], Has.No.Member(@"plugins\EmptyDirectory"));
        });
    }

    [Test]
    public void TestEnforcedBypassesLocalExclusions()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "EnforcedBypassesLocalExclusions"));

        List<string> downloadedFiles = [];

        RunPlugin(
            testPath,
            syncPaths: [new SyncPath("plugins", enforced: true)],
            configDeleteRemovedFiles: true,
            out _,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(updatedFiles["plugins"], Is.EquivalentTo(new List<string> { @"plugins\SAIN\SAIN.dll", @"plugins\SAIN\config.txt" }));
            Assert.That(removedFiles["plugins"], Is.EquivalentTo(new List<string> { @"plugins\SAIN\ExtraFile.txt" }));
        });
    }

    [Test]
    public void TestEnforcedOnlySyncedWhenUpdated()
    {
        var testPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\IntegrationTests", "EnforcedOnlySyncedWhenUpdated"));

        List<string> downloadedFiles = [];

        RunPlugin(
            testPath,
            syncPaths: [new SyncPath("test1.txt", enforced: true), new SyncPath("test2.txt", enforced: true)],
            configDeleteRemovedFiles: true,
            out _,
            out var updatedFiles,
            out var removedFiles,
            out _,
            ref downloadedFiles
        );

        Assert.Multiple(() =>
        {
            Assert.That(updatedFiles["test1.txt"], Is.Empty);
            Assert.That(updatedFiles["test2.txt"], Is.EquivalentTo(new List<string> { @"test2.txt" }));
        });
    }
}
