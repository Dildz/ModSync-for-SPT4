namespace ModSync.Server.Test;

/// <summary>
/// The login that has to hold up on a port the whole internet may be able to reach.
///
/// <see cref="WebAuthService.EnsureCredentials"/> and <see cref="WebAuthService.CompleteSetup"/> are not
/// covered here: both read and write a file at a fixed path derived from the process working
/// directory, so exercising them would mean writing to the real game root. The password rule they
/// both gate on is static and pure, and that is what these cover.
/// </summary>
[TestFixture]
public class WebAuthTests
{
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
