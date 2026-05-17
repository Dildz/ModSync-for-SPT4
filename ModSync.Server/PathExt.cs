namespace ModSync.Server;

/// <summary>
/// Path separator helpers — mirror of misc.ts winPath/unixPath.
///
/// The BepInEx client (Windows-only) expects all paths from the server with **backslashes**.
/// On a Linux server, .NET's `Path.Combine` produces forward slashes, so before sending paths
/// to the client we must convert them. Going the other direction (parsing incoming requests),
/// we normalize to forward slashes for matching against config syncpaths/exclusions.
///
/// `static class`: a class that can't be instantiated. Just a namespace for related helpers.
/// </summary>
public static class PathExt
{
    /// <summary>Convert all '/' to '\\' — for paths going out to the Windows BepInEx client.</summary>
    public static string WinPath(string p) => p.Replace('/', '\\');

    /// <summary>Convert all '\\' to '/' — for normalizing paths before glob matching.</summary>
    public static string UnixPath(string p) => p.Replace('\\', '/');
}
