using System.Text;
using System.Text.RegularExpressions;
using ModSync.Utility;

namespace ModSync.Test;

[TestFixture]
public class AddedFilesTests
{
    [Test]
    public void TestSingleAdded()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var addedFiles = Sync.GetAddedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles);

        Assert.That(addedFiles[@"BepInEx\plugins"], Is.EquivalentTo(new List<string> { @"BepInEx\plugins\Corter-ModSync.dll" }));
    }

    [Test]
    public void TestNoneAdded()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var addedFiles = Sync.GetAddedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles);

        Assert.That(addedFiles[@"BepInEx\plugins"], Is.Empty);
    }
}

[TestFixture]
public class UpdatedFilesTests
{
    [Test]
    public void TestSingleAdded()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(updatedFiles[@"BepInEx\plugins"], Is.Empty);
    }

    [Test]
    public void TestSingleUpdated()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(updatedFiles[@"BepInEx\plugins"], Is.EquivalentTo(new List<string> { @"BepInEx\plugins\Corter-ModSync.dll" }));
    }

    [Test]
    public void TestOnlyLocalUpdated()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(updatedFiles[@"BepInEx\plugins"], Is.Empty);
    }

    [Test]
    public void TestFilesExistButPreviousEmpty()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                    { @"BepInEx\plugins\New-Mod.dll", new ModFile("1234567") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>();

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.Multiple(() =>
        {
            Assert.That(updatedFiles[@"BepInEx\plugins"], Has.Count.EqualTo(1));
            Assert.That(updatedFiles[@"BepInEx\plugins"][0], Is.EqualTo(@"BepInEx\plugins\Corter-ModSync.dll"));
        });
    }

    [Test]
    public void TestBothUpdated()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(updatedFiles[@"BepInEx\plugins"], Is.Empty);
    }

    [Test]
    public void TestSingleUpdatedEnforced()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("2345678") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("2345678") },
                }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var updatedFiles = Sync.GetUpdatedFiles([new SyncPath(@"BepInEx\plugins", enforced: true)], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.Multiple(() =>
        {
            Assert.That(updatedFiles[@"BepInEx\plugins"], Has.Count.EqualTo(2));
            Assert.That(updatedFiles[@"BepInEx\plugins"], Does.Contain(@"BepInEx\plugins\Corter-ModSync.dll"));
            Assert.That(updatedFiles[@"BepInEx\plugins"], Does.Contain(@"BepInEx\plugins\SAIN\SAIN.dll"));
        });
    }
}

[TestFixture]
public class RemovedFilesTests
{
    [Test]
    public void TestSingleRemoved()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var removedFiles = Sync.GetRemovedFiles([new SyncPath(@"BepInEx\plugins")], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(removedFiles[@"BepInEx\plugins"], Is.EquivalentTo(new List<string> { @"BepInEx\plugins\Corter-ModSync.dll" }));
    }

    [Test]
    public void TestDeselectedOptionalMod_RemovesOnlyModSyncInstalled()
    {
        // A deselected opt-in path is compared with an EMPTY remote (the server was never asked
        // for it). Its ModSync-installed files (present in previousSync) are removed; a file the
        // player hand-installed (never in previousSync) is left alone.
        var path = @"BepInEx\plugins\OptionalMod";

        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                path,
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\OptionalMod\synced.dll", new ModFile("1") },
                    { @"BepInEx\plugins\OptionalMod\manual.dll", new ModFile("2") },
                }
            },
        };

        // Deselected → empty remote.
        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>> { { path, new Dictionary<string, ModFile>() } };

        // ModSync only ever installed synced.dll.
        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            { path, new Dictionary<string, ModFile> { { @"BepInEx\plugins\OptionalMod\synced.dll", new ModFile("1") } } },
        };

        var removedFiles = Sync.GetRemovedFiles([new SyncPath(path)], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(removedFiles[path], Is.EquivalentTo(new List<string> { @"BepInEx\plugins\OptionalMod\synced.dll" }),
            "deselected mod: remove the ModSync-installed file, leave the hand-installed one");
    }

    [Test]
    public void TestSingleRemovedEnforced()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\OtherPlugin\OtherPlugin.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567", true) },
                }
            },
        };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile> { { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") } }
            },
        };

        var previousRemoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\SAIN\SAIN.dll", new ModFile("1234567") },
                    { @"BepInEx\plugins\Corter-ModSync.dll", new ModFile("1234567") },
                }
            },
        };

        var removedFiles = Sync.GetRemovedFiles([new SyncPath(@"BepInEx\plugins", enforced: true)], localModFiles, remoteModFiles, previousRemoteModFiles);

        Assert.That(
            removedFiles[@"BepInEx\plugins"],
            Is.EquivalentTo(new List<string> { @"BepInEx\plugins\Corter-ModSync.dll", @"BepInEx\plugins\OtherPlugin\OtherPlugin.dll" })
        );
    }
}

[TestFixture]
public class CreatedDirectoriesTests
{
    [Test]
    public void TestCreatedDirectories()
    {
        var localModFiles = new Dictionary<string, Dictionary<string, ModFile>> { { @"BepInEx\plugins", new Dictionary<string, ModFile>() } };

        var remoteModFiles = new Dictionary<string, Dictionary<string, ModFile>>
        {
            {
                @"BepInEx\plugins",
                new Dictionary<string, ModFile>
                {
                    { @"BepInEx\plugins\ModThatDoesntErrorCheckFolders\SuperImportantEmptyFolder", new ModFile("1234567", directory: true) },
                }
            },
        };

        var createdDirectories = Sync.GetCreatedDirectories("", [new SyncPath(@"BepInEx\plugins", enforced: true)], localModFiles, remoteModFiles);

        Assert.That(
            createdDirectories[@"BepInEx\plugins"],
            Is.EquivalentTo(new List<string> { @"BepInEx\plugins\ModThatDoesntErrorCheckFolders\SuperImportantEmptyFolder" })
        );
    }
}

[TestFixture]
public class HashLocalFilesTests
{
    private readonly List<Regex> exclusions =
    [
        Glob.CreateNoEnd("**/*.nosync"),
        Glob.CreateNoEnd("**/*.nosync.txt"),
        Glob.CreateNoEnd("plugins/file2.dll"),
        Glob.CreateNoEnd("plugins/file3.dll"),
        Glob.CreateNoEnd("plugins/ModName"),
        Glob.CreateNoEnd("plugins/OtherMod/subdir"),
    ];

    private readonly Dictionary<string, string> fileContents = new()
    {
        { @"plugins\file1.dll", "Test content" },
        { @"plugins\file2.dll", "Test content 2" },
        { @"plugins\file2.dll.nosync", "" },
        { @"plugins\file3.dll", "Test content 3" },
        { @"plugins\file3.dll.nosync.txt", "" },
        { @"plugins\ModName\mod_name.dll", "Test content 4" },
        { @"plugins\ModName\.nosync", "" },
        { @"plugins\OtherMod\other_mod.dll", "Test content 5" },
        { @"plugins\OtherMod\subdir\image.png", "Test Image" },
        { @"plugins\OtherMod\subdir\.nosync", "" },
    };

    private string testDirectory = string.Empty;

    [SetUp]
    public void Setup()
    {
        testDirectory = TestUtils.GetTemporaryDirectory();

        Directory.CreateDirectory(testDirectory);

        // Create test files
        foreach (var kvp in fileContents)
        {
            var filePath = Path.Combine(testDirectory, kvp.Key);
            var fileParent = Path.GetDirectoryName(filePath);

            if (fileParent != null && !Directory.Exists(fileParent))
                Directory.CreateDirectory(fileParent);

            File.WriteAllText(filePath, kvp.Value);
        }

        Console.WriteLine(testDirectory);
    }

    [TearDown]
    public void Cleanup()
    {
        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestHashLocalFiles()
    {
        var expected = fileContents.Where(kvp => !Sync.IsExcluded(exclusions, kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var result = Sync.HashLocalFiles(testDirectory, [new SyncPath("plugins")], exclusions).Result;

        Assert.That(result, Is.Not.Null);

        foreach (var kvp in expected)
        {
            Assert.That(result["plugins"], Does.ContainKey(kvp.Key));
            Assert.That(result["plugins"][kvp.Key].hash, Is.EqualTo(ImoHash.HashFileObject(new MemoryStream(Encoding.ASCII.GetBytes(kvp.Value))).Result));
        }

        Assert.That(result["plugins"], Has.Count.EqualTo(2));
    }

    [Test]
    public void TestHashLocalFilesWithDirectoryThatDoesNotExist()
    {
        var result = Sync.HashLocalFiles(testDirectory, [new SyncPath("bad_directory")], exclusions).Result;
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result["bad_directory"], Is.Empty);
        });
    }

    [Test]
    public void TestHashLocalFilesWithSingleFile()
    {
        var syncPath = Path.Combine(testDirectory, @"plugins\file1.dll");

        var result = Sync.HashLocalFiles(testDirectory, [new SyncPath(syncPath)], exclusions).Result;

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result[syncPath], Has.Count.EqualTo(1));
            Assert.That(result[syncPath], Does.ContainKey(@"plugins\file1.dll"));
            Assert.That(result[syncPath][@"plugins\file1.dll"].hash, Is.EqualTo("0ce304b7ff04260d67adfdee0af9dd3b"));
        });
    }

    [Test]
    public void TestHashLocalFilesWithSingleFileThatDoesNotExist()
    {
        var syncPath = Path.Combine(testDirectory, "does_not_exist.dll");
        var result = Sync.HashLocalFiles(testDirectory, [new SyncPath(syncPath)], exclusions).Result;
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result[syncPath], Is.Empty);
        });
    }

    [Test]
    public void TestHashLocalFiles_DisabledOverride_CarvesOutOfCatchAll()
    {
        // Catch-all "plugins" (active) + "plugins/OtherMod" as a disabled (opt-out) override.
        // OtherMod's files must be claimed by the override — so they're NOT in the catch-all's
        // local set — and the override itself isn't returned. This stops an opt-out mod sitting
        // inside a catch-all from being flagged for add/remove.
        //
        // Self-contained real dir tree (Path.Combine, not backslash literals) so it runs on
        // both Linux and Windows.
        var dir = TestUtils.GetTemporaryDirectory();
        try
        {
            var plugins = Path.Combine(dir, "plugins");
            var otherMod = Path.Combine(plugins, "OtherMod");
            Directory.CreateDirectory(otherMod);
            File.WriteAllText(Path.Combine(plugins, "file1.dll"), "a");
            File.WriteAllText(Path.Combine(otherMod, "other_mod.dll"), "b");

            var catchAll = new SyncPath("plugins");
            var otherModOverride = new SyncPath(Path.Combine("plugins", "OtherMod"), enabled: false);

            // Longest-first, as Config sorts them.
            var result = Sync.HashLocalFiles(
                dir,
                [otherModOverride, catchAll],
                [],
                isActive: sp => sp.path == "plugins" // only the catch-all is active
            ).Result;

            var pluginsFiles = result["plugins"].Keys.Select(k => k.Replace('\\', '/')).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Not.ContainKey(otherModOverride.path),
                    "inactive override must not be returned");
                Assert.That(pluginsFiles, Has.None.Contains("OtherMod/other_mod.dll"),
                    "override's file must not leak into the catch-all's local set");
                Assert.That(pluginsFiles, Has.Some.Contains("plugins/file1.dll"),
                    "the rest of the catch-all must still be hashed");
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // TestHashLocalFilesEnforcedIgnoresLocalExclusions removed: local walk no longer
    // applies player exclusions at all (filter moved to the remote-list step in
    // Plugin.cs). The enforced flag's remaining role is to bypass the remote-list
    // filter, exercised in IntegrationTests.TestEnforcedBypassesLocalExclusions.

    [Test]
    public void TestHashLocalFiles_BaseFiles_AreHashedWithTheirSyncPath()
    {
        // baseFiles live OUTSIDE the mod's folder (a base-game file it replaces), but belong
        // to its syncpath for diff purposes. If the local walk missed them the local side
        // would look empty for those paths and the diff would re-download them every launch.
        var dir = TestUtils.GetTemporaryDirectory();
        try
        {
            var modDir = Path.Combine(dir, "patchers", "TarkovDLSS45");
            var nativeDir = Path.Combine(dir, "EscapeFromTarkov_Data", "Plugins", "x86_64");
            Directory.CreateDirectory(modDir);
            Directory.CreateDirectory(nativeDir);
            File.WriteAllText(Path.Combine(modDir, "TarkovDLSS45.dll"), "mod");
            File.WriteAllText(Path.Combine(nativeDir, "nvngx_dlss.dll"), "replaced-base-file");

            var baseFile = Path.Combine("EscapeFromTarkov_Data", "Plugins", "x86_64", "nvngx_dlss.dll");
            var syncPath = new SyncPath(
                Path.Combine("patchers", "TarkovDLSS45"),
                enabled: false,
                baseFiles: [baseFile]);

            var result = Sync.HashLocalFiles(dir, [syncPath], [], isActive: _ => true).Result;

            var files = result[syncPath.path].Keys.Select(k => k.Replace('\\', '/')).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(files, Has.Some.Contains("nvngx_dlss.dll"),
                    "the mod's baseFile must be hashed under its syncpath");
                Assert.That(files, Has.Some.Contains("TarkovDLSS45.dll"),
                    "the mod's own files must still be hashed");
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void TestHashLocalFiles_BaseFiles_InactivePathNotReturned()
    {
        // Opted out: neither the mod's folder nor its baseFile may appear in the local set,
        // or the diff would flag them for removal — which for a base-game file is exactly the
        // destructive outcome the whole baseFiles/.modsync-bak design exists to prevent.
        var dir = TestUtils.GetTemporaryDirectory();
        try
        {
            var modDir = Path.Combine(dir, "patchers", "TarkovDLSS45");
            var nativeDir = Path.Combine(dir, "EscapeFromTarkov_Data", "Plugins", "x86_64");
            Directory.CreateDirectory(modDir);
            Directory.CreateDirectory(nativeDir);
            File.WriteAllText(Path.Combine(modDir, "TarkovDLSS45.dll"), "mod");
            File.WriteAllText(Path.Combine(nativeDir, "nvngx_dlss.dll"), "replaced-base-file");

            var baseFile = Path.Combine("EscapeFromTarkov_Data", "Plugins", "x86_64", "nvngx_dlss.dll");
            var syncPath = new SyncPath(
                Path.Combine("patchers", "TarkovDLSS45"),
                enabled: false,
                baseFiles: [baseFile]);

            var result = Sync.HashLocalFiles(dir, [syncPath], [], isActive: _ => false).Result;

            Assert.That(result, Does.Not.ContainKey(syncPath.path),
                "an opted-out path must not be returned at all, baseFiles included");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void TestHashLocalFiles_BaseFile_MissingOnDisk_IsSkipped()
    {
        // A player who never installed the mod has no replaced base file. That must hash
        // cleanly rather than throwing — it's the normal state for most of the playerbase.
        var dir = TestUtils.GetTemporaryDirectory();
        try
        {
            var modDir = Path.Combine(dir, "patchers", "TarkovDLSS45");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "TarkovDLSS45.dll"), "mod");

            var syncPath = new SyncPath(
                Path.Combine("patchers", "TarkovDLSS45"),
                enabled: false,
                baseFiles: [Path.Combine("EscapeFromTarkov_Data", "Plugins", "x86_64", "nvngx_dlss.dll")]);

            var result = Sync.HashLocalFiles(dir, [syncPath], [], isActive: _ => true).Result;

            var files = result[syncPath.path].Keys.Select(k => k.Replace('\\', '/')).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(files, Has.None.Contains("nvngx_dlss.dll"));
                Assert.That(files, Has.Some.Contains("TarkovDLSS45.dll"));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

[TestFixture]
public class CreateModFileTest
{
    private readonly Dictionary<string, string> fileContents = new()
    {
        { "file1.dll", "" },
        { "file2.dll", "" },
        { "file2.dll.nosync", "" },
        { "file3.dll", "Test content 3" },
        { "file3.dll.nosync.txt", "" },
        { @"ModName\mod_name.dll", "Test content 4" },
        { @"ModName\.nosync", "" },
        { @"OtherMod\other_mod.dll", "Test content 5" },
        { @"OtherMod\subdir\image.png", "Test Image" },
        { @"OtherMod\subdir\.nosync", "" },
    };

    private string testDirectory = string.Empty;
    private readonly SemaphoreSlim limiter = new(1024);

    [SetUp]
    public void Setup()
    {
        testDirectory = TestUtils.GetTemporaryDirectory();

        Directory.CreateDirectory(testDirectory);

        // Create test files
        foreach (var kvp in fileContents)
        {
            var filePath = Path.Combine(testDirectory, kvp.Key);
            var fileParent = Path.GetDirectoryName(filePath);

            if (!Directory.Exists(fileParent))
                Directory.CreateDirectory(fileParent!);

            File.WriteAllText(filePath, kvp.Value);
        }

        Console.WriteLine(testDirectory);
    }

    [TearDown]
    public void Cleanup()
    {
        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestCreateModFile()
    {
        var modFile = Sync.CreateModFile(Path.Combine(testDirectory, "file1.dll")).Result;

        Assert.Multiple(() =>
        {
            Assert.That(modFile, Is.Not.Null);
            Assert.That(modFile.hash, Is.EqualTo("00d1413dcaf30500b65fc68446b10646"));
        });
    }

    [Test]
    public void TestCreateModFileWithContent()
    {
        var modFile = Sync.CreateModFile(Path.Combine(testDirectory, "file3.dll")).Result;

        Assert.Multiple(() =>
        {
            Assert.That(modFile, Is.Not.Null);
            Assert.That(modFile.hash, Is.EqualTo("0e51ecd1fbd55148997270d6634ff6db"));
        });
    }
}

[TestFixture]
public class IsExcludedTest
{
    private readonly Dictionary<string, string> fileContents = new()
    {
        { "file1.dll", "Test content" },
        { "file2.dll", "Test content 2" },
        { "file3.dll", "Test content 3" },
        { @"ModName\mod_name.dll", "Test content 4" },
        { @"ModName\.nosync", "" },
        { @"ModName\subdir\image.png", "Test Image 1" },
        { @"OtherMod\other_mod.dll", "Test content 5" },
        { @"OtherMod\subdir\image.png", "Test Image 2" },
        { @"OtherMod\subdir\.nosync", "" },
    };

    private readonly List<Regex> exclusions =
    [
        Glob.Create("**/*.nosync"),
        Glob.Create("**/*.nosync.txt"),
        Glob.Create("file2.dll"),
        Glob.Create("file3.dll"),
        Glob.Create(@"ModName"),
        Glob.Create(@"OtherMod\subdir"),
    ];

    private string testDirectory = string.Empty;

    [SetUp]
    public void Setup()
    {
        testDirectory = TestUtils.GetTemporaryDirectory();

        Directory.CreateDirectory(testDirectory);

        // Create test files
        foreach (var kvp in fileContents)
        {
            var filePath = Path.Combine(testDirectory, kvp.Key);
            var fileParent = Path.GetDirectoryName(filePath);

            if (!Directory.Exists(fileParent))
                Directory.CreateDirectory(fileParent!);

            File.WriteAllText(filePath, kvp.Value);
        }

        Console.WriteLine(testDirectory);
    }

    [TearDown]
    public void Cleanup()
    {
        Directory.Delete(testDirectory, true);
    }

    [Test]
    public void TestIsNotExcluded()
    {
        var result = Sync.IsExcluded(exclusions, "file1.dll");
        Assert.That(result, Is.False);
    }

    [Test]
    public void TestIsExcluded()
    {
        var result = Sync.IsExcluded(exclusions, "file2.dll");
        Assert.That(result, Is.True);
    }
}

// HashLocalFilesHeadlessTests (v0.12.0) removed when we reverted to upstream's 2-key
// schema. Headless routing now lives client-side via ModSync_Data/Exclusions.json,
// which already gets exercised through HashLocalFiles' regular localExclusions param.
