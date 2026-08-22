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
    private string? _setupToken;
    private bool _locked;

    /// <summary>Where the login stands. The UI shows a different form for each.</summary>
    public enum AuthState
    {
        /// <summary>No credential file yet - the admin chooses a password, proving they can see the console.</summary>
        NeedsSetup,

        /// <summary>Credentials exist; the login form applies.</summary>
        Ready,

        /// <summary>A credential file exists but cannot be read. Nothing works until it is deleted.</summary>
        Locked,
    }

    public AuthState State =>
        _credentials is not null ? AuthState.Ready
        : _locked ? AuthState.Locked
        : AuthState.NeedsSetup;

    /// <summary>
    /// Work out where the login stands, and on a first boot print the one-time setup code the admin
    /// needs in order to choose a password.
    ///
    /// Three outcomes, and the middle one is the security-relevant one: credentials load and the
    /// login form applies; no file exists so setup begins; or a file exists and cannot be read, in
    /// which case the page LOCKS rather than offering setup. Falling back to setup there would make
    /// "corrupt the credential file" a way to take the editor from its owner.
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
                    + "Delete it and restart the server to set a password again.");
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.Error($"Corter-ModSync: could not read {ProtectedFiles.WebAuthFileName} ({e.Message}). "
                    + "Delete it and restart the server to set a password again.");
            }

            // A file we cannot read must never fall through to "start setup" - that would make
            // corrupting the file a way to seize the editor. Refuse instead: locked until an admin
            // deletes it deliberately, which takes access to the machine.
            _credentials = null;
            _locked = true;
            return;
        }

        // First boot. The admin picks their own password, but they have to prove they can see this
        // console first.
        //
        // **Why a token at all.** This page is served on the port Fika clients sync against, which
        // is frequently port-forwarded. A setup form that anyone could complete would mean whoever
        // reaches it first owns the config editor - and the config editor decides which DLLs land in
        // every player's BepInEx folder. The window is not small either: it runs from first boot
        // until the admin happens to visit, which can be days.
        //
        // Held in memory only, never written down. A restart issues a fresh one, so an admin who
        // scrolled past it just restarts rather than hunting through old logs.
        _setupToken = GenerateSetupToken();

        logger.LogWithColor(
            $"""

            ┌─ Corter-ModSync ────────────────────────────────────────────────
            │ The ModSync configuration page has no password yet.
            │
            │   setup code   {_setupToken}
            │
            │ Open the page from the server's web UI, under ModSync, and use
            │ this code once to choose your own password. The username is
            │ {DefaultUsername}.
            │
            │ The code is only good until the password is set, and a new one
            │ is printed here on every restart until then.
            └─────────────────────────────────────────────────────────────────

            """,
            Color.Yellow);
    }

    /// <summary>
    /// Finish first-run setup: check the console code, then store the admin's chosen password.
    /// Returns null on success, or the reason it was refused.
    /// </summary>
    public string? CompleteSetup(string? token, string? password, string? confirmation)
    {
        if (State != AuthState.NeedsSetup) return "A password has already been set.";

        // Constant-time, same as the login path. The code is short-lived but it is still a secret,
        // and there is no reason to leak it a character at a time.
        if (_setupToken is null || !FixedTimeEquals(token, _setupToken))
            return "That setup code does not match the one in the server console.";

        if (password != confirmation) return "The two passwords do not match.";

        var problem = DescribePasswordProblem(password);
        if (problem is not null) return problem;

        _credentials = Create(DefaultUsername, password!);
        Write(_credentials);

        // Spent. Even though State stops returning NeedsSetup, clearing it keeps the secret from
        // sitting in memory for the life of the process.
        _setupToken = null;

        logger.Info("Corter-ModSync: the configuration page password has been set.");
        return null;
    }

    /// <summary>True once credentials exist and can be checked against.</summary>
    public bool IsReady => _credentials is not null;

    /// <summary>True on a fresh install, before anyone has chosen a password.</summary>
    public bool NeedsSetup => State == AuthState.NeedsSetup;

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
    /// Applies to first-run setup and to a later change alike - every password on this page is one
    /// an admin typed, so this is the only thing standing between the config editor and "password".
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
    /// The one-time code shown on the console during first-run setup.
    ///
    /// Letters and digits only - no special characters and no ambiguous glyphs. Unlike a password
    /// this is read off a terminal and typed into a browser exactly once, by someone who may be
    /// looking at a Docker log through a web console, so being easy to transcribe matters more than
    /// being dense. 12 characters from a 55-symbol alphabet is far beyond guessing for a secret that
    /// stops working the moment it is used.
    /// </summary>
    public static string GenerateSetupToken()
    {
        const int length = 12;
        var alphabet = Uppers + Lowers + Digits;

        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        return new string(chars);
    }

    /// <summary>
    /// Compare two secrets without leaking how much of the front matched through timing.
    ///
    /// A length mismatch does return early, which leaks the length - that is fine here, the setup
    /// code is a fixed 12 characters and that is written in this file. What must not leak is which
    /// characters are right.
    /// </summary>
    private static bool FixedTimeEquals(string? a, string b) =>
        a is not null
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));

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
