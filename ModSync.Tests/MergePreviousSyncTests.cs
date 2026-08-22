using ModSync.Utility;
using NUnit.Framework;

namespace ModSync.Tests;

using SyncPathModFiles = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, ModFile>>;

/// <summary>
/// PreviousSync.json is the record of what the server last offered, and it is the ONLY thing
/// that licenses ModSync to remove a file - a path with no entry can never have anything
/// removed under it.
///
/// A self-update run sees nothing but ModSync's own components, so writing that result
/// wholesale erases every mod path's record. The visible symptom is subtle and easy to
/// misdiagnose: after an update, un-ticking a mod in F12 silently does nothing for exactly one
/// launch. Merging keeps the records intact.
/// </summary>
[TestFixture]
public class MergePreviousSyncTests
{
    private static SyncPathModFiles Make(params (string path, string file)[] entries)
    {
        var result = new SyncPathModFiles(System.StringComparer.OrdinalIgnoreCase);

        foreach (var (path, file) in entries)
            result[path] = new System.Collections.Generic.Dictionary<string, ModFile> { [file] = new(hash: "h", directory: false) };

        return result;
    }

    [Test]
    public void PathsNotInThisRun_KeepTheirRecords()
    {
        // The whole point: a self-update must not cost the player their mod records.
        var previous = Make(
            (@"BepInEx\plugins", "SAIN.dll"),
            (@"BepInEx\plugins\DynamicMaps", "dm.dll"),
            (@"BepInEx\plugins\Corter-ModSync", "Corter-ModSync.dll"));

        var selfUpdateOnly = Make((@"BepInEx\plugins\Corter-ModSync", "Corter-ModSync.dll"));

        var merged = Sync.MergePreviousSync(previous, selfUpdateOnly);

        Assert.Multiple(() =>
        {
            Assert.That(merged, Has.Count.EqualTo(3), "mod paths were dropped - removals would silently stop working");
            Assert.That(merged, Does.ContainKey(@"BepInEx\plugins"));
            Assert.That(merged, Does.ContainKey(@"BepInEx\plugins\DynamicMaps"));
        });
    }

    [Test]
    public void PathsInThisRun_GetTheFreshRecord()
    {
        var previous = Make((@"BepInEx\plugins\Corter-ModSync", "old.dll"));
        var current = Make((@"BepInEx\plugins\Corter-ModSync", "new.dll"));

        var merged = Sync.MergePreviousSync(previous, current);

        Assert.Multiple(() =>
        {
            Assert.That(merged[@"BepInEx\plugins\Corter-ModSync"], Does.ContainKey("new.dll"));
            Assert.That(merged[@"BepInEx\plugins\Corter-ModSync"], Does.Not.ContainKey("old.dll"),
                "a covered path must be REPLACED, not union'd - stale files would linger in the record forever");
        });
    }

    [Test]
    public void PathsSeenForTheFirstTime_AreAdded()
    {
        var previous = Make((@"BepInEx\plugins", "SAIN.dll"));
        var current = Make((@"ModSync.Updater.exe", "ModSync.Updater.exe"));

        var merged = Sync.MergePreviousSync(previous, current);

        Assert.That(merged, Has.Count.EqualTo(2));
    }

    [Test]
    public void EmptyPrevious_YieldsCurrent()
    {
        // First ever run: nothing to preserve.
        var current = Make((@"BepInEx\plugins\Corter-ModSync", "Corter-ModSync.dll"));

        var merged = Sync.MergePreviousSync(new SyncPathModFiles(), current);

        Assert.That(merged, Has.Count.EqualTo(1));
    }

    [Test]
    public void MergeIsCaseInsensitiveOnPaths()
    {
        // Wire paths come from the server; local records were written by an earlier build.
        // A casing difference must not produce two entries for the same path.
        var previous = Make((@"BepInEx\Plugins\Corter-ModSync", "old.dll"));
        var current = Make((@"bepinex\plugins\corter-modsync", "new.dll"));

        var merged = Sync.MergePreviousSync(previous, current);

        Assert.That(merged, Has.Count.EqualTo(1), "casing produced a duplicate path record");
    }
}
