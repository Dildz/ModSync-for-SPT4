using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ModSync.Utility;
// Alias: SPT.Custom.Utils also declares a Crc32. It resolves against the 4.1.1 reference
// assemblies at COMPILE time but is ABSENT at RUNTIME on 4.1.3 - binding to it is exactly the
// typeref failure that breaks other mods on 4.1.3.
using ModSyncCrc32 = ModSync.Utility.Crc32;
using Newtonsoft.Json;

namespace ModSync;

/// <summary>
/// Fetches server-mod asset bundles for a Fika headless client, before the game loads them.
///
/// ---- the problem -------------------------------------------------------
/// SPT 4.1.3 moved bundle acquisition out of the game client and into the launcher.
/// `BundleManager.DownloadBundle` and `BundleManager.ShouldAcquire` were both REMOVED (they
/// exist in 4.1.2 and earlier). The launcher now downloads bundles into a CRC-keyed cache and
/// the client only ever reads from it:
///
/// <code>
/// // SPT 4.1.3, BundleManager.GetBundleFilePath
/// var cached = "SPT_Runtime/user/cache/bundles/" + bundle.Crc.ToString("X8") + "/" + bundle.FileName;
/// if (VFS.Exists(cached)) return cached;
/// return "SPT_Runtime/" + bundle.ModPath + "/bundles/" + bundle.FileName;
/// </code>
///
/// A Fika headless never runs the launcher - the container execs EscapeFromTarkov.exe directly -
/// so it cannot obtain a single server-mod bundle. Every bundle-shipping mod then fails with
/// "N of N mod bundles were found in neither the bundle cache nor a mod folder", and the client
/// stalls before Fika.Headless starts.
///
/// ---- why this lives in the PATCHER, not the plugin ---------------------
/// It was written in the client plugin first, hooked where ModSync detects headless. That runs at
/// BepInEx *chainloader* time, and measurement showed it loses the race: SPT requests
/// `/singleplayer/bundles` and logs its "found in neither" error roughly 60 log lines BEFORE
/// ModSync's sync finishes. The ordering had looked safe only because, on a boot with nothing to
/// download, ModSync finished in milliseconds. Adding a 68 MB download consumed that slack.
///
/// The patcher runs at BepInEx *preloader* time - strictly before any plugin, and therefore
/// strictly before SPT asks for the manifest. It is also the headless's own applier by
/// convention (players use ModSync.Updater.exe; a headless has no UI to run it), so this is the
/// natural home rather than a special case.
///
/// The cost of being that early is that nothing SPT provides is loaded yet: no `RequestHandler`,
/// no `BundleManager`, no `VFS`. So the HTTP call, the manifest decode and the path construction
/// are all done by hand here. That is a deliberate trade for correct ordering.
///
/// ---- remove this when the official headless image ships ----------------
/// It self-neutralises meanwhile: once every bundle is cached, a run is one manifest fetch and
/// no downloads.
/// </summary>
public static class HeadlessBundles
{
    /// <summary>Must match BundleManager.GetBundleFilePath exactly, or SPT will not look here.</summary>
    private const string CacheRelative = @"SPT_Runtime\user\cache\bundles";

    /// <summary>
    /// Present only on a headless install, and carries the server URL - so one file both
    /// identifies the audience and tells us where to fetch from. A player install has no such
    /// file, which is exactly the gate we want: players get their bundles from the launcher.
    /// </summary>
    private const string HeadlessConfigName = "HeadlessConfig.json";

    /// <summary>Manifest entry. Only the three fields we need; Json.NET ignores the rest.</summary>
    private class ManifestEntry
    {
        [JsonProperty("FileName")] public string FileName { get; set; }
        [JsonProperty("Crc")] public uint Crc { get; set; }
        [JsonProperty("Size")] public long Size { get; set; }
    }

    private class HeadlessConfig
    {
        [JsonProperty("BackendUrl")] public string BackendUrl { get; set; }
    }

    /// <summary>
    /// Entry point. Returns silently on a player install. Never throws - a bundle problem must
    /// not stop the game booting, and this runs inside the BepInEx preloader where an escaping
    /// exception is considerably worse than a missing cosmetic asset.
    /// </summary>
    public static void Acquire(string gameDir, Action<string> log, Action<string> warn)
    {
        try
        {
            var backendUrl = ReadBackendUrl(gameDir);
            if (backendUrl == null)
                return; // not a headless install - the launcher handles bundles


            var manifest = FetchManifest(backendUrl);
            if (manifest == null || manifest.Count == 0)
                return;

            var cacheRoot = Path.Combine(gameDir, CacheRelative);
            var missing = new List<ManifestEntry>();
            foreach (var entry in manifest)
            {
                if (!IsCached(cacheRoot, entry))
                    missing.Add(entry);
            }

            if (missing.Count == 0)
            {
                log($"Bundles: {manifest.Count} in manifest, all cached.");
                return;
            }

            // This blocks the game thread, by design (see Plugin.Awake). On a first run that can be
            // tens of megabytes with no window drawn yet, which is indistinguishable from a hang
            // unless we narrate it - so report the total up front and tick per bundle.
            var totalBytes = 0L;
            foreach (var entry in missing)
                totalBytes += entry.Size;

            log($"Bundles: {manifest.Count} in manifest, {missing.Count} missing ({Mb(totalBytes)}). Fetching - the launcher normally does this, and a headless has no launcher.");
            log("Bundles: the game will not start until this finishes. First run only; they are cached afterwards.");

            var started = DateTime.UtcNow;
            var got = 0;
            var doneBytes = 0L;

            for (var i = 0; i < missing.Count; i++)
            {
                var entry = missing[i];
                try
                {
                    Download(backendUrl, cacheRoot, entry);
                    got++;
                    doneBytes += entry.Size;

                    // Name the file rather than only a counter: when one is slow or stalls, the
                    // admin needs to know WHICH, and bundle sizes vary by an order of magnitude.
                    log($"Bundles: [{i + 1}/{missing.Count}] {ShortName(entry.FileName)} ({Mb(entry.Size)}) - {Mb(doneBytes)}/{Mb(totalBytes)} done");
                }
                catch (Exception e)
                {
                    // One bad bundle costs that mod's assets, not the boot.
                    warn($"Bundles: [{i + 1}/{missing.Count}] FAILED {entry.FileName} - {e.Message}");
                }
            }

            var elapsed = DateTime.UtcNow - started;
            log($"Bundles: {got}/{missing.Count} fetched ({Mb(doneBytes)}) in {elapsed.TotalSeconds:F1}s.");
        }
        catch (Exception e)
        {
            warn($"Bundles: skipped due to an unexpected error - {e}");
        }
    }

    /// <summary>Reads BackendUrl from HeadlessConfig.json. Null means "not a headless".</summary>
    private static string ReadBackendUrl(string gameDir)
    {
        var path = Path.Combine(gameDir, HeadlessConfigName);
        if (!File.Exists(path))
            return null;

        var cfg = JsonConvert.DeserializeObject<HeadlessConfig>(File.ReadAllText(path));
        var url = cfg?.BackendUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(url) ? null : url;
    }

    /// <summary>
    /// GET /singleplayer/bundles. SPT zlib-compresses this response (it starts 0x78 0xDA) with no
    /// Content-Encoding header, so nothing decompresses it for us - we inflate by hand.
    ///
    /// Note the bundle FILES are NOT compressed; only this JSON endpoint is. That was established
    /// by measurement, not assumption: fetching all 19 bundles raw produced zero CRC mismatches.
    /// </summary>
    private static List<ManifestEntry> FetchManifest(string backendUrl)
    {
        var raw = Http.GetByteArrayAsync($"{backendUrl}/singleplayer/bundles").GetAwaiter().GetResult();

        var json = LooksLikeZlib(raw) ? Inflate(raw) : Encoding.UTF8.GetString(raw);
        return JsonConvert.DeserializeObject<List<ManifestEntry>>(json);
    }

    /// <summary>
    /// Our own client rather than Server.cs's: this runs in Awake(), before the sync machinery is
    /// used, and it must bypass SPT's self-signed certificate exactly the same way.
    /// Mono only has a working TLS provider from chainloader time onward - a WebClient attempt at
    /// preloader time failed outright with "TLS Support not available", which is why this lives
    /// here and not in the patcher.
    /// </summary>
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>zlib header: 0x78 then one of the standard compression-level bytes.</summary>
    private static bool LooksLikeZlib(byte[] data) =>
        data.Length > 2 && data[0] == 0x78 && (data[1] == 0x01 || data[1] == 0x5E || data[1] == 0x9C || data[1] == 0xDA);

    /// <summary>
    /// Inflate a zlib stream. DeflateStream handles raw DEFLATE only, so skip the 2-byte zlib
    /// header; the 4-byte Adler-32 trailer is simply never read.
    /// </summary>
    private static string Inflate(byte[] data)
    {
        using var input = new MemoryStream(data, 2, data.Length - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    /// <summary>Bundle keys are long paths; the leaf is what identifies it to a human.</summary>
    private static string ShortName(string fileName)
    {
        var i = fileName.LastIndexOf('/');
        return i >= 0 && i < fileName.Length - 1 ? fileName.Substring(i + 1) : fileName;
    }

    private static string CachePathFor(string cacheRoot, ManifestEntry entry) =>
        Path.Combine(cacheRoot, entry.Crc.ToString("X8"), entry.FileName.Replace('/', '\\'));

    /// <summary>
    /// Cached means present AND correct. The path is CRC-keyed, so a mismatch indicates a
    /// truncated or corrupt earlier download rather than a stale version - refetch it.
    /// Size is checked first because it is free and rules out the common truncation case.
    /// </summary>
    private static bool IsCached(string cacheRoot, ManifestEntry entry)
    {
        var path = LongPath.Extended(CachePathFor(cacheRoot, entry));
        if (!File.Exists(path))
            return false;

        try
        {
            if (entry.Size > 0 && new FileInfo(path).Length != entry.Size)
                return false;

            return ModSyncCrc32.ComputeFile(path) == entry.Crc;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fetch to a temp file, verify, then move into place.
    ///
    /// Verification is NOT optional: SPT's /files/bundle/{name} answers 200 even for a name it
    /// does not have, so a status check alone would persist an error body as a .bundle. Writing
    /// to .part first means a failed verify never leaves a plausible-looking file at the real
    /// path, where IsCached would trust it on the next boot.
    /// </summary>
    private static void Download(string backendUrl, string cacheRoot, ManifestEntry entry)
    {
        var finalPath = CachePathFor(cacheRoot, entry);
        var tempPath = finalPath + ".part";

        Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(finalPath)));

        // Escape each segment but keep '/' as a separator, so spaces and other characters in
        // bundle names survive the URL intact.
        var encoded = string.Join("/", Array.ConvertAll(entry.FileName.Replace('\\', '/').Split('/'), Uri.EscapeDataString));

        using (var response = Http.GetAsync($"{backendUrl}/files/bundle/{encoded}", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = new FileStream(LongPath.Extended(tempPath), FileMode.Create);
            src.CopyTo(dst);
        }

        var actual = ModSyncCrc32.ComputeFile(LongPath.Extended(tempPath));
        if (actual != entry.Crc)
        {
            TryDelete(tempPath);
            throw new InvalidDataException($"CRC mismatch (expected {entry.Crc:X8}, got {actual:X8})");
        }

        TryDelete(finalPath); // File.Move throws on an existing destination in net472
        File.Move(LongPath.Extended(tempPath), LongPath.Extended(finalPath));
    }

    private static void TryDelete(string path)
    {
        try
        {
            var extended = LongPath.Extended(path);
            if (File.Exists(extended))
                File.Delete(extended);
        }
        catch
        {
            // Best effort. A leftover .part is inert - IsCached only looks at the real name.
        }
    }
}
