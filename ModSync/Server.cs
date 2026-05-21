using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModSync.Utility;
using SPT.Common.Http;
using SPT.Common.Utils;

namespace ModSync;

using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

public class Server(Version pluginVersion)
{
    /// <summary>
    /// Class constructor — runs once the first time anything touches `Server`. Installs a
    /// global TLS bypass on <see cref="ServicePointManager"/>.
    ///
    /// **Why both this AND the per-handler callback?** Mono/UnityTLS doesn't always honor
    /// <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c> — particularly
    /// on renegotiated TLS sessions or connections pulled from the pool. When that path is
    /// taken, the older <see cref="ServicePointManager.ServerCertificateValidationCallback"/>
    /// is consulted instead. Setting both is belt + suspenders that costs nothing.
    /// </summary>
    static Server()
    {
        ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
    }

    /// <summary>
    /// Single shared HttpClient instance, reused for every request.
    ///
    /// **Why static / shared.** `new HttpClient()` per call is a long-standing .NET
    /// anti-pattern — each instance opens its own socket pool and (for HTTPS) negotiates
    /// a fresh TLS handshake. On large syncs (1000+ files) this exhausted Mono/UnityTLS
    /// resources mid-sync, producing `UNITYTLS_INTERNAL_ERROR` handshake failures that
    /// not even the 5x retry loop could recover from. A single shared client lets .NET's
    /// connection pool keep TCP+TLS sessions alive across the whole sync, dropping
    /// handshake count from ~2000 to a handful.
    ///
    /// **`ServerCertificateCustomValidationCallback`** bypasses SPT's self-signed cert.
    /// SPT 4 serves over HTTPS on port 6969; SPT's own `SPT.Common.Http.Client` does
    /// the same bypass on its private instance, but only on its own — not globally.
    ///
    /// **`Timeout`** is generous (10 minutes) because `/modsync/fetch` streams files of
    /// arbitrary size; small files finish in milliseconds, huge bundles can legitimately
    /// take minutes on slow connections. Per-request cancellation is handled separately
    /// via the `CancellationToken` passed into `DownloadFile`.
    /// </summary>
    private static readonly HttpClient SharedClient = new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Build a GET request with the modsync-version header. Created per-call (not stored
    /// on the shared client) because `HttpClient.DefaultRequestHeaders` is process-global,
    /// which would race across concurrent file downloads.
    /// </summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("modsync-version", pluginVersion.ToString());
        return request;
    }

    private async Task<string> GetJson(string path)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"{RequestHandler.Host}{path}");
            using var response = await SharedClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"There was an error performing request.\n{e.Message}\n{e.StackTrace}");
            throw;
        }
    }

    public async Task DownloadFile(string file, string downloadDir, SemaphoreSlim limiter, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var downloadPath = Path.Combine(downloadDir, file);
        VFS.CreateDirectory(downloadPath.GetDirectory());

        var retryCount = 0;

        await limiter.WaitAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // URL-encode the file path so spaces, special chars, and backslashes
                    // survive the HTTP layer intact. We normalise `\` to `/` first so the
                    // forward-slashes act as path separators (which we want to keep raw),
                    // then encode each segment individually — that way spaces become %20
                    // etc. but the path structure stays parseable. Mono's URI handling is
                    // less forgiving than desktop .NET about unescaped chars; on the server
                    // side we already call Uri.UnescapeDataString to decode, so this is
                    // the matched encoding step.
                    var encodedFile = string.Join("/",
                        file.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

                    // SharedClient is reused — DO NOT dispose. Per-request state lives on
                    // the HttpRequestMessage, which we dispose normally below.
                    using var request = NewRequest(HttpMethod.Get, $"{RequestHandler.Host}/modsync/fetch/{encodedFile}");
                    using var response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var responseStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(downloadPath, FileMode.Create);
                    await responseStream.CopyToAsync(fileStream, 81920, cancellationToken);

                    return;
                }
                catch (Exception e)
                {
                    if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
                        throw;

                    if (retryCount < 5)
                    {
                        Plugin.Logger.LogError($"Failed to download '{file}'. Retrying ({retryCount + 1}/5)...");
                        Plugin.Logger.LogDebug(e);
                        await Task.Delay(500, cancellationToken);
                        retryCount++;
                        continue;
                    }

                    Plugin.Logger.LogError($"Failed to download '{file}'. Exiting...");
                    Plugin.Logger.LogError(e);
                    throw;
                }
            }
        }
        finally
        {
            // Always release — previously this only happened on the success path, so a
            // 5x-retry exhaustion (which throws) leaked a slot. Cancellation hid the bug
            // because the whole sync got torn down anyway, but doing this properly costs
            // nothing.
            limiter.Release();
        }
    }

    /// <summary>
    /// Append `?headless=1` (or `&amp;headless=1` if a query string already exists) when
    /// this client is a Fika headless instance. Server uses the flag to pick the right
    /// filter set on /exclusions, /hashes, and /includes. /version and /paths don't
    /// vary by client kind so they skip the flag.
    /// </summary>
    private static string WithHeadlessFlag(string path)
    {
        if (!Plugin.IsHeadless) return path;
        var sep = path.Contains('?') ? '&' : '?';
        return $"{path}{sep}headless=1";
    }

    public async Task<string> GetModSyncVersion()
    {
        return Json.Deserialize<string>(await GetJson("/modsync/version"));
    }

    public async Task<List<SyncPath>> GetModSyncPaths()
    {
        return Json.Deserialize<List<SyncPath>>(await GetJson("/modsync/paths"));
    }

    public async Task<List<string>> GetModSyncExclusions()
    {
        return Json.Deserialize<List<string>>(await GetJson(WithHeadlessFlag("/modsync/exclusions")));
    }

    /// <summary>
    /// Fetch the headless allowlist (BepInEx/plugins paths in wire form). Returns an
    /// empty list for player clients — they don't have an allowlist concept, but the
    /// endpoint exists so we can call it unconditionally without branching here.
    /// </summary>
    public async Task<List<string>> GetModSyncIncludes()
    {
        return Json.Deserialize<List<string>>(await GetJson(WithHeadlessFlag("/modsync/includes")));
    }

    public async Task<SyncPathModFiles> GetRemoteModFileHashes(List<SyncPath> syncPaths)
    {
        var pathQuery = string.Join("&path=", syncPaths.Select(path => Uri.EscapeUriString(path.path.Replace(@"\", "/"))));
        return Json.Deserialize<SyncPathModFiles>(
                await GetJson(WithHeadlessFlag($"/modsync/hashes?path={pathQuery}"))
            )
            .ToDictionary(
                item => item.Key,
                item => item.Value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase
            );
    }
}
