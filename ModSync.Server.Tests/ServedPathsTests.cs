using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for <see cref="ServedPaths.Resolve"/> - the tombstone policy.
///
/// Why tombstones exist: deleting a mod's syncPath entry used to strand its files on every
/// client forever. The path never reached the client's diff, so no removal was computed, and
/// the client's next launch erased the PreviousSync record that licenses a removal. These
/// tests pin the two halves of the rule that keeps that fix from over-reaching:
/// only retire a path whose FILES are gone too, and stop advertising it eventually.
/// </summary>
[TestFixture]
public class ServedPathsTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ServedPath Served(string path, DateTime? retiredAt = null) =>
        new(path, $"Mod at {path}", Silent: false, RestartRequired: true, Headless: true, retiredAt);

    // Nothing on disk - the case a retired mod is actually in.
    private static bool Gone(string _) => false;
    private static bool Present(string _) => true;

    [Test]
    public void RetiresPathThatLeftTheConfigWithItsFiles()
    {
        var (tombstones, record) = ServedPaths.Resolve([Served("../BepInEx/plugins/OldMod")], [], Gone, Now);

        Assert.Multiple(() =>
        {
            Assert.That(tombstones, Has.Count.EqualTo(1));
            Assert.That(tombstones[0].path, Is.EqualTo("../BepInEx/plugins/OldMod"));
            Assert.That(record[0].RetiredAt, Is.EqualTo(Now), "the retirement clock starts now");
        });
    }

    [Test]
    public void DoesNotRetirePathWhoseFilesAreStillOnTheServer()
    {
        // Entry removed but files kept is a DIFFERENT intent - "stop making this optional, let
        // the catch-all sync it to everyone". Tombstoning here would delete a mod the admin
        // still wants served, which is the one way this feature could do real damage.
        var (tombstones, record) = ServedPaths.Resolve([Served("../BepInEx/plugins/StillHere")], [], Present, Now);

        Assert.Multiple(() =>
        {
            Assert.That(tombstones, Is.Empty);
            Assert.That(record, Is.Empty, "and we stop tracking it - the catch-all owns it now");
        });
    }

    [Test]
    public void KeepsAdvertisingATombstoneIndefinitely()
    {
        // Deliberately no expiry. A catch-all entry lives in the config forever, which is why the
        // lazy setup removes a mod correctly whenever a player next logs in; a timed tombstone
        // would be the only thing here that could be MISSED, stranding the mod for good on anyone
        // who was away too long.
        var retiredAt = Now - TimeSpan.FromDays(400);
        var (tombstones, record) = ServedPaths.Resolve([Served("../BepInEx/plugins/OldMod", retiredAt)], [], Gone, Now);

        Assert.Multiple(() =>
        {
            Assert.That(tombstones, Has.Count.EqualTo(1));
            Assert.That(tombstones[0].name, Is.EqualTo("Mod at ../BepInEx/plugins/OldMod"),
                "the client's update prompt must still name the mod, not the raw path");
            Assert.That(tombstones[0].restartRequired, Is.True, "and behave as the entry did when it was live");
            Assert.That(record[0].RetiredAt, Is.EqualTo(retiredAt), "the original retirement date is preserved, not refreshed");
        });
    }

    [Test]
    public void StopsTombstoningWhenTheAdminPutsThePathBack()
    {
        var restored = new SyncPath("../BepInEx/plugins/OldMod", name: "Back Again");
        var retiredAt = Now - TimeSpan.FromDays(1);

        var (tombstones, record) = ServedPaths.Resolve([Served("../BepInEx/plugins/OldMod", retiredAt)], [restored], Gone, Now);

        Assert.Multiple(() =>
        {
            Assert.That(tombstones, Is.Empty, "it's a live path again, not a retired one");
            Assert.That(record, Has.Count.EqualTo(1));
            Assert.That(record[0].RetiredAt, Is.Null);
            Assert.That(record[0].Name, Is.EqualTo("Back Again"), "and the record follows the current config");
        });
    }

    [Test]
    public void NoPreviousRecordProducesNoTombstones()
    {
        // First boot after updating, or the admin deleted the record file. A missing record must
        // only ever cost us a removal, never cause one - losing it degrades to old behaviour.
        var current = new SyncPath("../BepInEx/plugins/SomeMod");

        var (tombstones, record) = ServedPaths.Resolve([], [current], Gone, Now);

        Assert.Multiple(() =>
        {
            Assert.That(tombstones, Is.Empty);
            Assert.That(record, Has.Count.EqualTo(1), "but we start tracking from now on");
            Assert.That(record[0].Path, Is.EqualTo("../BepInEx/plugins/SomeMod"));
        });
    }

    // ── the serving half: a tombstone must reach the client as an EMPTY path ──

    [Test]
    public async Task TombstoneIsServedEmptyAndItsFolderIsNeverWalked()
    {
        // Deliberately gives the tombstone a folder that still has files in it. In reality a
        // tombstone's folder is gone - this is a mutation guard: drop the IsTombstone check in
        // HashModFilesAsync and these files get served, which would RE-INSTALL the retired mod
        // instead of removing it.
        var dir = TestUtils.GetTemporaryDirectory();
        File.WriteAllText(Path.Combine(dir, "RetiredMod.dll"), "should never be served");

        try
        {
            var config = new Config(
                syncPaths: [],
                exclusions: [],
                headlessIncludes: [],
                managedIncludes: [],
                headlessManagedIncludes: [],
                tombstones: [dir]);

            var result = await new SyncUtil(config, new NoOpLogger<SyncUtil>())
                .HashModFilesAsync([new SyncPath(dir)], isHeadless: false, isActive: _ => true);

            Assert.Multiple(() =>
            {
                Assert.That(result.Keys, Has.Count.EqualTo(1),
                    "the path must still be ADVERTISED - that's what triggers the client-side uninstall");
                Assert.That(result.Values.SelectMany(files => files.Keys), Is.Empty,
                    "but with no files, which is what makes the client remove its copy");
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void RecordCarriesTheOptionsAClientNeedsToPromptProperly()
    {
        var current = new SyncPath("user/mods", name: "(Optional) server mods", silent: true, restartRequired: false, headless: false);

        var (_, record) = ServedPaths.Resolve([], [current], Gone, Now);

        Assert.Multiple(() =>
        {
            Assert.That(record[0].Name, Is.EqualTo("(Optional) server mods"));
            Assert.That(record[0].Silent, Is.True);
            Assert.That(record[0].RestartRequired, Is.False);
            Assert.That(record[0].Headless, Is.False);
        });
    }
}
