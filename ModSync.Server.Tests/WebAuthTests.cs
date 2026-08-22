namespace ModSync.Server.Test;

/// <summary>
/// The login that has to hold up on a port the whole internet may be able to reach.
///
/// <see cref="WebAuthService.EnsureCredentials"/> is not covered here: it reads and writes a file at
/// a fixed path derived from the process working directory, so exercising it would mean writing to
/// the real game root. The parts that decide whether a login succeeds are static and pure, and those
/// are what these cover.
/// </summary>
[TestFixture]
public class WebAuthTests
{
    [Test]
    public void SetupTokensAreDistinct()
    {
        // If two servers could be issued the same code, the code would not be a secret.
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++) seen.Add(WebAuthService.GenerateSetupToken());

        Assert.That(seen, Has.Count.EqualTo(500), "setup codes repeated - the source is not random");
    }

    [Test]
    public void SetupTokensAreEasyToTranscribe()
    {
        // This is read off a terminal - often a Docker log in a browser tab - and typed into another
        // browser tab by hand. Letters and digits only, and none of the glyphs people mistype.
        for (var i = 0; i < 500; i++)
        {
            var token = WebAuthService.GenerateSetupToken();

            Assert.That(token, Has.Length.EqualTo(12));
            Assert.That(token, Does.Match("^[A-Za-z0-9]+$"), "setup code must be alphanumeric");
            Assert.That(token, Does.Not.Match("[O0Il1]"), "setup code contains an ambiguous glyph");
        }
    }

    [Test]
    public void SetupTokensUseMoreThanAHandfulOfCharacters()
    {
        // A generator stuck on a narrow slice of its alphabet would still pass the checks above.
        var chars = new HashSet<char>();
        for (var i = 0; i < 200; i++) chars.UnionWith(WebAuthService.GenerateSetupToken());

        Assert.That(chars, Has.Count.GreaterThan(40), "the setup code alphabet is barely being used");
    }

    [TestCase("", "at least 8")]
    [TestCase("Ab1!", "at least 8")]
    [TestCase("abcdefg1!", "uppercase")]
    [TestCase("Abcdefgh!", "number")]
    [TestCase("Abcdefg12", "special")]
    public void RejectedPasswordsSayWhatIsWrong(string password, string expectedHint)
    {
        var problem = WebAuthService.DescribePasswordProblem(password);

        Assert.That(problem, Is.Not.Null);
        Assert.That(problem, Does.Contain(expectedHint).IgnoreCase);
    }

    [TestCase("Abcdefg1!")]
    [TestCase("ABCDEFG1!")]                  // no lowercase is required - uppercase is
    [TestCase("Str0ng-Password")]
    [TestCase("aB3$aB3$aB3$")]
    public void AcceptablePasswordsPass(string password) =>
        Assert.That(WebAuthService.DescribePasswordProblem(password), Is.Null);

    [Test]
    public void NullIsRejectedRatherThanThrowing() =>
        Assert.That(WebAuthService.DescribePasswordProblem(null), Is.Not.Null);
}
