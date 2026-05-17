using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Utils;
// HttpServerHelper / HttpFileUtil are both under SPTarkov.Server.Core.Utils.

namespace ModSync.Server;

/// <summary>
/// HTTP listener for the /modsync/* routes. Replaces the TS Router class + the
/// HttpListenerModService dance from the original SPT 3 implementation.
///
/// How SPT 4 wires this up: anything implementing <see cref="IHttpListener"/> with
/// <c>[Injectable]</c> is collected by the DI container at startup. When a request
/// arrives, SPT iterates registered listeners and calls <see cref="CanHandle"/> on
/// each; the first one that says yes gets <see cref="Handle"/> invoked. SPT's own
/// <c>SptHttpListener</c> only matches routes registered on its internal router,
/// so our <c>/modsync/</c> prefix is safe — there's no conflict.
///
/// **Initialize() setter pattern.** This listener is constructed by DI during the
/// container build, but the <see cref="Config"/> it needs comes from disk and is
/// only available once <see cref="ModSyncMod"/>.PreSptLoadAsync runs. So we expose
/// an <c>Initialize</c> method that ModSyncMod calls after loading config. Until
/// then, <c>CanHandle</c> returns false and the listener is invisible to clients.
/// This mirrors NarcoNet's setter pattern; it's the cleanest workaround for
/// "DI needs to construct me but my dependency is async/runtime-loaded."
/// </summary>
[Injectable]
public class ModSyncHttpListener(
    ISptLogger<ModSyncHttpListener> logger,
    ISptLogger<SyncUtil> syncUtilLogger,
    HttpFileUtil httpFileUtil) : IHttpListener
{
    // Filled in by Initialize() after config has been loaded from disk.
    // `Config?` (nullable) — keeps the compiler happy that this might be null
    // before init, and forces a null-check before use.
    private Config? _config;
    private SyncUtil? _syncUtil;
    private string _modVersion = "0.0.0";

    /// <summary>
    /// JSON options reused across all responses.
    ///
    /// `IncludeFields = true` — this is the important one. ModFile/SyncPath in
    /// ModSync.Utility use public *fields* (not properties) named `hash`, `path`,
    /// etc. System.Text.Json ignores fields by default; turning this on makes it
    /// pick them up so the JSON keys match what the client deserializes by name.
    ///
    /// No `PropertyNamingPolicy` set — we want field names verbatim (lowercase)
    /// because that's what the existing client + the wire format expect.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
    };

    /// <summary>
    /// Called by <see cref="ModSyncMod"/> once config has loaded from disk.
    /// After this runs, <see cref="CanHandle"/> starts returning true for our routes.
    /// </summary>
    public void Initialize(Config config, string modVersion)
    {
        _config = config;
        // Each ISptLogger&lt;T&gt; tags its log lines with T's name — so we pass SyncUtil's
        // own typed logger here rather than reusing our listener's, keeping the source
        // category accurate in the SPT log output.
        _syncUtil = new SyncUtil(config, syncUtilLogger);
        _modVersion = modVersion;
    }

    /// <summary>
    /// First gate SPT calls. Return true only when we're ready (config loaded)
    /// AND the request looks like one of ours.
    ///
    /// `MongoId sessionId` — SPT's per-player session ID type. We don't use it
    /// (ModSync is server-wide, not per-player), so the parameter is named but
    /// ignored. Discarding via `_` would also work.
    /// </summary>
    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        if (_config is null) return false;
        var path = context.Request.Path.Value;
        return context.Request.Method == "GET"
            && path is not null
            && path.StartsWith("/modsync/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Route dispatch + top-level error handling. Each route handler can either
    /// complete the response normally or throw <see cref="HttpError"/> to signal
    /// a specific status code (e.g. 404 for a missing file).
    /// </summary>
    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        // We checked these are non-null in CanHandle, but the compiler doesn't
        // know that across method boundaries. `!` is the null-forgiving operator —
        // tells the compiler "trust me, this isn't null here". Use sparingly.
        var path = context.Request.Path.Value!;

        try
        {
            // Route table — order matters only for the `/fetch/` prefix (everything
            // else is an exact match). Switch expression keeps the dispatch compact.
            Task task = path switch
            {
                "/modsync/version" => HandleVersionAsync(context),
                "/modsync/paths" => HandlePathsAsync(context),
                "/modsync/exclusions" => HandleExclusionsAsync(context),
                "/modsync/hashes" => HandleHashesAsync(context),
                _ when path.StartsWith("/modsync/fetch/", StringComparison.Ordinal)
                    => HandleFetchAsync(context, path["/modsync/fetch/".Length..]),
                _ => throw new HttpError(404, "Corter-ModSync: Unknown route"),
            };

            await task;
        }
        catch (HttpError ex)
        {
            // Our own controlled errors — log + send the chosen status.
            logger.Error($"Corter-ModSync: [{context.Request.Method} {path}] -> {ex.Code} {ex.Message}");
            await WriteTextAsync(context, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // Anything else is unexpected — log full trace, send 500.
            logger.Error($"Corter-ModSync: error handling [{context.Request.Method} {path}]:\n{ex}");
            await WriteTextAsync(context, 500, $"Corter-ModSync: error handling request:\n{ex.Message}");
        }
    }

    // ─── Route handlers ────────────────────────────────────────────────────────

    /// <summary>GET /modsync/version → JSON-encoded version string, e.g. "0.11.1".</summary>
    private async Task HandleVersionAsync(HttpContext context)
    {
        // Note the JSON string is `"0.11.1"` (with the quotes) — the client
        // does `Json.Deserialize&lt;string&gt;` on it, so it expects a JSON string.
        await WriteJsonAsync(context, 200, _modVersion);
    }

    /// <summary>GET /modsync/paths → list of configured syncpaths with windows-style separators.</summary>
    private async Task HandlePathsAsync(HttpContext context)
    {
        // Project to anonymous objects so we can rewrite `path` to backslashes
        // without mutating the shared SyncPath instances (they're held by Config).
        // Anonymous types use properties, not fields, so they ignore IncludeFields —
        // serialization still produces the expected key names via the property names below.
        var winPaths = _config!.SyncPaths.ConvertAll(sp => new
        {
            path = PathExt.WinPath(sp.path),
            name = sp.name,
            enabled = sp.enabled,
            enforced = sp.enforced,
            silent = sp.silent,
            restartRequired = sp.restartRequired,
        });

        await WriteJsonAsync(context, 200, winPaths);
    }

    /// <summary>GET /modsync/exclusions → list of exclusion glob strings.</summary>
    private async Task HandleExclusionsAsync(HttpContext context)
    {
        await WriteJsonAsync(context, 200, _config!.Exclusions);
    }

    /// <summary>
    /// GET /modsync/hashes?path=X&amp;path=Y → nested dict: syncpath → file → ModFile.
    ///
    /// `path` query params are optional. If provided, only those syncpaths are
    /// hashed — except syncpaths marked `enforced=true` are always included
    /// (matches the TS behaviour: built-ins like the ModSync DLL must always
    /// be reported so the client can self-update).
    /// </summary>
    private async Task HandleHashesAsync(HttpContext context)
    {
        var query = context.Request.Query;
        IEnumerable<SyncPath> pathsToHash = _config!.SyncPaths;

        if (query.ContainsKey("path"))
        {
            // ASP.NET Core has already URL-decoded these. The client sends paths
            // with forward slashes (it normalizes via `Replace("\\", "/")`), and
            // our config paths can be either separator depending on what the user
            // wrote in config.jsonc — so normalize both sides before comparing.
            var requested = new HashSet<string>(
                query["path"].Where(s => s is not null).Select(s => PathExt.UnixPath(s!)),
                StringComparer.Ordinal);

            pathsToHash = _config.SyncPaths
                .Where(sp => sp.enforced || requested.Contains(PathExt.UnixPath(sp.path)));
        }

        var hashes = await _syncUtil!.HashModFilesAsync(pathsToHash);
        await WriteJsonAsync(context, 200, hashes);
    }

    /// <summary>
    /// GET /modsync/fetch/{file} → stream the requested file's bytes.
    ///
    /// The `{file}` part is everything after `/modsync/fetch/` in the URL,
    /// URL-encoded by the client. After unescaping + sanitization (which throws
    /// HttpError 400 on traversal attempts), we hand off to SPT's HttpFileUtil
    /// which sets Content-Type/Content-Length headers and streams the file.
    /// </summary>
    private async Task HandleFetchAsync(HttpContext context, string rawPath)
    {
        // The TS used `decodeURIComponent`. C# equivalent for full URI component
        // decoding (handles %20 etc, doesn't choke on + like the older variant).
        var filePath = Uri.UnescapeDataString(rawPath);

        var sanitizedPath = SyncUtil.SanitizeDownloadPath(filePath, _config!.SyncPaths);

        if (!File.Exists(sanitizedPath))
        {
            throw new HttpError(404, $"Corter-ModSync: file '{filePath}' not found on server.");
        }

        // SendFile internally derives Content-Type from the file extension and
        // sets Content-Length, then streams the body to the client.
        await httpFileUtil.SendFile(context.Response, sanitizedPath);
    }

    // ─── Response helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Serialize <paramref name="payload"/> to JSON and write it as the response body.
    ///
    /// We bypass SPT's HttpResponseUtil.NoBody because it uses SPT's internal JsonUtil
    /// (Newtonsoft-flavoured) which has different defaults than what the client expects.
    /// Doing it directly with our shared <see cref="JsonOpts"/> guarantees the wire
    /// format is exactly what the BepInEx client deserializes.
    /// </summary>
    private static async Task WriteJsonAsync<T>(HttpContext context, int statusCode, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        await context.Response.Body.WriteAsync(bytes);
    }

    /// <summary>Write a plain-text response body. Used for error pages.</summary>
    private static async Task WriteTextAsync(HttpContext context, int statusCode, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        await context.Response.Body.WriteAsync(bytes);
    }
}
