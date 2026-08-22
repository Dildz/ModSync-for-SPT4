using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// The credential file must never leave the server. It sits in the game root - the folder that
/// exists specifically to stage client-bound files - under a fixed, documented name, so its location
/// protects it not at all. This predicate is the entire defence, and these are the ways round it.
/// </summary>
[TestFixture]
public class ProtectedFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "modsync-prot", "SPT_Runtime");

    [Test]
    public void TheCredentialFileIsProtected() =>
        Assert.That(ProtectedFiles.IsProtected(ProtectedFiles.WebAuthPath, Root), Is.True);

    [Test]
    public void ProtectionSurvivesTheSpellingsCallersActuallyUse()
    {
        // The listing gate yields server-relative paths with whatever separator the OS produced; the
        // download gate has already resolved to an absolute path. Both must be caught.
        var absolute = Path.GetFullPath(Path.Combine(Root, ProtectedFiles.WebAuthPath));

        Assert.Multiple(() =>
        {
            Assert.That(ProtectedFiles.IsProtected(absolute, Root), Is.True, "absolute");
            Assert.That(ProtectedFiles.IsProtected(@"..\" + ProtectedFiles.WebAuthFileName, Root), Is.True, "backslashes");
            Assert.That(ProtectedFiles.IsProtected("../" + ProtectedFiles.WebAuthFileName, Root), Is.True, "forward slashes");
        });
    }

    [Test]
    public void ProtectionSurvivesPathsThatWanderBeforeArriving()
    {
        // The obvious bypass: spell the same file a different way and hope the guard compares
        // strings rather than resolving them.
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedFiles.IsProtected($"../BepInEx/../{ProtectedFiles.WebAuthFileName}", Root), Is.True);
            Assert.That(ProtectedFiles.IsProtected($"./../{ProtectedFiles.WebAuthFileName}", Root), Is.True);
            Assert.That(ProtectedFiles.IsProtected($"../BepInEx/plugins/../../{ProtectedFiles.WebAuthFileName}", Root), Is.True);
        });
    }

    [Test]
    public void ProtectionIgnoresCase()
    {
        // On Windows a case-variant is the same file. Being strict on Linux too costs nothing and
        // removes a whole class of platform-dependent bug.
        Assert.That(ProtectedFiles.IsProtected("../MODSYNC.WEBAUTH.JSON", Root), Is.True);
    }

    [Test]
    public void OrdinaryFilesAreNotProtected()
    {
        // The guard has to be narrow, or it silently stops serving real mods.
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedFiles.IsProtected("../BepInEx/plugins/SAIN/SAIN.dll", Root), Is.False);
            Assert.That(ProtectedFiles.IsProtected("../ModSync.Updater.exe", Root), Is.False);
            Assert.That(ProtectedFiles.IsProtected($"../BepInEx/plugins/{ProtectedFiles.WebAuthFileName}", Root), Is.False,
                "a file with the same NAME elsewhere is somebody else's file, not the credential");
            Assert.That(ProtectedFiles.IsProtected("", Root), Is.False);
        });
    }

    [Test]
    public void AnUnresolvablePathIsRefusedRatherThanServed()
    {
        // Fail closed. One unserved file is a bug report; one served credential is a compromised
        // server and every player's machine.
        Assert.That(ProtectedFiles.IsProtected("\0invalid", Root), Is.True);
    }
}
