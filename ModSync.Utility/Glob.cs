namespace ModSync.Utility;

using System.Collections.Generic;
using System.Text.RegularExpressions;

public static partial class Glob
{
    private static readonly Regex DotRE = new(@"\.", RegexOptions.Compiled);
    private const string DotPattern = @"\.";

    private static readonly Regex RestRE = new(@"\*\*$", RegexOptions.Compiled);
    private const string RestPattern = "(.+)";

    private static readonly Regex GlobRE = new(@"(?:\*\*\/|\*\*|\*)", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> GlobPatterns =
        new()
        {
            ["*"] = "([^/]+)", // no backslashes
            ["**"] = "(.+/)?([^/]+)", // short for "**/*"
            ["**/"] = "(.+/)?" // one or more directories
        };

    private static string Replace(string glob)
    {
        // Globs from the server arrive in wire format using backslashes (e.g.
        // "BepInEx\plugins\Fika"). When we feed that straight into Regex, the
        // engine reads sequences like "\F" as escape codes — most are invalid
        // and throw at compile time ("Unrecognized escape sequence \F").
        // A few (\s, \p) are valid but mean the wrong thing.
        // We escape every literal backslash up front so the regex engine treats
        // each "\" as a literal character (regex source "\\" → matches one "\").
        glob = glob.Replace(@"\", @"\\");
        return GlobRE.Replace(RestRE.Replace(DotRE.Replace(glob, DotPattern), RestPattern), match => GlobPatterns[match.Value]);
    }

    public static Regex Create(string glob) => new($"^{Replace(glob)}$", RegexOptions.Compiled);

    public static Regex CreateNoEnd(string glob) => new($"^{Replace(glob)}", RegexOptions.Compiled);
}
