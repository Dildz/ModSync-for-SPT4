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
    private bool _locked;

    /// <summary>Where the login stands. The UI shows a different form for each.</summary>
    public enum AuthState
    {
        /// <summary>No credential file yet - the page asks the admin to choose a password.</summary>
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
    /// Work out where the login stands, and say so on the console when no password is set yet.
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

        // First boot: nobody has set a password. Say so on the console, because until the admin
        // sets one the page will accept a password from whoever opens it - so how soon they get to
        // it is a decision they can only make if they know it is outstanding.
        logger.LogWithColor(
            $"""

            ┌─ Corter-ModSync ────────────────────────────────────────────────
            │ The ModSync configuration page has no password yet.
            │
            │ Open it from the server's web UI, under ModSync, and choose one.
            │ The username is {DefaultUsername}.
            │
            │ Until a password is set the page will let anyone set it, so do
            │ this before opening the server up.
            └─────────────────────────────────────────────────────────────────

            """,
            Color.Yellow);
    }

    /// <summary>
    /// Finish first-run setup: store the password the admin chose. Returns null on success, or the
    /// reason it was refused.
    /// </summary>
    public string? CompleteSetup(string? password, string? confirmation)
    {
        if (State != AuthState.NeedsSetup) return "A password has already been set.";

        if (password != confirmation) return "The two passwords do not match.";

        var problem = DescribePasswordProblem(password);
        if (problem is not null) return problem;

        _credentials = Create(DefaultUsername, password!);
        Write(_credentials);

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

    // What counts as a special character. Punctuation outside this set still passes the length,
    // case and digit checks - this list only decides what SATISFIES the "special character" rule.
    private const string Specials = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

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
