using System;
using System.IO;

namespace ModSync.Utility;

/// <summary>
/// Files the server must never hand to a client, whatever the config says.
///
/// Right now that is exactly one file: the web UI's credentials. It lives in the game root next to
/// <c>ModSync.Updater.exe</c>, and the game root is where client-bound files are staged - so it sits
/// among things that ARE served, with a fixed, documented name. Nothing about its location keeps it
/// safe. This predicate is what keeps it safe.
///
/// **Two independent gates have to consult this, because either one alone can be bypassed:**
/// <list type="number">
///   <item><description>
///     The listing gate in <c>SyncUtil.GetFilesInDir</c>. Note it must sit OUTSIDE that method's
///     <c>skipExclusions</c> check - enforced syncpaths pass <c>skipExclusions: true</c>, so a guard
///     placed inside it would be defeated by putting <c>"enforced": true</c> on a syncpath covering
///     the game root, which is precisely the attack.
///   </description></item>
///   <item><description>
///     The download gate in <c>SyncUtil.SanitizeDownloadPath</c>. A file that is never advertised is
///     still fetchable by direct request if it falls inside a configured syncpath, and a fixed
///     filename takes no guessing at all.
///   </description></item>
/// </list>
///
/// Both call HERE rather than testing the path themselves. Two copies of a rule like this drift -
/// the codebase already has <c>IsInProtectedBaseFolder</c> duplicated between the patcher and the
/// updater with one spelling it <c>StartsWith</c> and the other <c>Contains</c>, which is on the
/// backlog for exactly that reason.
///
/// Lives in ModSync.Utility beside <see cref="Builtins"/> because it is the same kind of thing: a
/// fact about ModSync's own files that more than one place needs and none may restate.
/// </summary>
public static class ProtectedFiles
{
    /// <summary>The credential file's name. Public so the web UI and the docs agree on it.</summary>
    public const string WebAuthFileName = "modsync.webauth.json";

    /// <summary>
    /// Where it lives, server-cwd-relative. The server runs from &lt;gameRoot&gt;/SPT_Runtime/, so
    /// this resolves to the game root - beside ModSync.Updater.exe, which an admin already has to
    /// delete by hand when removing ModSync, so one more file there is no extra chore.
    ///
    /// The placement is not the defence, but it is the better of two bad neighbourhoods: no
    /// plausible syncPath covers the bare game root, whereas syncing <c>user/mods</c> is a recipe
    /// the wiki documents and admins actually follow - which would have shipped this file to every
    /// connecting client had it lived in the mod folder.
    /// </summary>
    public const string WebAuthPath = "../" + WebAuthFileName;

    /// <summary>
    /// True if this path is one the server must never serve.
    ///
    /// Callers pass wildly different spellings - the listing gate yields server-relative paths with
    /// whatever separator the OS used, the download gate has already resolved to an absolute path -
    /// so everything is resolved against the server root before comparing. That also disposes of
    /// any <c>..</c> games in a crafted request: two spellings of the same file resolve to the same
    /// string, and only the resolved string is compared.
    ///
    /// Case-insensitive on purpose. On Windows a case-variant IS the same file; on Linux it is not,
    /// so there the comparison is stricter than it strictly needs to be. That direction is free -
    /// it can only refuse to serve a file nobody has - and the opposite direction leaks a password
    /// hash on the platform where most servers run.
    /// </summary>
    public static bool IsProtected(string path, string serverRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var resolved = Path.GetFullPath(Path.Combine(serverRoot, path.Replace('\\', '/')));
            var authFile = Path.GetFullPath(Path.Combine(serverRoot, WebAuthPath));

            return string.Equals(resolved, authFile, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that will not resolve is a path we cannot reason about. Refuse it: the cost of
            // a false positive is one unserved file, the cost of a false negative is the credential.
            return true;
        }
    }
}
