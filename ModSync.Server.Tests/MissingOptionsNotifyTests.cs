namespace ModSync.Server.Test;

/// <summary>
/// Tests for ConfigUtil.MissingTopLevelOptions — the boot-time advisory that names config
/// options an admin's file predates.
///
/// An existing config.jsonc is NEVER overwritten, so without this a newly added top-level
/// option is invisible to every existing server: it silently takes its default and the admin
/// has no reason to know it exists. Nothing breaks (RawConfig defaults everything), which is
/// exactly why it needs announcing rather than failing.
/// </summary>
[TestFixture]
public class MissingOptionsNotifyTests
{
    private const string Defaults = """
        {
            "syncPaths": [],
            "exclusions": [],
            "futureOption": []
        }
        """;

    [Test]
    public void ConfigPredatingAnOption_NamesIt()
    {
        var oldConfig = """
            {
                "syncPaths": [],
                "exclusions": []
            }
            """;

        Assert.That(ConfigUtil.MissingTopLevelOptions(oldConfig, Defaults), Is.EqualTo(new[] { "futureOption" }));
    }

    [Test]
    public void CurrentConfig_NamesNothing()
    {
        var current = """
            {
                "syncPaths": [],
                "exclusions": [],
                "futureOption": []
            }
            """;

        Assert.That(ConfigUtil.MissingTopLevelOptions(current, Defaults), Is.Empty);
    }

    [Test]
    public void CommentsAndTrailingCommas_DoNotBreakTheComparison()
    {
        // The real file is .jsonc with heavy comments and the trailing commas we tell admins
        // are fine. If this parse regressed, the advisory would either vanish or fire wrongly.
        var commented = """
            {
                // a comment about syncPaths
                "syncPaths": [
                    "../BepInEx/plugins",   // trailing comment
                ],
                /* block comment */
                "exclusions": [],
            }
            """;

        Assert.That(ConfigUtil.MissingTopLevelOptions(commented, Defaults), Is.EqualTo(new[] { "futureOption" }));
    }

    [Test]
    public void ExtraKeysTheAdminAdded_AreNotReported()
    {
        // We only report what the DEFAULTS have and they don't — never the reverse. An admin's
        // own stray key is their business, and nagging about it would be noise.
        var withExtra = """
            {
                "syncPaths": [],
                "exclusions": [],
                "futureOption": [],
                "somethingOfTheirOwn": 1
            }
            """;

        Assert.That(ConfigUtil.MissingTopLevelOptions(withExtra, Defaults), Is.Empty);
    }

    [Test]
    public void MalformedConfig_ReturnsEmptyRatherThanThrowing()
    {
        // Advisory only. The real parse has already succeeded by the time this runs, so a
        // hiccup here must never take the server down.
        Assert.That(ConfigUtil.MissingTopLevelOptions("{ not json", Defaults), Is.Empty);
    }

    [Test]
    public void ShippedDefaultConfig_ReportsNothingAgainstItself()
    {
        // Guards the shipped file: if DefaultConfig ever became unparseable under the JSONC
        // options, every server would silently lose this advisory.
        Assert.That(
            ConfigUtil.MissingTopLevelOptions(ConfigUtil.ShippedDefaultConfig, ConfigUtil.ShippedDefaultConfig),
            Is.Empty);
    }
}
