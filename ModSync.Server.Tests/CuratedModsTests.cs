namespace ModSync.Server.Test;

/// <summary>
/// The editor shows MODS, not paths - one line for "Hollywood Graphics" rather than two for its
/// plugin folder and its .cfg. That grouping is recovered from the labels admins already write by
/// hand, so these pin the recovery rules against the shapes that occur in real configs.
///
/// Getting this wrong is not cosmetic: two rows that should be one mod would get separate controls
/// and could drift apart, which is exactly the hand-maintenance the grouping exists to remove.
/// </summary>
[TestFixture]
public class CuratedModsTests
{
    private static ConfigDraft Draft(string json) => ConfigEditorService.ParseDraft(json);

    [TestCase("(Optional) Hollywood Graphics", "Hollywood Graphics")]
    [TestCase("(Optional) Hollywood Graphics - config", "Hollywood Graphics")]
    [TestCase("Hollywood Graphics - config", "Hollywood Graphics")]
    [TestCase("(optional) no insurance", "no insurance")]
    [TestCase("Dynamic Maps", "Dynamic Maps")]
    public void ModIdentity_StripsTheLabelsAdminsAddByHand(string name, string expected) =>
        Assert.That(ConfigEditorService.ModIdentity(new SyncPathRow { Name = name }), Is.EqualTo(expected));

    [TestCase("DrakiaXYZ-Waypoints")]
    [TestCase("Corter-ModSync")]
    [TestCase("acidphantasm-botplacementsystem")]
    public void ModIdentity_LeavesHyphensThatArePartOfTheName(string name) =>
        Assert.That(ConfigEditorService.ModIdentity(new SyncPathRow { Name = name }), Is.EqualTo(name));

    [Test]
    public void ModIdentity_FallsBackToThePathWhenThereIsNoName()
    {
        var row = new SyncPathRow { Path = "../BepInEx/plugins/SAIN", Name = "" };
        Assert.That(ConfigEditorService.ModIdentity(row), Is.EqualTo("../BepInEx/plugins/SAIN"));
    }

    [Test]
    public void Curated_GroupsAModWithItsConfigFile()
    {
        // Straight out of a live config: the pair an admin currently keeps in step by hand.
        var draft = Draft("""
        {
          "syncPaths": [
            "../BepInEx/plugins",
            { "path": "../BepInEx/plugins/HollywoodGraphics", "name": "(Optional) Hollywood Graphics", "enabled": false },
            { "path": "../BepInEx/config/com.janky.hollywoodgraphics.cfg", "name": "(Optional) Hollywood Graphics - config", "enabled": false }
          ]
        }
        """);

        var curated = ConfigEditorService.Curated(draft);

        Assert.Multiple(() =>
        {
            Assert.That(curated, Has.Count.EqualTo(1), "one mod, not two entries");
            Assert.That(curated[0].Label, Is.EqualTo("Hollywood Graphics"));
            Assert.That(curated[0].Rows, Has.Count.EqualTo(2));
            Assert.That(curated[0].Rows[0].Path, Is.EqualTo("../BepInEx/plugins/HollywoodGraphics"),
                "the mod itself leads, so the row the admin sees is the plugin and not its config");
        });
    }

    [Test]
    public void Curated_ExcludesTheCatchAlls()
    {
        // The three catch-alls are section 1 of the editor. If they leaked into section 2 an admin
        // could be offered "starts unticked" on the folder holding every mod they need to connect.
        var draft = Draft("""
        {
          "syncPaths": ["../BepInEx/plugins", "../BepInEx/patchers", "../BepInEx/config"]
        }
        """);

        Assert.That(ConfigEditorService.Curated(draft), Is.Empty);
    }

    [Test]
    public void Curated_TreatsBackslashSpelledCatchAllsAsCatchAlls()
    {
        // A config authored on Windows spells these with backslashes, and the server treats the two
        // separators as interchangeable everywhere else.
        var draft = Draft("""{ "syncPaths": ["..\\BepInEx\\plugins"] }""");

        Assert.Multiple(() =>
        {
            Assert.That(ConfigEditorService.IsCatchAll("..\\BepInEx\\plugins"), Is.True);
            Assert.That(ConfigEditorService.Curated(draft), Is.Empty);
        });
    }

    [Test]
    public void Curated_KeepsFileOrderSoTheEditorNeverReshufflesTheAdminsList()
    {
        var draft = Draft("""
        {
          "syncPaths": [
            { "path": "../BepInEx/plugins/Zebra", "name": "Zebra" },
            { "path": "../BepInEx/plugins/Alpha", "name": "Alpha" }
          ]
        }
        """);

        Assert.That(ConfigEditorService.Curated(draft).Select(m => m.Label), Is.EqualTo(new[] { "Zebra", "Alpha" }));
    }

    [Test]
    public void Curated_DoesNotGroupTwoDifferentModsFromTheSameAuthor()
    {
        var draft = Draft("""
        {
          "syncPaths": [
            { "path": "../BepInEx/plugins/HollywoodGraphics", "name": "(Optional) Hollywood Graphics" },
            { "path": "../BepInEx/plugins/HollywoodFX", "name": "(Optional) Hollywood FX" }
          ]
        }
        """);

        Assert.That(ConfigEditorService.Curated(draft), Has.Count.EqualTo(2));
    }
}
