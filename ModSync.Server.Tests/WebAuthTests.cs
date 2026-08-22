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
    public void GeneratedPasswordsSatisfyTheRuleTheUIEnforces()
    {
        // The generated password has to pass the same bar an admin's chosen one does, or the very
        // first password issued would be one the change-password form would reject.
        for (var i = 0; i < 200; i++)
        {
            var password = WebAuthService.GeneratePassword();
            Assert.That(WebAuthService.DescribePasswordProblem(password), Is.Null,
                $"generated password '{password}' failed its own rule");
        }
    }

    [Test]
    public void GeneratedPasswordsAreNotAllTheSameShape()
    {
        // A shuffle bug that left the guaranteed characters in fixed positions would still pass the
        // rule above while making every password start with an uppercase letter.
        var firstCharClasses = new HashSet<string>();

        for (var i = 0; i < 200; i++)
        {
            var c = WebAuthService.GeneratePassword()[0];
            firstCharClasses.Add(char.IsUpper(c) ? "upper" : char.IsLower(c) ? "lower" : char.IsDigit(c) ? "digit" : "special");
        }

        Assert.That(firstCharClasses, Has.Count.GreaterThan(1),
            "every generated password began with the same class of character - the shuffle is not shuffling");
    }

    [Test]
    public void GeneratedPasswordsAreDistinct()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++) seen.Add(WebAuthService.GeneratePassword());

        Assert.That(seen, Has.Count.EqualTo(200), "generated passwords repeated - the source is not random");
    }

    [Test]
    public void GeneratedPasswordsAvoidCharactersThatDoNotSurviveACopyPaste()
    {
        // This is read off a terminal and typed into a browser. Quotes, backslashes and spaces get
        // mangled on the way; O/0 and I/l/1 get mistyped.
        for (var i = 0; i < 200; i++)
        {
            Assert.That(WebAuthService.GeneratePassword(),
                Does.Not.Match("[\"'\\\\ `O0Il1]"),
                "generated password contains an ambiguous or shell-hostile character");
        }
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
