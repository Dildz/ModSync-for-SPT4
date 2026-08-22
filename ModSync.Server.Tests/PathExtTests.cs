namespace ModSync.Server.Test;

/// <summary>
/// The wire prefix for a path inside the server folder must be that folder's actual name.
///
/// It used to be the hardcoded literal `SPT\`, which is correct on SPT 4.0 and wrong on 4.1,
/// where the server folder was renamed to `SPT_Runtime/`. Nothing failed loudly: ToWirePath and
/// ToServerPath were exact inverses of each other, so hashes matched and downloads succeeded,
/// and the files simply landed in `&lt;gameRoot&gt;/SPT/` - a folder that doesn't exist on 4.1.
/// A round-trip test alone would have passed throughout, so these assert the prefix itself.
/// </summary>
[TestFixture]
public class PathExtTests
{
    // What the server folder is called on this machine. The tests run from the test project's
    // output directory, so this is whatever that happens to be - the point is that ToWirePath
    // agrees with it, not what it contains.
    private static string ServerFolder => new DirectoryInfo(Directory.GetCurrentDirectory()).Name;

    [Test]
    public void ToWirePath_PrefixesWithTheServerFolderName_NotAHardcodedSpt()
    {
        var wire = PathExt.ToWirePath(PathExt.WinPath("user/mods"));

        Assert.That(wire, Is.EqualTo($@"{ServerFolder}\user\mods"),
            "a path inside the server folder must be advertised under that folder's real name");
    }

    [Test]
    public void ToWirePath_LeavesGameRootPathsAlone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PathExt.ToWirePath("../BepInEx/plugins"), Is.EqualTo("BepInEx/plugins"));
            Assert.That(PathExt.ToWirePath(@"..\BepInEx\plugins"), Is.EqualTo(@"BepInEx\plugins"));
        });
    }

    [Test]
    public void ToServerPath_IsTheInverseOfToWirePath()
    {
        string[] serverPaths = ["user/mods", "../BepInEx/plugins", "../ModSync.Updater.exe"];

        foreach (var serverPath in serverPaths)
        {
            var roundTripped = PathExt.ToServerPath(PathExt.ToWirePath(serverPath));

            Assert.That(PathExt.UnixPath(roundTripped), Is.EqualTo(PathExt.UnixPath(serverPath)),
                $"'{serverPath}' did not survive the wire round trip");
        }
    }

    [Test]
    public void ToServerPath_OnlyStripsTheFolderNameFollowedByASeparator()
    {
        // A syncpath whose name merely starts with the folder name is a game-root path like any
        // other. On 4.0 that is the difference between `SPT/user` and a mod folder called `SPTfoo`.
        var wire = $"{ServerFolder}foo/bar";

        Assert.That(PathExt.ToServerPath(wire), Is.EqualTo($"../{wire}"));
    }
}
