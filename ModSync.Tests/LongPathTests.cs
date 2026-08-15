using ModSync.Utility;

namespace ModSync.Test;

[TestFixture]
public class LongPathTests
{
    // A realistically long staged path (~259 chars, over the 240 threshold), built from a REAL
    // worst-case file in a CORRECTLY-installed 7Bpencil.WeaponCamoAndStickers (the relative part
    // is ~176 chars on its own - auto-generated preview filenames). A deep install root plus the
    // ModSync_Data\PendingUpdates\ staging prefix tips it over the Windows MAX_PATH cliff.
    private static string LongDrivePath() =>
        @"C:\Users\Rob\Documents\Applications\SPTarkov\FriedTown\ModSync_Data\PendingUpdates\" +
        @"BepInEx\plugins\7Bpencil.WeaponCamoAndStickers\temp\previews\stickers\7Bpencil\blood\" +
        @"png-transparent-red-splat-blood-residue-bloodstain-miscellaneous-ink-computer-wallpaper.png";

    [Test]
    public void LongDrivePath_GetsExtendedPrefix()
    {
        var p = LongDrivePath();
        Assert.That(p.Length, Is.GreaterThan(240));   // guard: fixture really is "long"
        Assert.That(LongPath.Extended(p, windows: true), Is.EqualTo(@"\\?\" + p));
    }

    [Test]
    public void LongForwardSlashPath_NormalizedToBackslashesAndPrefixed()
    {
        var p = LongDrivePath().Replace('\\', '/');
        Assert.That(LongPath.Extended(p, windows: true), Is.EqualTo(@"\\?\" + LongDrivePath()));
    }

    [Test]
    public void LongUncPath_GetsUncExtendedPrefix()
    {
        var p = @"\\server\share" + LongDrivePath().Substring(2);   // \\server\share\Users\Rob\...
        Assert.That(LongPath.Extended(p, windows: true), Is.EqualTo(@"\\?\UNC\" + p.Substring(2)));
    }

    [Test]
    public void AlreadyPrefixed_LeftUnchanged()
    {
        var p = @"\\?\" + LongDrivePath();
        Assert.That(LongPath.Extended(p, windows: true), Is.EqualTo(p));
    }

    [Test]
    public void ShortPath_LeftUnchanged()
    {
        // Below the threshold - the common case, must stay byte-for-byte identical.
        Assert.That(LongPath.Extended(@"C:\SPT\BepInEx\plugins\mod\file.png", windows: true),
            Is.EqualTo(@"C:\SPT\BepInEx\plugins\mod\file.png"));
    }

    [Test]
    public void NonWindows_LeftUnchanged_EvenWhenLong()
    {
        // Native Linux/Docker headless: "\\?\" would corrupt the path, so never apply it.
        var p = LongDrivePath();
        Assert.That(LongPath.Extended(p, windows: false), Is.EqualTo(p));
    }

    [Test]
    public void RelativePath_LeftUnchanged()
    {
        var p = @"BepInEx\plugins\" + new string('a', 250) + @"\file.png";   // long but not rooted
        Assert.That(LongPath.Extended(p, windows: true), Is.EqualTo(p));
    }

    [Test]
    public void EmptyOrNull_LeftUnchanged()
    {
        Assert.That(LongPath.Extended("", windows: true), Is.EqualTo(""));
        Assert.That(LongPath.Extended(null!, windows: true), Is.Null);
    }
}
