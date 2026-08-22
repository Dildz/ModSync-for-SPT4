namespace ModSync.Server.Test;

/// <summary>
/// What the save button actually puts in an admin's file.
///
/// The risk is never a crash - it is a save that quietly rewrites something nobody touched. So these
/// mostly assert about what is ABSENT: defaults that must not be written, entries that must stay
/// one-liners, and a round trip that has to come back identical.
/// </summary>
[TestFixture]
public class ConfigSerializeTests
{
    private static ConfigDraft RoundTrip(string jsonc) =>
        ConfigEditorService.ParseDraft(ConfigEditorService.Serialize(ConfigEditorService.ParseDraft(jsonc)));

    [Test]
    public void ABareStringStaysABareString()
    {
        // The whole reason SyncPathRow remembers its original form. A config of nine tidy one-line
        // entries must not come back as nine blocks because someone toggled something elsewhere.
        var json = ConfigEditorService.Serialize(ConfigEditorService.ParseDraft(
            """{ "syncPaths": ["../BepInEx/plugins", "../BepInEx/patchers"] }"""));

        Assert.That(json, Does.Contain("\"../BepInEx/plugins\""));
        Assert.That(json, Does.Not.Contain("\"path\""), "nothing here needed promoting to an object");
    }

    [Test]
    public void AnObjectEntryThatIsAllDefaultsCollapsesToABareString()
    {
        // The server treats the two spellings identically, so the shorter one is the honest output.
        var json = ConfigEditorService.Serialize(ConfigEditorService.ParseDraft(
            """{ "syncPaths": [{ "path": "../BepInEx/plugins", "enabled": true, "headless": true }] }"""));

        Assert.That(json, Does.Not.Contain("\"enabled\""));
        Assert.That(json, Does.Not.Contain("\"headless\""));
    }

    [Test]
    public void OnlyChangedOptionsAreWritten()
    {
        var draft = ConfigEditorService.ParseDraft("""{ "syncPaths": [] }""");
        draft.SyncPaths.Add(new SyncPathRow
        {
            Path = "../BepInEx/plugins/DynamicMaps",
            Name = "Dynamic Maps",
            Enabled = false,
        });

        var json = ConfigEditorService.Serialize(draft);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"enabled\": false"));
            Assert.That(json, Does.Contain("\"name\": \"Dynamic Maps\""));
            Assert.That(json, Does.Not.Contain("\"restartRequired\""), "still at its default");
            Assert.That(json, Does.Not.Contain("\"headless\""), "still at its default");
            Assert.That(json, Does.Not.Contain("\"silent\""), "still at its default");
            Assert.That(json, Does.Not.Contain("\"enforced\""), "still at its default");
        });
    }

    [Test]
    public void ANameEqualToThePathIsNotWrittenBack()
    {
        // ParseDraft defaults Name to Path, so writing it out would add a key to every entry that
        // never had one.
        var draft = ConfigEditorService.ParseDraft("""{ "syncPaths": [] }""");
        draft.SyncPaths.Add(new SyncPathRow { Path = "../BepInEx/plugins/SAIN", Name = "../BepInEx/plugins/SAIN", Silent = true });

        Assert.That(ConfigEditorService.Serialize(draft), Does.Not.Contain("\"name\""));
    }

    [Test]
    public void EveryTopLevelKeyIsWrittenEvenWhenEmpty()
    {
        // ConfigUtil tolerates missing keys, but an admin opening the file should see the shape of
        // what they can set rather than having to remember the names.
        var json = ConfigEditorService.Serialize(ConfigEditorService.ParseDraft("""{ "syncPaths": [] }"""));

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"syncPaths\""));
            Assert.That(json, Does.Contain("\"exclusions\""));
            Assert.That(json, Does.Contain("\"headlessIncludes\""));
            Assert.That(json, Does.Contain("\"managedIncludes\""));
            Assert.That(json, Does.Contain("\"headlessManagedIncludes\""));
        });
    }

    [Test]
    public void ARealConfigSurvivesARoundTrip()
    {
        // Shaped like a live 4.0 config: catch-alls, a carve-out with baseFiles, exclusions
        // including globs, and a headless allowlist.
        const string original = """
        {
          "syncPaths": [
            "../BepInEx/plugins",
            "../BepInEx/patchers",
            { "path": "../BepInEx/patchers/TarkovDLSS45", "name": "(Optional) Tarkov DLSS 4.5", "enabled": false, "headless": false,
              "baseFiles": ["../EscapeFromTarkov_Data/Plugins/x86_64/nvngx_dlss.dll"] },
            { "path": "../BepInEx/plugins/NoInsurance.dll", "name": "(Optional) No Insurance", "enabled": false, "headless": false }
          ],
          "exclusions": ["../BepInEx/plugins/spt", "**/*.nosync", "**/.git"],
          "headlessIncludes": ["../BepInEx/plugins/Fika", "../BepInEx/plugins/SAIN"],
          "managedIncludes": [],
          "headlessManagedIncludes": []
        }
        """;

        var once = ConfigEditorService.ParseDraft(original);
        var twice = RoundTrip(original);

        Assert.Multiple(() =>
        {
            Assert.That(twice.SyncPaths, Has.Count.EqualTo(once.SyncPaths.Count));
            Assert.That(twice.Exclusions, Is.EqualTo(once.Exclusions));
            Assert.That(twice.HeadlessIncludes, Is.EqualTo(once.HeadlessIncludes));

            for (var i = 0; i < once.SyncPaths.Count; i++)
            {
                var a = once.SyncPaths[i];
                var b = twice.SyncPaths[i];

                Assert.That(b.Path, Is.EqualTo(a.Path), $"path {i}");
                Assert.That(b.Name, Is.EqualTo(a.Name), $"name {i}");
                Assert.That(b.Enabled, Is.EqualTo(a.Enabled), $"enabled {i}");
                Assert.That(b.Optional, Is.EqualTo(a.Optional), $"optional {i}");
                Assert.That(b.Enforced, Is.EqualTo(a.Enforced), $"enforced {i}");
                Assert.That(b.Silent, Is.EqualTo(a.Silent), $"silent {i}");
                Assert.That(b.RestartRequired, Is.EqualTo(a.RestartRequired), $"restartRequired {i}");
                Assert.That(b.Headless, Is.EqualTo(a.Headless), $"headless {i}");
                Assert.That(b.BaseFiles, Is.EqualTo(a.BaseFiles), $"baseFiles {i}");
            }
        });
    }

    [Test]
    public void SerializedOutputIsParseableByTheServerItself()
    {
        // Serialize writes strict JSON; the loader reads JSONC. The one must always be readable by
        // the other, or the first save bricks the server on its next boot.
        var json = ConfigEditorService.Serialize(ConfigEditorService.ParseDraft("""
        { "syncPaths": ["../BepInEx/plugins"], "exclusions": ["**/*.nosync"] }
        """));

        Assert.DoesNotThrow(() => ConfigEditorService.ParseDraft(json));
    }
}
