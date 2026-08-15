using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for the Config class filter methods - IsExcluded, IsHeadlessAllowed,
/// IsManagedAllowed, IsHeadlessManagedAllowed. These are pure logic with no file I/O.
/// Ported from the original TypeScript config.test.ts "Config" describe block.
/// </summary>
[TestFixture]
public class ConfigTests
{
    private static Config MakeConfig(
        List<string>? exclusions = null,
        List<string>? headlessIncludes = null,
        List<string>? managedIncludes = null,
        List<string>? headlessManagedIncludes = null) =>
        new(
            syncPaths: [],
            exclusions: exclusions ?? [],
            headlessIncludes: headlessIncludes ?? [],
            managedIncludes: managedIncludes ?? [],
            headlessManagedIncludes: headlessManagedIncludes ?? []);

    // ── IsExcluded ────────────────────────────────────────────────────────────

    [Test]
    public void IsExcluded_FileMatchingGlob_ReturnsTrue()
    {
        var config = MakeConfig(exclusions: ["plugins/**/node_modules", "plugins/**/*.js"]);
        Assert.That(config.IsExcluded("plugins/banana/test.js"), Is.True);
    }

    [Test]
    public void IsExcluded_FolderMatchingGlob_ReturnsTrue()
    {
        var config = MakeConfig(exclusions: ["plugins/**/node_modules"]);
        Assert.That(config.IsExcluded("plugins/banana/node_modules"), Is.True);
    }

    [Test]
    public void IsExcluded_FileNotMatchingAnyGlob_ReturnsFalse()
    {
        var config = MakeConfig(exclusions: ["plugins/**/node_modules", "plugins/**/*.js"]);
        Assert.That(config.IsExcluded("plugins/test.dll"), Is.False);
    }

    [Test]
    public void IsExcluded_FileWithNosyncExtension_ReturnsTrue()
    {
        var config = MakeConfig(exclusions: ["**/*.nosync", "**/*.nosync.txt"]);
        Assert.That(config.IsExcluded("plugins/SAIN/SAIN.dll.nosync"), Is.True);
    }

    [Test]
    public void IsExcluded_NosyncTxtExtension_ReturnsTrue()
    {
        var config = MakeConfig(exclusions: ["**/*.nosync", "**/*.nosync.txt"]);
        Assert.That(config.IsExcluded("plugins/SAIN/SAIN.dll.nosync.txt"), Is.True);
    }

    [Test]
    public void IsExcluded_BackslashPath_NormalizesAndMatches()
    {
        var config = MakeConfig(exclusions: ["**/*.nosync"]);
        // Server may receive backslash-separated paths on Windows - should normalize
        Assert.That(config.IsExcluded(@"plugins\SAIN\SAIN.dll.nosync"), Is.True);
    }

    [Test]
    public void IsExcluded_FikaHeadlessDll_ReturnsTrue()
    {
        var config = MakeConfig(exclusions: ["../BepInEx/plugins/Fika/Fika.Headless.dll"]);
        Assert.That(config.IsExcluded("../BepInEx/plugins/Fika/Fika.Headless.dll"), Is.True);
    }

    // ── IsHeadlessAllowed ─────────────────────────────────────────────────────

    [Test]
    public void IsHeadlessAllowed_FolderEntryMatchesFileInsideFolder()
    {
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/SAIN"]);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/SAIN/SAIN.dll"), Is.True);
    }

    [Test]
    public void IsHeadlessAllowed_FolderEntryMatchesFolderItself()
    {
        // Empty-dir sentinel: the folder path itself should match
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/SAIN"]);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/SAIN"), Is.True);
    }

    [Test]
    public void IsHeadlessAllowed_FolderEntryDoesNotMatchSiblingFolder()
    {
        // "../BepInEx/plugins/SAIN" must NOT match "../BepInEx/plugins/SAINExtra"
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/SAIN"]);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/SAINExtra/SAINExtra.dll"), Is.False);
    }

    [Test]
    public void IsHeadlessAllowed_ExactFileEntryMatchesOnlyThatFile()
    {
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/BigBrain.dll"]);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/BigBrain.dll"), Is.True);
    }

    [Test]
    public void IsHeadlessAllowed_ExactFileEntryDoesNotMatchOtherFile()
    {
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/BigBrain.dll"]);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/SAIN.dll"), Is.False);
    }

    [Test]
    public void IsHeadlessAllowed_EmptyAllowlist_ReturnsFalse()
    {
        var config = MakeConfig(headlessIncludes: []);
        Assert.That(config.IsHeadlessAllowed("../BepInEx/plugins/SAIN/SAIN.dll"), Is.False);
    }

    [Test]
    public void IsHeadlessAllowed_BackslashPath_NormalizesAndMatches()
    {
        var config = MakeConfig(headlessIncludes: ["../BepInEx/plugins/SAIN"]);
        Assert.That(config.IsHeadlessAllowed(@"..\BepInEx\plugins\SAIN\SAIN.dll"), Is.True);
    }

    // ── IsManagedAllowed ──────────────────────────────────────────────────────

    [Test]
    public void IsManagedAllowed_FileInList_ReturnsTrue()
    {
        var config = MakeConfig(managedIncludes: ["Unity.VectorGraphics.dll"]);
        Assert.That(config.IsManagedAllowed("Unity.VectorGraphics.dll"), Is.True);
    }

    [Test]
    public void IsManagedAllowed_FileNotInList_ReturnsFalse()
    {
        var config = MakeConfig(managedIncludes: ["Unity.VectorGraphics.dll"]);
        Assert.That(config.IsManagedAllowed("Assembly-CSharp.dll"), Is.False);
    }

    [Test]
    public void IsManagedAllowed_CaseInsensitive()
    {
        // Windows filesystems are case-insensitive; the allowlist should match regardless
        var config = MakeConfig(managedIncludes: ["Unity.VectorGraphics.dll"]);
        Assert.That(config.IsManagedAllowed("unity.vectorgraphics.dll"), Is.True);
    }

    [Test]
    public void IsManagedAllowed_EmptyList_ReturnsFalse()
    {
        var config = MakeConfig(managedIncludes: []);
        Assert.That(config.IsManagedAllowed("Unity.VectorGraphics.dll"), Is.False);
    }

    // ── IsHeadlessManagedAllowed ──────────────────────────────────────────────

    [Test]
    public void IsHeadlessManagedAllowed_FileInList_ReturnsTrue()
    {
        var config = MakeConfig(headlessManagedIncludes: ["Unity.VectorGraphics.dll"]);
        Assert.That(config.IsHeadlessManagedAllowed("Unity.VectorGraphics.dll"), Is.True);
    }

    [Test]
    public void IsHeadlessManagedAllowed_FileNotInPlayerList_ReturnsFalse()
    {
        // headlessManagedIncludes is separate from managedIncludes
        var config = MakeConfig(
            managedIncludes: ["Unity.VectorGraphics.dll"],
            headlessManagedIncludes: []);
        Assert.That(config.IsHeadlessManagedAllowed("Unity.VectorGraphics.dll"), Is.False);
    }
}
