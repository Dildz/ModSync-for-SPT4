using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Build an HttpClient that accepts SPT's self-signed TLS cert.
    ///
    /// SPT 4 serves HTTP over HTTPS on port 6969 with a self-signed certificate.
    /// A bare <c>new HttpClient()</c> would reject the cert and throw on every
    /// request. SPT's own <c>SPT.Common.Http.Client</c> works around this by
    /// supplying an <see cref="HttpClientHandler"/> with a permissive validation
    /// callback — but that bypass only applies to SPT's *own* HttpClient instance,
    /// not globally. So we need to do the same on each of ours.
    ///
    /// This is SPT-4-specific: the SPT 3 server was plain HTTP, so the upstream
    /// 0.11.1 client didn't need this. Lambda discards `(_, _, _, _) =&gt; true`
    /// match SPT's own pattern — we don't care about the cert at all, the server
    /// is on localhost (or a trusted LAN/VPS the user has explicitly configured).
    /// </summary>
    private static HttpClient NewHttpClient() =>
        new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });

    private async Task<string> GetJson(string path)
    {
        try
        {
            using var client = NewHttpClient();
            client.DefaultRequestHeaders.Add("modsync-version", pluginVersion.ToString());
            client.Timeout = TimeSpan.FromMinutes(5);
            var json = await client.GetStringAsync($"{RequestHandler.Host}{path}");
            return json;
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

        await limiter.WaitAsync();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = NewHttpClient();
                if (retryCount > 0)
                    client.Timeout = TimeSpan.FromMinutes(10);

                using var responseStream = await client.GetStreamAsync($"{RequestHandler.Host}/modsync/fetch/{file}");
                using var fileStream = new FileStream(downloadPath, FileMode.Create);

                await responseStream.CopyToAsync(fileStream);

                limiter.Release();
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
