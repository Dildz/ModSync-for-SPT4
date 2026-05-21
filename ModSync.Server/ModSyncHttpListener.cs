using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModSync.Utility;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.DI;
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
///
/// **`InjectionType.Singleton` is mandatory here**, not optional. SPT's DI defaults
/// to transient — every resolution builds a fresh instance. Without Singleton,
/// `ModSyncMod`'s ctor gets one instance (and we call Initialize on it), but SPT's
/// `HttpServer` constructor takes `IEnumerable&lt;IHttpListener&gt;` which triggers a
/// SEPARATE resolution → a different instance ends up in the dispatch list, with
/// its `_config` still null → `CanHandle` returns false → SPT logs `[UNHANDLED]`
/// and serves 404. Singleton makes both resolutions return the same instance.
///
/// **Headless routing.** Clients append `?headless=1` to /modsync/{exclusions,hashes,
/// includes} when Fika headless is detected. The listener parses that flag and threads
/// it through to Config + SyncUtil so the response reflects the headless-allowlist
/// rules. Player clients omit the param and get the full denylist-only behavior.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader + 1)]
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
                "/modsync/includes" => HandleIncludesAsync(context),
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// True if the request carries `?headless=1` in its query string. Used by the
    /// per-route handlers to pick the right filter set. Headless clients (Fika
    /// headless) append this flag; players omit it.
    /// </summary>
    private static bool IsHeadlessRequest(HttpContext context)
    {
        return context.Request.Query.TryGetValue("headless", out var v)
            && v == "1";
    }

    // ─── Route handlers ────────────────────────────────────────────────────────

    /// <summary>GET /modsync/version → JSON-encoded version string, e.g. "0.11.1".</summary>
    private async Task HandleVersionAsync(HttpContext context)
    {
        // Note the JSON string is `"0.11.1"` (with the quotes) — the client
        // does `Json.Deserialize&lt;string&gt;` on it, so it expects a JSON string.
        await WriteJsonAsync(context, 200, _modVersion);
    }

    /// <summary>
    /// GET /modsync/paths → list of configured syncpaths in wire form
    /// (game-root-relative, backslash separators).
    ///
    /// Same syncpath list for both client kinds — there's no per-syncpath routing
    /// in the current schema. Filtering happens inside each syncpath at the file
    /// level (see /hashes), not at the syncpath level.
    /// </summary>
    private async Task HandlePathsAsync(HttpContext context)
    {
        // Project to anonymous objects so we can rewrite paths to the wire format
        // without mutating the shared SyncPath instances (they're held by Config).
        // Anonymous types use properties, not fields, so they ignore IncludeFields —
        // serialization still produces the expected key names via the property names below.
        //
        // Pipeline per path: WinPath (normalize separators) → ToWirePath (translate
        // server-cwd-relative → game-root-relative). Order matters: ToWirePath looks
        // for `..\` prefixes so the path needs to be backslash-normalized first.
        var winPaths = _config!.SyncPaths.ConvertAll(sp => new
        {
            path = PathExt.ToWirePath(PathExt.WinPath(sp.path)),
            name = sp.name,
            enabled = sp.enabled,
            enforced = sp.enforced,
            silent = sp.silent,
            restartRequired = sp.restartRequired,
        });

        await WriteJsonAsync(context, 200, winPaths);
    }

    /// <summary>
    /// GET /modsync/exclusions → flat list of exclusion glob strings the client
    /// should ignore in its local-side walk.
    ///
    /// Returned content depends on the client kind:
    ///   • Player (no ?headless=1): GlobalExclusions ∪ ClientExclusions
    ///   • Headless (?headless=1):   GlobalExclusions only — ClientExclusions
    ///                               doesn't apply to headless, and the allowlist
    ///                               (HeadlessIncludes) is served separately via
    ///                               /modsync/includes.
    /// </summary>
    private async Task HandleExclusionsAsync(HttpContext context)
    {
        var isHeadless = IsHeadlessRequest(context);

        var effective = isHeadless
            ? _config!.GlobalExclusions
            : _config!.GlobalExclusions.Concat(_config.ClientExclusions).ToList();

        await WriteJsonAsync(context, 200, effective);
    }

    /// <summary>
    /// GET /modsync/includes → flat list of allowlist path strings (BepInEx/plugins
    /// paths in wire form, e.g. `BepInEx\plugins\SAIN`). Only meaningful for headless
    /// clients; players get an empty list.
    ///
    /// The headless client uses this to restrict its OWN local-side walk to allowlisted
    /// paths inside plugins/ — otherwise the local-vs-server diff would falsely flag
    /// every non-allowlisted local file as "extra" or "removed".
    ///
    /// Paths are translated to wire form (`../BepInEx/plugins/SAIN` → `BepInEx\plugins\SAIN`)
    /// for the same reason /paths translates: the client resolves these against its own
    /// cwd (game root) and would mismatch a `../`-prefixed entry.
    /// </summary>
    private async Task HandleIncludesAsync(HttpContext context)
    {
        var isHeadless = IsHeadlessRequest(context);

        // Players don't have an allowlist concept — return empty so the client can
        // call this endpoint unconditionally without branching on its own kind.
        IEnumerable<string> includes = isHeadless
            ? _config!.HeadlessIncludes.ConvertAll(p => PathExt.ToWirePath(PathExt.WinPath(p)))
            : [];

        await WriteJsonAsync(context, 200, includes);
    }

    /// <summary>
    /// GET /modsync/hashes?path=X&amp;path=Y[&amp;headless=1] → nested dict: syncpath →
    /// file → ModFile.
    ///
    /// `path` query params are optional. If provided, only those syncpaths are
    /// hashed — except syncpaths marked `enforced=true` are always included
    /// (matches the TS behaviour: built-ins like the ModSync DLL must always
    /// be reported so the client can self-update).
    ///
    /// `headless=1` selects the headless filter set (see SyncUtil class doc).
    /// Player clients omit it and get the player filter set.
    /// </summary>
    private async Task HandleHashesAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var isHeadless = IsHeadlessRequest(context);
        IEnumerable<SyncPath> pathsToHash = _config!.SyncPaths;

        if (query.ContainsKey("path"))
        {
            // The client sends paths in WIRE form (game-root-relative, forward-slash
            // normalized via `Replace("\\", "/")`). Translate each config syncpath
            // into the same wire form so we can compare like-for-like.
            var requested = new HashSet<string>(
                query["path"].Where(s => s is not null).Select(s => PathExt.UnixPath(s!)),
                StringComparer.Ordinal);

            pathsToHash = _config.SyncPaths
                .Where(sp => sp.enforced
                    || requested.Contains(PathExt.UnixPath(PathExt.ToWirePath(PathExt.WinPath(sp.path)))));
        }

        // SyncUtil returns paths in server-cwd terms (e.g. `..\BepInEx\plugins\...`).
        // Translate every outer and inner key to wire form before sending — the client
        // resolves these relative to its own cwd (game root) and would fail otherwise.
        var serverHashes = await _syncUtil!.HashModFilesAsync(pathsToHash, isHeadless);
        var wireHashes = serverHashes.ToDictionary(
            outer => PathExt.ToWirePath(outer.Key),
            outer => outer.Value.ToDictionary(
                inner => PathExt.ToWirePath(inner.Key),
                inner => inner.Value,
                StringComparer.Ordinal),
            StringComparer.Ordinal);

        await WriteJsonAsync(context, 200, wireHashes);
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
        var wirePath = Uri.UnescapeDataString(rawPath);

        // Client sends a wire-form path (game-root-relative); translate back to
        // server cwd before resolving against syncpaths + the filesystem.
        var filePath = PathExt.ToServerPath(wirePath);

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
