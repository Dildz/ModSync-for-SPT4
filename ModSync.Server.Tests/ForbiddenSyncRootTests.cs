namespace ModSync.Server.Test;

/// <summary>
/// The server folder lives INSIDE the game root, so a syncPath of `../` does not merely serve a lot
/// of files - it encloses the server, and would hand every connecting client the player profiles,
/// config.jsonc and the web UI's credential file. These pin the containment rule, including the
/// spellings of "the game root" that a string comparison would miss.
/// </summary>
[TestFixture]
public class ForbiddenSyncRootTests
{
    // A stand-in for <gameRoot>/SPT_Runtime. Nothing is touched on disk: the rule is pure path
    // arithmetic, which is exactly why ConfigUtil takes the server root as a parameter.
    private static readonly string ServerRoot =
        Path.Combine(Path.GetTempPath(), "modsync-test-gameroot", "SPT_Runtime");

    [TestCase("..", TestName = "bare parent")]
    [TestCase("../", TestName = "parent with separator")]
    [TestCase("./..", TestName = "parent via current dir")]
    [TestCase("../SPT_Runtime/..", TestName = "parent via a round trip")]
    [TestCase("../../", TestName = "above the game root")]
    [TestCase(".", TestName = "the server folder itself")]
    [TestCase("", TestName = "empty")]
    [TestCase("   ", TestName = "whitespace")]
    public void ForbiddenRoots_AreRejected(string syncPath)
    {
        Assert.That(ConfigUtil.IsForbiddenSyncRoot(syncPath, ServerRoot), Is.True,
            $"'{syncPath}' resolves to the game root or above and must never be servable");
    }

    [TestCase("../BepInEx/plugins", TestName = "client plugins catch-all")]
    [TestCase("../BepInEx/patchers", TestName = "client patchers catch-all")]
    [TestCase("../ModSync.Updater.exe", TestName = "the Updater builtin")]
    [TestCase("user/mods", TestName = "server mods")]
    [TestCase("../EscapeFromTarkov_Data/Managed", TestName = "the Managed builtin")]
    [TestCase("../BepInEx/plugins/SAIN", TestName = "a single mod folder")]
    public void LegitimateSyncPaths_AreAllowed(string syncPath)
    {
        Assert.That(ConfigUtil.IsForbiddenSyncRoot(syncPath, ServerRoot), Is.False,
            $"'{syncPath}' is a normal syncPath and must keep working");
    }

    [Test]
    public void WindowsSeparators_AreHandled()
    {
        // config.jsonc is hand-edited, and an admin on Windows may well write backslashes.
        Assert.Multiple(() =>
        {
            Assert.That(ConfigUtil.IsForbiddenSyncRoot(@"..\", ServerRoot), Is.True);
            Assert.That(ConfigUtil.IsForbiddenSyncRoot(@"..\BepInEx\plugins", ServerRoot), Is.False);
        });
    }

    [Test]
    public void TheGameRootAsAnAbsolutePath_IsAlsoRejected()
    {
        // Path.Combine returns an absolute second argument as-is, so an admin who pastes a full
        // path reaches the same place by a different route.
        var gameRoot = Path.GetFullPath(Path.Combine(ServerRoot, ".."));

        Assert.That(ConfigUtil.IsForbiddenSyncRoot(gameRoot, ServerRoot), Is.True);
    }
}
