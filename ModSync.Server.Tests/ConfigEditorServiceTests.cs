using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// The editor reads the admin's file and will eventually write it back, so the risk here is not that
/// it crashes - it is that it quietly changes something nobody asked it to change. These pin the two
/// places that can happen: losing the bare-string form of an entry, and drifting away from the
/// defaults ConfigUtil applies when it loads the same file.
/// </summary>
[TestFixture]
public class ConfigEditorServiceTests
{
    [Test]
    public void BareStringEntry_IsRememberedAsBare_AndCountsAsAllDefaults()
    {
        var draft = ConfigEditorService.ParseDraft("""{ "syncPaths": ["../BepInEx/plugins"] }""");

        Assert.That(draft.SyncPaths, Has.Count.EqualTo(1));
        var row = draft.SyncPaths[0];

        Assert.Multiple(() =>
        {
            Assert.That(row.Path, Is.EqualTo("../BepInEx/plugins"));
            Assert.That(row.WasBareString, Is.True, "the entry's original form has to survive the round trip");
            Assert.That(row.IsAllDefaults, Is.True, "a bare string is by definition every option at its default");
        });
    }

    [Test]
    public void ObjectEntry_ReadsEveryOption()
    {
        var draft = ConfigEditorService.ParseDraft("""
        {
          "syncPaths": [{
            "path": "../BepInEx/plugins/SAIN",
            "name": "SAIN",
            "enabled": false,
            "optional": true,
            "enforced": true,
            "silent": true,
            "restartRequired": false,
            "headless": false,
            "baseFiles": ["../EscapeFromTarkov_Data/Managed/Example.dll"]
          }]
        }
        """);

        var row = draft.SyncPaths[0];

        Assert.Multiple(() =>
        {
            Assert.That(row.Name, Is.EqualTo("SAIN"));
            Assert.That(row.Enabled, Is.False);
            Assert.That(row.Optional, Is.True);
            Assert.That(row.Enforced, Is.True);
            Assert.That(row.Silent, Is.True);
            Assert.That(row.RestartRequired, Is.False);
            Assert.That(row.Headless, Is.False);
            Assert.That(row.BaseFiles, Has.Count.EqualTo(1));
            Assert.That(row.WasBareString, Is.False);
            Assert.That(row.IsAllDefaults, Is.False);
        });
    }

    [Test]
    public void ObjectEntry_WithNothingButAPath_CanCollapseBackToABareString()
    {
        // An admin who adds an option and then removes it again should not be left with a
        // permanently expanded entry.
        var draft = ConfigEditorService.ParseDraft("""{ "syncPaths": [{ "path": "user/mods" }] }""");

        Assert.That(draft.SyncPaths[0].IsAllDefaults, Is.True);
    }

    [Test]
    public void RowDefaults_MatchTheDefaultsConfigUtilApplies()
    {
        // ConfigUtil.BuildSyncPath fills unspecified options from its own constants, and SyncPath's
        // constructor carries the same set. If either moves without this row moving with it, the
        // editor would write an option that changes behaviour while looking like a no-op edit.
        var fromServer = new SyncPath("user/mods");
        var fromEditor = ConfigEditorService.ParseDraft("""{ "syncPaths": ["user/mods"] }""").SyncPaths[0];

        Assert.Multiple(() =>
        {
            Assert.That(fromEditor.Enabled, Is.EqualTo(fromServer.enabled), "enabled default drifted");
            Assert.That(fromEditor.Optional, Is.EqualTo(fromServer.optional), "optional default drifted");
            Assert.That(fromEditor.Enforced, Is.EqualTo(fromServer.enforced), "enforced default drifted");
            Assert.That(fromEditor.Silent, Is.EqualTo(fromServer.silent), "silent default drifted");
            Assert.That(fromEditor.RestartRequired, Is.EqualTo(fromServer.restartRequired), "restartRequired default drifted");
            Assert.That(fromEditor.Headless, Is.EqualTo(fromServer.headless), "headless default drifted");
            Assert.That(fromEditor.Name, Is.EqualTo(fromServer.name), "name should fall back to the path");
        });
    }

    [Test]
    public void MissingSectionsParseAsEmpty_NotNull()
    {
        var draft = ConfigEditorService.ParseDraft("{}");

        Assert.Multiple(() =>
        {
            Assert.That(draft.SyncPaths, Is.Empty);
            Assert.That(draft.Exclusions, Is.Empty);
            Assert.That(draft.HeadlessIncludes, Is.Empty);
            Assert.That(draft.ManagedIncludes, Is.Empty);
            Assert.That(draft.HeadlessManagedIncludes, Is.Empty);
        });
    }

    [Test]
    public void CommentsAndTrailingCommas_AreTolerated()
    {
        // Every config in the wild is ~85% comments, so the editor has to read one.
        var draft = ConfigEditorService.ParseDraft("""
        {
          // a comment
          "exclusions": [
            "**/.git",
          ],
        }
        """);

        Assert.That(draft.Exclusions, Is.EqualTo(new[] { "**/.git" }));
    }

    [Test]
    public void OptionalOnACatchAll_IsBlocking()
    {
        var draft = ConfigEditorService.ParseDraft(
            """{ "syncPaths": [{ "path": "../BepInEx/plugins", "optional": true }] }""");

        var warning = ConfigEditorService.Validate(draft).SingleOrDefault(w => w.Blocking);

        Assert.That(warning, Is.Not.Null, "unticking a catch-all strips the mods a player needs to connect");
        Assert.That(warning!.Message, Does.Contain("catch-all"));
    }

    [Test]
    public void OptionalPlusEnforced_WarnsButDoesNotBlock()
    {
        var draft = ConfigEditorService.ParseDraft(
            """{ "syncPaths": [{ "path": "user/mods", "optional": true, "enforced": true }] }""");

        var warnings = ConfigEditorService.Validate(draft);

        Assert.That(warnings, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(warnings[0].Blocking, Is.False, "contradictory, not dangerous - the server tolerates it");
            Assert.That(warnings[0].Message, Does.Contain("Enforced wins"));
        });
    }

    [Test]
    public void DuplicatePaths_AreReportedOnce_AndSeparatorInsensitively()
    {
        var draft = ConfigEditorService.ParseDraft("""
        { "syncPaths": ["../BepInEx/config", { "path": "..\\BepInEx\\config", "silent": true }] }
        """);

        var warnings = ConfigEditorService.Validate(draft);

        Assert.That(warnings, Has.Exactly(1).Items);
        Assert.That(warnings[0].Message, Does.Contain("listed 2 times"));
    }

    [Test]
    public void EmptyPath_IsBlocking()
    {
        var draft = ConfigEditorService.ParseDraft("""{ "syncPaths": [{ "enabled": true }] }""");

        Assert.That(ConfigEditorService.Validate(draft).Single().Blocking, Is.True);
    }

    [Test]
    public void AValidConfig_ProducesNoWarnings()
    {
        var draft = ConfigEditorService.ParseDraft("""
        {
          "syncPaths": ["../BepInEx/plugins", { "path": "user/mods", "optional": true }],
          "exclusions": ["**/.git"]
        }
        """);

        Assert.That(ConfigEditorService.Validate(draft), Is.Empty);
    }
}
