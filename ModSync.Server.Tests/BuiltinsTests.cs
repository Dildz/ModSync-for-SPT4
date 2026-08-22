using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// ModSync's own three components are defined once in ModSync.Utility.Builtins and consumed by
/// BOTH sides: the server prepends them as builtin syncpaths, the client matches incoming wire
/// paths against them to decide whether a run is a self-update.
///
/// If those two views ever disagree, the client stops recognising its own components, never
/// self-updates, and an outdated plugin goes on interpreting a newer server's config - the
/// failure that made a v0.12.5 client offer to delete a hand-installed DynamicMaps. The patcher
/// has already been renamed once (v0.12.4), so this is a real risk, not a hypothetical one.
/// </summary>
[TestFixture]
public class BuiltinsTests
{
    [Test]
    public void ServerBuiltinConsts_ComeFromTheSharedSource()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConfigUtil.UpdaterSyncPath, Is.EqualTo(Builtins.UpdaterPath));
            Assert.That(ConfigUtil.PatcherSyncPath, Is.EqualTo(Builtins.PatcherPath));
        });
    }

    [Test]
    public void EveryBuiltin_IsRecognisedInWireForm()
    {
        // The round trip the client actually performs: server-relative path → wire → match.
        foreach (var builtin in Builtins.All)
        {
            var wire = Builtins.ToWire(builtin);

            Assert.That(Builtins.IsBuiltinWirePath(wire), Is.True,
                $"builtin '{builtin}' is not recognised as '{wire}' - self-update would skip it");
            Assert.That(wire, Does.Not.StartWith(".."),
                "wire paths are game-root-relative");
        }
    }

    [Test]
    public void ToWire_MatchesThePathTranslationTheListenerUses()
    {
        // PathExt.ToWirePath(PathExt.WinPath(x)) is what /modsync/paths applies. Builtins.ToWire
        // must produce the identical string or the client's comparison silently fails.
        foreach (var builtin in Builtins.All)
        {
            Assert.That(
                Builtins.ToWire(builtin),
                Is.EqualTo(PathExt.ToWirePath(PathExt.WinPath(builtin))),
                $"Builtins.ToWire disagrees with the listener's translation for '{builtin}'");
        }
    }

    [Test]
    public void IsBuiltinWirePath_IsSeparatorAndCaseInsensitive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Builtins.IsBuiltinWirePath(@"BepInEx\plugins\Corter-ModSync"), Is.True);
            Assert.That(Builtins.IsBuiltinWirePath("BepInEx/plugins/Corter-ModSync"), Is.True);
            Assert.That(Builtins.IsBuiltinWirePath(@"bepinex\plugins\corter-modsync"), Is.True);
        });
    }

    [Test]
    public void IsBuiltinWirePath_DoesNotMatchOrdinaryMods()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Builtins.IsBuiltinWirePath(@"BepInEx\plugins"), Is.False,
                "the plugins catch-all is not a builtin");
            Assert.That(Builtins.IsBuiltinWirePath(@"BepInEx\patchers"), Is.False);
            Assert.That(Builtins.IsBuiltinWirePath(@"BepInEx\plugins\SAIN"), Is.False);
            Assert.That(Builtins.IsBuiltinWirePath(@"BepInEx\patchers\TarkovDLSS45"), Is.False);
        });
    }

}
