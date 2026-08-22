using ModSync.Utility;

namespace ModSync.Server.Test;

/// <summary>
/// Tests for SyncUtil.SanitizeDownloadPath - the path traversal guard that validates
/// every client download request. A critical security boundary.
/// Ported from the original TypeScript sync.test.ts "sanitizeDownloadPath" describe block.
/// </summary>
[TestFixture]
public class SanitizeDownloadPathTests
{
    private static List<SyncPath> PluginsSyncPath =>
        [new SyncPath("plugins")];

    // ── Valid paths ───────────────────────────────────────────────────────────

    [Test]
    public void ValidPath_ReturnsResolvedFullPath()
    {
        var result = SyncUtil.SanitizeDownloadPath(@"plugins\file.dll", PluginsSyncPath);
        Assert.That(result, Does.EndWith("file.dll"));
    }

    [Test]
    public void ValidSubPath_IsAccepted()
    {
        var result = SyncUtil.SanitizeDownloadPath(@"plugins\SAIN\SAIN.dll", PluginsSyncPath);
        Assert.That(result, Does.EndWith("SAIN.dll"));
    }

    // ── Path traversal ────────────────────────────────────────────────────────

    [Test]
    public void PathTraversalUpward_ThrowsHttpError400()
    {
        var ex = Assert.Throws<HttpError>(() =>
            SyncUtil.SanitizeDownloadPath(@"plugins\..\secret.dll", PluginsSyncPath));

        Assert.That(ex!.Code, Is.EqualTo(400));
    }

    [Test]
    public void DeepPathTraversal_ThrowsHttpError400()
    {
        var ex = Assert.Throws<HttpError>(() =>
            SyncUtil.SanitizeDownloadPath(@"plugins\..\..\etc\passwd", PluginsSyncPath));

        Assert.That(ex!.Code, Is.EqualTo(400));
    }

    // ── File not in syncpath ──────────────────────────────────────────────────

    [Test]
    public void FileOutsideAllSyncPaths_ThrowsHttpError400()
    {
        var ex = Assert.Throws<HttpError>(() =>
            SyncUtil.SanitizeDownloadPath(@"otherDir\file.dll", PluginsSyncPath));

        Assert.That(ex!.Code, Is.EqualTo(400));
    }

    [Test]
    public void FileInDifferentSyncPath_ThrowsWhenNotConfigured()
    {
        // patchers is not in syncPaths - even a valid relative path should be rejected
        var ex = Assert.Throws<HttpError>(() =>
            SyncUtil.SanitizeDownloadPath(@"patchers\SomePatcher.dll", PluginsSyncPath));

        Assert.That(ex!.Code, Is.EqualTo(400));
    }

    [Test]
    public void FileInDifferentSyncPath_AcceptedWhenConfigured()
    {
        var syncPaths = new List<SyncPath> { new("plugins"), new("patchers") };

        var result = SyncUtil.SanitizeDownloadPath(@"patchers\SomePatcher.dll", syncPaths);

        Assert.That(result, Does.EndWith("SomePatcher.dll"));
    }
}
