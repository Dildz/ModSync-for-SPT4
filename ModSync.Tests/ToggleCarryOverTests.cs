using BepInEx.Configuration;
using ModSync.Utility;

namespace ModSync.Test;

/// <summary>
/// Tests for Migrator.CarryToggleValues - keeping a player's F12 choices when the key a toggle is
/// stored under changes from the syncpath's NAME to its PATH.
///
/// Why it matters: names are server config an admin edits freely, so a rename used to reset every
/// player's choice for that mod and leave an orphan line behind. Harmless while the only toggles
/// were opt-IN, but `optional` made a reset actively wrong - an opt-OUT path re-seeds to ticked,
/// so a mod the player deliberately refused would reinstall itself.
/// </summary>
[TestFixture]
public class ToggleCarryOverTests
{
    private string _dir = null!;
    private string _cfgPath = null!;
    private readonly List<string> _reported = [];

    [SetUp]
    public void SetUp()
    {
        _dir = TestUtils.GetTemporaryDirectory();
        _cfgPath = Path.Combine(_dir, "corter.modsync.cfg");
        _reported.Clear();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private void WriteCfg(string body) => File.WriteAllText(_cfgPath, "[Synced Paths]\n\n" + body);

    private Dictionary<string, bool> Carry(params SyncPath[] syncPaths) =>
        Migrator.CarryToggleValues(new ConfigFile(_cfgPath, saveOnInit: false), [.. syncPaths], _reported.Add);

    // ── ReadSavedKeys: what the file already holds ────────────────────────────

    [Test]
    public void ReadsOnlyKeysFromTheRequestedSection()
    {
        var text = "[General]\n\nDelete Removed Files = true\n\n[Synced Paths]\n\n## a comment\nuser/mods = false\nBepInEx/plugins = true\n";

        var keys = Migrator.ReadSavedKeys(text, "Synced Paths");

        Assert.Multiple(() =>
        {
            Assert.That(keys, Is.EquivalentTo(new[] { "user/mods", "BepInEx/plugins" }));
            Assert.That(keys, Does.Not.Contain("Delete Removed Files"), "another section's keys must not leak in");
        });
    }

    [Test]
    public void ReadsKeysContainingPathsAndSpaces()
    {
        // Both forms occur in the wild: a friendly name with spaces and brackets, and a raw path.
        var text = "[Synced Paths]\n\n(Optional) server mods = false\n../BepInEx/config = true\n";

        Assert.That(Migrator.ReadSavedKeys(text, "Synced Paths"),
            Is.EquivalentTo(new[] { "(Optional) server mods", "../BepInEx/config" }));
    }

    // ── the carry itself ──────────────────────────────────────────────────────

    [Test]
    public void CarriesTheChoiceSavedUnderTheOldName()
    {
        WriteCfg("(Optional) server mods = false\n");
        var syncPath = new SyncPath("SPT/user/mods", name: "(Optional) server mods");

        var carried = Carry(syncPath);

        Assert.Multiple(() =>
        {
            Assert.That(carried["SPT/user/mods"], Is.False, "the player opted out; that must survive the re-key");
            Assert.That(_reported, Has.Count.EqualTo(1), "and the player is told, once");
        });
    }

    [Test]
    public void DropsTheStaleLineSoItCannotLingerAsAnOrphan()
    {
        WriteCfg("(Optional) server mods = false\n");
        var config = new ConfigFile(_cfgPath, saveOnInit: false);

        Migrator.CarryToggleValues(config, [new SyncPath("SPT/user/mods", name: "(Optional) server mods")], _reported.Add);
        config.Save();

        Assert.That(File.ReadAllText(_cfgPath), Does.Not.Contain("(Optional) server mods"));
    }

    [Test]
    public void LeavesAnAlreadyMigratedToggleAlone()
    {
        // Second launch: the path key is already there. Touching it would overwrite a fresh
        // choice with a stale one from the old name-keyed line.
        WriteCfg("SPT/user/mods = true\n(Optional) server mods = false\n");

        var carried = Carry(new SyncPath("SPT/user/mods", name: "(Optional) server mods"));

        Assert.Multiple(() =>
        {
            Assert.That(carried, Is.Empty);
            Assert.That(_reported, Is.Empty, "nothing changed, so say nothing");
        });
    }

    [Test]
    public void IgnoresAPathWithNoSavedChoiceAtAll()
    {
        // A mod the player has never seen. It must fall through to the normal seeded default.
        WriteCfg("BepInEx/plugins = true\n");

        Assert.That(Carry(new SyncPath("BepInEx/plugins/NewMod", name: "Brand New Mod")), Is.Empty);
    }

    [Test]
    public void IgnoresAnUnnamedPathWhoseKeyIsUnchanged()
    {
        // name defaults to the path, so there is nothing to migrate.
        WriteCfg("BepInEx/plugins = false\n");

        Assert.That(Carry(new SyncPath("BepInEx/plugins")), Is.Empty);
    }

    [Test]
    public void MissingConfigFileIsNotAnError()
    {
        // First ever launch. Must be silent, not throw.
        Assert.That(Carry(new SyncPath("SPT/user/mods", name: "(Optional) server mods")), Is.Empty);
    }
}
