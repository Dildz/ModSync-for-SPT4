using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModSync.Utility;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Utils;
// HttpServerHelper / HttpFileUtil are both under SPTarkov.Server.Core.Utils.

namespace ModSync.Server;

/// <summary>
/// HTTP listener for the /modsync/* routes: /version, /paths, /exclusions, /hashes,
/// /fetch/{file}. The <c>exclusions</c> + <c>paths</c> endpoints are the same for
/// every client (universal denylist + syncpath shape). Per-client divergence happens
/// only on <c>/hashes</c>: a client appends <c>?headless=1</c> when it's a Fika
/// headless install, and the server applies the <c>headlessIncludes</c> allowlist to
/// the BepInEx/plugins folder before returning hashes.
///
/// Players' per-install opt-outs are not seen by the server — they live in each
/// install's <c>ModSync_Data/Exclusions.jsonc</c> and are applied client-side after
/// the server's response arrives.
///
/// How SPT 4 wires this up: anything implementing <see cref="IHttpListener"/> with
/// <c>[Injectable]</c> is collected by the DI container at startup. When a request
/// arrives, SPT iterates registered listeners and calls <see cref="CanHandle"/> on
/// each; the first one that says yes gets <see cref="HandleAsync"/> invoked. SPT's own
/// <c>SptHttpListener</c> only matches routes registered on its internal router,
/// so our <c>/modsync/</c> prefix is safe — there's no conflict.
///
/// **Initialize() setter pattern.** This listener is constructed by DI during the
/// container build, but the <see cref="Config"/> it needs comes from disk and is
/// only available once <see cref="ModSyncMod"/>.OnLoadAsync runs. So we expose
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
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 1)]
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
    /// </summary>
    // SPT 4.1 dropped the sessionId parameter from CanHandle — the decision is made purely
    // on the request itself. We never used it here anyway (we only look at method + path).
    public bool CanHandle(HttpContext context)
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
    // Renamed from Handle in SPT 4.1, and it now receives a CancellationToken that is
    // signalled if the client drops the request or the server shuts down.
    public async Task HandleAsync(MongoId sessionId, HttpContext context, CancellationToken cancellationToken = default)
    {
        var path = context.Request.Path.Value!;

        try
        {
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
            logger.Error($"Corter-ModSync: [{context.Request.Method} {path}] -> {ex.Code} {ex.Message}");
            await WriteTextAsync(context, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            logger.Error($"Corter-ModSync: error handling [{context.Request.Method} {path}]:\n{ex}");
            await WriteTextAsync(context, 500, $"Corter-ModSync: error handling request:\n{ex.Message}");
        }
    }

    // ─── Route handlers ────────────────────────────────────────────────────────

    /// <summary>GET /modsync/version → JSON-encoded version string, e.g. "0.12.0".</summary>
    private async Task HandleVersionAsync(HttpContext context)
    {
        await WriteJsonAsync(context, 200, _modVersion);
    }

    /// <summary>
    /// GET /modsync/paths → list of configured syncpaths in wire form
    /// (game-root-relative, backslash separators).
    /// </summary>
    private async Task HandlePathsAsync(HttpContext context)
    {
        // The Updater (desktop players) and patcher (headless) are each enforced for the audience
        // that runs them and relaxed for the other, so the unused component can be trimmed via
        // Exclusions.jsonc while the needed one can't be accidentally removed (see ResolveEnforced).
        // The client appends ?headless=1 here exactly as it does on /hashes.
        var isHeadless = context.Request.Query.ContainsKey("headless");

        // Version gate. A client only gets the full config if it announces a version matching
        // ours; anything else — a different version, or a pre-0.12.6 client that doesn't send
        // one at all — receives ONLY ModSync's own components.
        //
        // This is what makes self-update-first work for clients that don't yet have the
        // self-update-first code. A v0.12.5 client computes its whole diff (including
        // REMOVALS) from the paths we hand it, so handing it only builtins makes it
        // structurally incapable of proposing to delete a mod. It updates ModSync, restarts,
        // and comes back speaking our version.
        var clientVersion = context.Request.Query.TryGetValue("version", out var v) ? v.ToString() : null;
        var versionMatches = clientVersion == _modVersion;

        if (!versionMatches)
            logger.LogWithColor(
                $"Corter-ModSync: client reports version '{clientVersion ?? "unknown"}' (server is {_modVersion}) "
                + "— serving ModSync's own components only until it updates.",
                Color.Grey);

        await WriteJsonAsync(context, 200, BuildPathsResponse(_config!.SyncPaths, isHeadless, versionMatches));
    }

    /// <summary>
    /// Builds the /modsync/paths payload. Pure function, split out from the HTTP handler so the
    /// audience rules can be tested without HTTP plumbing — a mismatch between what this
    /// advertises and what <see cref="SyncUtil.HashModFilesAsync"/> serves once shipped a
    /// KeyNotFoundException to every headless client, and nothing caught it.
    ///
    /// Per path: WinPath (normalize separators) → ToWirePath (server-cwd-relative →
    /// game-root-relative). Projected into a DTO rather than mutating the shared SyncPath
    /// instances, which are held by Config. Member names are lowercase on purpose — the
    /// serializer applies no naming policy, so these are the literal JSON keys the client
    /// deserializes by name.
    /// </summary>
    public static List<SyncPathDto> BuildPathsResponse(List<SyncPath> syncPaths, bool isHeadless, bool versionMatches = true)
    {
        // Version mismatch: hand back only ModSync's own components. The client then has
        // nothing else it could act on, so it updates itself and restarts. See HandlePathsAsync.
        if (!versionMatches)
            syncPaths = syncPaths.FindAll(sp => Builtins.IsBuiltinWirePath(PathExt.ToWirePath(PathExt.WinPath(sp.path))));

        // Don't advertise a path we will never serve to this audience. HashModFilesAsync skips
        // `headless:false` paths for headless clients, so listing them here would leave a
        // client asking for a path that comes back with no entry at all — which older clients
        // (<=0.12.5) index directly and throw KeyNotFoundException on, failing ModSync's load.
        // Filtering at the source keeps both endpoints telling the same story.
        var visiblePaths = isHeadless
            ? syncPaths.FindAll(sp => sp.headless)
            : syncPaths;

        return visiblePaths.ConvertAll(sp => new SyncPathDto(
            path: PathExt.ToWirePath(PathExt.WinPath(sp.path)),
            name: sp.name,
            enabled: sp.enabled,
            // The Updater (desktop players) and patcher (headless) are each enforced for the
            // audience that runs them and relaxed for the other.
            enforced: ConfigUtil.ResolveEnforced(sp, isHeadless),
            silent: sp.silent,
            restartRequired: sp.restartRequired,
            headless: sp.headless,
            // baseFiles go over the wire game-root-relative, same as `path`, so the client can
            // resolve them against its own cwd when checking for .modsync-bak ownership.
            baseFiles: sp.baseFiles.ConvertAll(bf => PathExt.ToWirePath(PathExt.WinPath(bf)))));
    }

    /// <summary>
    /// GET /modsync/exclusions → flat list of exclusion glob strings the client
    /// should ignore in its local-side walk. Same list for everyone — the client
    /// combines this with its own Exclusions.json before walking.
    /// </summary>
    private async Task HandleExclusionsAsync(HttpContext context)
    {
        await WriteJsonAsync(context, 200, _config!.Exclusions);
    }

    /// <summary>
    /// GET /modsync/hashes[?headless=1][&amp;path=X&amp;path=Y] → nested dict:
    /// syncpath → file → ModFile.
    ///
    /// <c>?headless=1</c> (presence-only — value ignored) tells the server to apply
    /// <c>headlessIncludes</c> as an allowlist over BepInEx/plugins. Client appends it
    /// when it detects Fika headless.
    ///
    /// <c>?path</c> query params are optional. If provided, only those syncpaths are
    /// hashed — except syncpaths marked <c>enforced=true</c> are always included
    /// (built-ins like the ModSync DLL must always be reported so the client can
    /// self-update).
    /// </summary>
    private async Task HandleHashesAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var isHeadless = query.ContainsKey("headless");

        // Always hash across ALL configured syncpaths so ownership (most-specific wins) is
        // computed over the full set — a disabled/opt-out override must carve its files out
        // of an enclosing catch-all even though the client didn't request it. `isActive`
        // then limits what's actually RETURNED to the paths the client asked for (plus
        // enforced, which always applies). Without this, a toggled-off optional path would
        // drop out of the walk and the catch-all would re-serve its files.
        Func<SyncPath, bool> isActive = _ => true;
        if (query.ContainsKey("path"))
        {
            // The client sends paths in WIRE form (game-root-relative, forward-slash
            // normalized via `Replace("\\", "/")`). Translate each config syncpath
            // into the same wire form so we can compare like-for-like.
            var requested = new HashSet<string>(
                query["path"].Where(s => s is not null).Select(s => PathExt.UnixPath(s!)),
                StringComparer.Ordinal);

            isActive = sp => sp.enforced
                || requested.Contains(PathExt.UnixPath(PathExt.ToWirePath(PathExt.WinPath(sp.path))));
        }

        // SyncUtil returns paths in server-cwd terms (e.g. `..\BepInEx\plugins\...`).
        // Translate every outer and inner key to wire form before sending — the client
        // resolves these relative to its own cwd (game root) and would fail otherwise.
        var serverHashes = await _syncUtil!.HashModFilesAsync(_config!.SyncPaths, isHeadless, isActive);
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
    /// </summary>
    private async Task HandleFetchAsync(HttpContext context, string rawPath)
    {
        var wirePath = Uri.UnescapeDataString(rawPath);

        // Client sends a wire-form path (game-root-relative); translate back to
        // server cwd before resolving against syncpaths + the filesystem.
        var filePath = PathExt.ToServerPath(wirePath);

        var sanitizedPath = SyncUtil.SanitizeDownloadPath(filePath, _config!.SyncPaths);

        if (!File.Exists(sanitizedPath))
        {
            throw new HttpError(404, $"Corter-ModSync: file '{filePath}' not found on server.");
        }

        await httpFileUtil.SendFile(context.Response, sanitizedPath);
    }

    // ─── Response helpers ──────────────────────────────────────────────────────

    private static async Task WriteJsonAsync<T>(HttpContext context, int statusCode, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        await context.Response.Body.WriteAsync(bytes);
    }

    private static async Task WriteTextAsync(HttpContext context, int statusCode, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        await context.Response.Body.WriteAsync(bytes);
    }
}

/// <summary>
/// Wire shape of one entry in GET /modsync/paths.
///
/// Member names are deliberately lowercase: <see cref="ModSyncHttpListener"/> serializes with
/// no naming policy, so these are the literal JSON keys, and the client's SyncPath constructor
/// is matched against them by name. Renaming a member here silently breaks that binding.
/// </summary>
public record SyncPathDto(
    string path,
    string name,
    bool enabled,
    bool enforced,
    bool silent,
    bool restartRequired,
    bool headless,
    List<string> baseFiles);
