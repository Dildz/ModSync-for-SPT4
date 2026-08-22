namespace ModSync.Server.Test;

/// <summary>
/// The editor offers real mods to pick from instead of a text box, and it guesses which BepInEx
/// config file belongs to each one. The guess is the risky part: it is only right about two thirds
/// of the time, so what these pin down is that it stays HONEST about that - a confident wrong answer
/// would attach a stranger's config file to a mod and send it to every player.
///
/// The names below are taken from a real 4.0 server (84 plugin folders, 50 config files), including
/// the ones the matcher genuinely cannot solve. Those are asserted as null on purpose: "no guess" is
/// the correct output, and if a future change starts returning something for them, that is a
/// regression rather than an improvement unless it is right.
/// </summary>
[TestFixture]
public class ClientModScannerTests
{
    // Real config file names, in the shapes that actually occur: reverse-DNS, vendor-prefixed,
    // and bare.
    private static readonly List<string> ConfigFiles =
    [
        "../BepInEx/config/com.janky.hollywoodgraphics.cfg",
        "../BepInEx/config/com.janky.hollywoodfx.cfg",
        "../BepInEx/config/com.mpstark.PlayerEncumbranceBar.cfg",
        "../BepInEx/config/com.mpstark.dynamicmaps.cfg",
        "../BepInEx/config/7Bpencil.WeaponCamoAndStickers.cfg",
        "../BepInEx/config/com.acidphantasm.botplacementsystem.cfg",
        "../BepInEx/config/xyz.drakia.waypoints.cfg",
        "../BepInEx/config/com.borkel.nvgmasks.cfg",
        "../BepInEx/config/HealingAutoCancel.cfg",
    ];

    [TestCase("HollywoodGraphics", "../BepInEx/config/com.janky.hollywoodgraphics.cfg")]
    [TestCase("PlayerEncumbranceBar", "../BepInEx/config/com.mpstark.PlayerEncumbranceBar.cfg")]
    [TestCase("DynamicMaps", "../BepInEx/config/com.mpstark.dynamicmaps.cfg")]
    [TestCase("HealingAutoCancel", "../BepInEx/config/HealingAutoCancel.cfg")]
    public void MatchesAcrossSeparatorAndCasingConventions(string mod, string expected) =>
        Assert.That(ClientModScanner.SuggestConfigFile(mod, ConfigFiles), Is.EqualTo(expected));

    [Test]
    public void MatchesWhenTheFolderNameIsItselfVendorPrefixed()
    {
        // The whole folder name is the match here, not a trailing segment of it.
        Assert.That(
            ClientModScanner.SuggestConfigFile("7Bpencil.WeaponCamoAndStickers", ConfigFiles),
            Is.EqualTo("../BepInEx/config/7Bpencil.WeaponCamoAndStickers.cfg"));
    }

    [Test]
    public void MatchesWhenTheFolderUsesHyphensAndTheConfigUsesDots()
    {
        Assert.That(
            ClientModScanner.SuggestConfigFile("acidphantasm-botplacementsystem", ConfigFiles),
            Is.EqualTo("../BepInEx/config/com.acidphantasm.botplacementsystem.cfg"));
    }

    [Test]
    public void DeclinesToGuessWhenTheWordsAreInADifferentOrder()
    {
        // xyz.drakia.waypoints.cfg really does belong to DrakiaXYZ-Waypoints, but recovering that
        // needs word-order-insensitive matching, and every scheme that gets it also starts pairing
        // unrelated mods that share a vendor. Returning null is the honest answer: the editor then
        // adds the mod alone and the admin adds the config file if they want it.
        Assert.That(ClientModScanner.SuggestConfigFile("DrakiaXYZ-Waypoints", ConfigFiles), Is.Null);
    }

    [Test]
    public void DeclinesToGuessWhenNothingResembles()
    {
        Assert.That(ClientModScanner.SuggestConfigFile("BorkelRNVG", ConfigFiles), Is.Null,
            "com.borkel.nvgmasks.cfg shares only the vendor, which is not enough to pair on");
    }

    [Test]
    public void DeclinesToGuessOnVeryShortNames()
    {
        // Two characters appear inside half the config names on a real server. No guess beats a
        // confident wrong one.
        Assert.That(ClientModScanner.SuggestConfigFile("HG", ConfigFiles), Is.Null);
        Assert.That(ClientModScanner.SuggestConfigFile("", ConfigFiles), Is.Null);
    }

    [Test]
    public void MatchesALoosePluginDllByItsTrimmedName()
    {
        Assert.That(ClientModScanner.SuggestConfigFile("HealingAutoCancel.dll", ConfigFiles),
            Is.EqualTo("../BepInEx/config/HealingAutoCancel.cfg"),
            "a loose plugin is X.dll on disk but X to its author");
    }

    [TestCase("NoInsurance.dll", "NoInsurance")]
    [TestCase("NoInsurance.DLL", "NoInsurance")]
    [TestCase("SAIN", "SAIN")]
    [TestCase("some.folder.name", "some.folder.name")]
    public void TrimDll_OnlyStripsARealExtension(string input, string expected) =>
        Assert.That(ClientModScanner.TrimDll(input), Is.EqualTo(expected));

    [Test]
    public void MissingFoldersScanEmptyRatherThanThrowing()
    {
        // A server that has never had a client staged against it still has to render the page.
        var nowhere = Path.Combine(Path.GetTempPath(), "modsync-scan-" + Guid.NewGuid().ToString("N"));

        Assert.Multiple(() =>
        {
            Assert.That(ClientModScanner.Scan(nowhere), Is.Empty);
            Assert.That(ClientModScanner.ConfigFiles(nowhere), Is.Empty);
            Assert.That(ClientModScanner.CountEntries(nowhere, ClientModScanner.PluginsPath), Is.Zero);
        });
    }

    [Test]
    public void ScanFindsFoldersAndLoosePluginsAndSpellsPathsTheWayTheConfigDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "modsync-scan-" + Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(root, "..", "BepInEx", "plugins");
        var patchers = Path.Combine(root, "..", "BepInEx", "patchers");

        try
        {
            Directory.CreateDirectory(Path.Combine(plugins, "SAIN"));
            Directory.CreateDirectory(patchers);
            File.WriteAllText(Path.Combine(plugins, "NoInsurance.dll"), "");
            File.WriteAllText(Path.Combine(patchers, "SomePatcher.dll"), "");

            var found = ClientModScanner.Scan(root);

            Assert.Multiple(() =>
            {
                Assert.That(found.Select(m => m.Path), Does.Contain("../BepInEx/plugins/SAIN"));
                Assert.That(found.Select(m => m.Path), Does.Contain("../BepInEx/plugins/NoInsurance.dll"),
                    "the PATH keeps the extension - it has to address the real file");
                Assert.That(found.Find(m => m.Name == "NoInsurance"), Is.Not.Null,
                    "the NAME drops it - that is what an admin calls the mod");
                Assert.That(found.Find(m => m.Name == "SomePatcher")?.Area, Is.EqualTo("patchers"));
                Assert.That(ClientModScanner.CountEntries(root, ClientModScanner.PluginsPath), Is.EqualTo(2));
            });
        }
        finally
        {
            var gameRoot = Path.GetFullPath(Path.Combine(root, "..", "BepInEx"));
            if (Directory.Exists(gameRoot)) Directory.Delete(gameRoot, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
