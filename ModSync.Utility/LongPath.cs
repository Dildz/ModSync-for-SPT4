namespace ModSync.Utility;

using System.IO;

/// <summary>
/// Windows MAX_PATH (260-character) workaround.
///
/// The client plugin runs on .NET Framework 4.7.2, which enforces the legacy 260-char path
/// limit (248 for directories). A deep SPT install combined with a deeply-nested mod — e.g. an
/// addon that bundles its own <c>BepInEx/plugins/...</c> tree inside itself — can push a staged
/// download path past that, and <c>FileStream</c>/<c>Directory</c> ops then fail with
/// <c>DirectoryNotFoundException</c>. Installs near the drive root stay under the limit, which is
/// why only some users hit it.
///
/// Prefixing a fully-qualified path with the extended-length marker <c>\\?\</c> tells the Win32
/// API to skip MAX_PATH normalization, raising the effective limit to ~32,767 characters — no OS
/// registry change, app.config switch, or manifest entry required.
///
/// **Cross-platform safety:** the marker is a Windows construct. On native Linux (the Docker
/// headless client when not under Wine) it would corrupt the path, so we no-op there. We also
/// only touch paths that actually approach the limit, leaving the common case — including every
/// normal headless sync — byte-for-byte unchanged on every platform.
/// </summary>
public static class LongPath
{
    // Below this, a path is safe on native Windows without any special handling (260 file /
    // 248 directory limit, with margin). Above it, prefix to be safe.
    private const int Threshold = 240;

    /// <summary>
    /// Prefix <paramref name="path"/> with <c>\\?\</c> when it needs MAX_PATH bypass, deciding
    /// Windows-ness from the runtime. Idempotent; short, relative, already-prefixed, and
    /// non-Windows paths are returned unchanged.
    /// </summary>
    public static string Extended(string path) => Extended(path, Path.DirectorySeparatorChar == '\\');

    /// <summary>
    /// Testable core. <paramref name="windows"/> is whether this is a Windows-style filesystem —
    /// true on real Windows and under Wine (where the Docker headless client runs), false on
    /// native Linux. Split out so the transform can be unit-tested deterministically regardless
    /// of the host OS.
    /// </summary>
    public static string Extended(string path, bool windows)
    {
        // Linux: "\\?\" is meaningless and would corrupt the path. Never touch it.
        if (!windows)
            return path;

        // Short / empty / already-prefixed paths need nothing.
        if (string.IsNullOrEmpty(path) || path.Length < Threshold || path.StartsWith(@"\\?\"))
            return path;

        // The marker requires backslash separators and a rooted path.
        var normalized = path.Replace('/', '\\');

        // UNC share: \\server\share\... → \\?\UNC\server\share\...
        if (normalized.StartsWith(@"\\"))
            return @"\\?\UNC\" + normalized.Substring(2);

        // Drive-rooted: C:\... → \\?\C:\...
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            return @"\\?\" + normalized;

        // Relative / non-rooted — can't safely prefix, leave it alone.
        return path;
    }
}
