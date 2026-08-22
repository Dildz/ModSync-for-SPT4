using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;

namespace ModSync.Server;

/// <summary>What is on disk. Only ever a salted hash - the password itself is shown once and then gone.</summary>
public class WebAuthFile
{
    [JsonPropertyName("username")] public string Username { get; set; } = WebAuthService.DefaultUsername;
    [JsonPropertyName("salt")] public string Salt { get; set; } = string.Empty;
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = WebAuthService.Iterations;
}

/// <summary>
/// The login behind the config editor.
///
/// **Why this is not optional hardening.** The SPT web port is the same port Fika clients sync
/// against, so the config page is reachable by every player on the server and by the whole internet
/// if that port is forwarded. An unauthenticated editor that can write config.jsonc lets anyone who
/// reaches it add an <c>enforced</c> syncPath and push arbitrary DLLs into every player's
/// BepInEx/plugins - remote code execution on their machines - or point a syncPath at a sensitive
/// folder and read it back through /modsync/fetch. That is the difference between a config page and
/// a remote shell, which is why this had to ship with the save path rather than after it.
///
/// The credential file itself is hardcoded un-syncable through <see cref="ProtectedFiles"/>.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class WebAuthService(ISptLogger<WebAuthService> logger)
{
    public const string DefaultUsername = "ModSyncAdmin";

    // PBKDF2 with SHA-256. In the framework since forever, so no new dependency, and the iteration
    // count is stored per-file so it can be raised later without invalidating existing credentials.
    public const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static string FilePath =>
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ProtectedFiles.WebAuthPath));

    private WebAuthFile? _credentials;

    /// <summary>
    /// Read the credential file, generating one on first boot. The generated password is printed to
    /// the server console EXACTLY once, at the moment it is created - nothing stores it, so an admin
    /// who misses it recovers by deleting the file and restarting rather than by asking us for it.
    /// </summary>
    public void EnsureCredentials()
    {
        if (_credentials is not null) return;

        var path = FilePath;

        if (File.Exists(path))
        {
            try
            {
                _credentials = JsonSerializer.Deserialize<WebAuthFile>(File.ReadAllText(path));
                if (_credentials is not null && !string.IsNullOrEmpty(_credentials.Hash)) return;

                logger.Error($"Corter-ModSync: {ProtectedFiles.WebAuthFileName} is missing its hash. "
                    + "Delete it and restart the server to be issued a new password.");
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.Error($"Corter-ModSync: could not read {ProtectedFiles.WebAuthFileName} ({e.Message}). "
                    + "Delete it and restart the server to be issued a new password.");
            }

            // A file we cannot read must never fall through to "generate a new one" - that would let
            // anyone who can corrupt the file force a fresh password onto the console. Refuse
            // instead: the editor stays locked until an admin deletes it deliberately.
            _credentials = null;
            return;
        }

        var password = GeneratePassword();
        _credentials = Create(DefaultUsername, password);
        Write(_credentials);

        // The one and only time this string exists outside the admin's head.
        logger.LogWithColor(
            $"""

            ┌─ Corter-ModSync ────────────────────────────────────────────────
            │ A login has been created for the ModSync configuration page.
            │
            │   username   {DefaultUsername}
            │   password   {password}
            │
            │ This password is shown once and is not stored anywhere in
            │ readable form. Write it down now.
            │
            │ Open the page from the server's web UI, under ModSync.
            │ Forgotten it later? Delete {ProtectedFiles.WebAuthFileName} from the game
            │ folder and restart - a new one is issued and printed here.
            └─────────────────────────────────────────────────────────────────

            """,
            Color.Yellow);
    }

    /// <summary>True once credentials exist and can be checked against.</summary>
    public bool IsReady => _credentials is not null;

    public string Username => _credentials?.Username ?? DefaultUsername;

    /// <summary>
    /// Check a login. Constant-time on the hash comparison so a network observer cannot narrow the
    /// hash down a byte at a time by timing the response.
    /// </summary>
    public bool Verify(string username, string password)
    {
        if (_credentials is null) return false;
        if (string.IsNullOrEmpty(password)) return false;

        if (!string.Equals(username, _credentials.Username, StringComparison.Ordinal)) return false;

        try
        {
            var expected = Convert.FromBase64String(_credentials.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                Convert.FromBase64String(_credentials.Salt),
                _credentials.Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Replace the password. The caller must have verified the current one first.</summary>
    public void SetPassword(string password)
    {
        var problem = DescribePasswordProblem(password);
        if (problem is not null) throw new ArgumentException(problem, nameof(password));

        _credentials = Create(_credentials?.Username ?? DefaultUsername, password);
        Write(_credentials);
        logger.Info("Corter-ModSync: the configuration page password has been changed.");
    }

    /// <summary>
    /// Why a password is not acceptable, or null if it is. Returns the reason rather than a bool so
    /// the UI can say what is wrong instead of just refusing.
    ///
    /// Only applies to a password an admin chooses. The generated one satisfies these by
    /// construction, which is why <see cref="GeneratePassword"/> draws one character from each set
    /// before filling the rest.
    /// </summary>
    public static string? DescribePasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "Use at least 8 characters.";
        if (!password.Any(char.IsUpper)) return "Include an uppercase letter.";
        if (!password.Any(char.IsDigit)) return "Include a number.";
        if (!password.Any(c => Specials.Contains(c))) return "Include a special character.";
        return null;
    }

    // Deliberately narrow: no quotes, backslashes or spaces. This gets copied out of a terminal and
    // pasted into a browser, and every one of those has a way of not surviving the trip.
    private const string Specials = "!#$%&*+-=?@^_";
    private const string Uppers = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lowers = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";

    /// <summary>
    /// A 16-character password with one of each required class, then shuffled so the classes are not
    /// always in the same positions. Ambiguous glyphs (O/0, I/l/1) are left out of the alphabets -
    /// this is read off a console and typed back in by hand.
    /// </summary>
    public static string GeneratePassword()
    {
        const int length = 16;
        var all = Uppers + Lowers + Digits + Specials;

        var chars = new char[length];
        chars[0] = Pick(Uppers);
        chars[1] = Pick(Lowers);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Specials);
        for (var i = 4; i < length; i++) chars[i] = Pick(all);

        // Fisher-Yates with a cryptographic source, so the guaranteed characters do not sit in
        // fixed positions.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }

    private static WebAuthFile Create(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return new WebAuthFile
        {
            Username = username,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = Iterations,
        };
    }

    private void Write(WebAuthFile file)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Error($"Corter-ModSync: could not write {ProtectedFiles.WebAuthFileName} ({e.Message}). "
                + "The configuration page will stay locked until this is fixed.");
        }
    }
}
