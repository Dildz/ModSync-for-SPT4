using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private const string RuntimeRelative = "SPT_Runtime";
    private const string CacheRelative = @"SPT_Runtime\user\cache\bundles";

    /// <summary>
    /// How many bundles to fetch at once.
    ///
    /// Measured on a LAN headless: one stream sustained 1.2 MB/s while plain curl over the same
    /// hop managed 200 MB/s, so the ceiling is per-stream CPU inside Wine/Mono (TLS, then the
    /// CRC pass), not the wire. Independent streams therefore scale across idle cores.
    ///
    /// Kept deliberately low. Plugin.cs cut ModSync's own file-sync limiter from 8 to 2 after
    /// Mono's TLS stack proved fragile under heavy concurrency on slow uplinks - handshakes
    /// timing out into retry storms that never recovered. Unlike that path this one has no retry,
    /// so a storm here costs a mod's assets outright. 4 is a compromise: a headless is normally
    /// close to its server, but it is not always.
    /// </summary>
    private const int Concurrency = 4;

    /// <summary>
    /// Read/write chunk for bundle downloads. Stream.CopyTo defaults to 80 KB, which is ~85,000
    /// file calls across a 6.5 GB first run. Wine translates every Win32 file call, so syscall
    /// count costs more here than it would on native .NET; 1 MB cuts it to ~6,500.
    /// At Concurrency 4 this is 4 MB of buffers total.
    /// </summary>
    private const int CopyBufferSize = 1024 * 1024;

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
        [JsonProperty("ModPath")] public string ModPath { get; set; }
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
                if (!IsCached(gameDir, cacheRoot, entry))
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
            var finished = 0;

            // .NET Framework caps outbound connections PER ENDPOINT at 2 by default, so without
            // this the parallel loop below quietly serialises into pairs.
            //
            // Raising DefaultConnectionLimit alone is NOT enough, and failing to notice that cost
            // a whole test run: the default is only read when a ServicePoint is first CREATED,
            // and FetchManifest above has already created the one for this host. Setting it
            // afterwards leaves that instance pinned at 2 - measured as never more than 2 bundles
            // in flight, with container CPU flat. FindServicePoint returns that same live
            // instance, so set the limit on it directly and stay order-independent.
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, Concurrency);
            ServicePointManager.FindServicePoint(new Uri(backendUrl)).ConnectionLimit = Concurrency;

            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Concurrency }, entry =>
            {
                try
                {
                    Download(backendUrl, cacheRoot, entry);

                    Interlocked.Increment(ref got);
                    var bytes = Interlocked.Add(ref doneBytes, entry.Size);
                    var n = Interlocked.Increment(ref finished);

                    // Name the file rather than only a counter: when one is slow or stalls, the
                    // admin needs to know WHICH, and bundle sizes vary by an order of magnitude.
                    // With Concurrency > 1 these complete out of order, so [n] counts finished
                    // bundles rather than a position in the list.
                    log($"Bundles: [{n}/{missing.Count}] {ShortName(entry.FileName)} ({Mb(entry.Size)}) - {Mb(bytes)}/{Mb(totalBytes)} done");
                }
                catch (Exception e)
                {
                    // One bad bundle costs that mod's assets, not the boot.
                    var n = Interlocked.Increment(ref finished);
                    warn($"Bundles: [{n}/{missing.Count}] FAILED {entry.FileName} - {e.Message}");
                }
            });

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
    /// Cached means present, at the CRC-keyed path, and the size the manifest expects.
    ///
    /// This deliberately does NOT re-CRC the file. It used to, and that cost a full read of the
    /// entire cache on EVERY boot - roughly 7 GB through Mono once the cache is populated, paid
    /// again on each of the restarts ModSync itself triggers. The check bought very little for
    /// that price: a file only reaches this path via Download, which CRC-verifies it while
    /// writing and moves it into place from .part only on success, so anything here was correct
    /// when written. Re-hashing guards solely against later disk corruption, and SPT's own
    /// launcher populates this same cache and never re-verifies it either - so trusting it
    /// matches platform behaviour rather than weakening it.
    ///
    /// Size still catches the realistic failure (a truncated file from a killed process). Where
    /// the manifest omits a size we have nothing cheap to check, so fall back to the CRC.
    /// </summary>
    private static bool IsCached(string gameDir, string cacheRoot, ManifestEntry entry)
    {
        // Mirror SPT's own lookup order: CRC cache first, then the mod folder.
        if (Usable(CachePathFor(cacheRoot, entry), entry))
            return true;

        // A headless that shares a filesystem with its server already has every bundle on disk.
        // Bind-mounting the server's user/mods into the headless is the obvious way to arrange
        // that, and SPT 4.1.3 still falls back to SPT_Runtime/<ModPath>/bundles/<FileName> when
        // the cache misses - the "found in neither the bundle cache nor a mod folder" error
        // names both paths. Downloading a second copy of a file the game will happily read in
        // place is pure waste, so treat a mod-folder hit as cached and fetch nothing.
        //
        // Remote headless setups have no such folder, fall through, and download as before.
        return !string.IsNullOrEmpty(entry.ModPath) && Usable(ModFolderPathFor(gameDir, entry), entry);
    }

    /// <summary>
    /// Present, and the size the manifest expects.
    ///
    /// Deliberately does NOT re-CRC. That used to cost a full read of the entire cache on EVERY
    /// boot - roughly 7 GB through Mono once populated, paid again on each restart ModSync itself
    /// triggers - and bought very little: a file only reaches the cache path via Download, which
    /// CRC-verifies while writing and moves from .part only on success. SPT's own launcher fills
    /// that cache and never re-verifies it either, and nothing verifies the mod folder at all, so
    /// trusting both matches platform behaviour rather than weakening it.
    ///
    /// Size still catches the realistic failure, a truncated file from a killed process. Where
    /// the manifest omits a size there is nothing cheap to check, so fall back to the CRC.
    /// </summary>
    private static bool Usable(string path, ManifestEntry entry)
    {
        var full = LongPath.Extended(path);
        if (!File.Exists(full))
            return false;

        try
        {
            if (entry.Size > 0)
                return new FileInfo(full).Length == entry.Size;

            return ModSyncCrc32.ComputeFile(full) == entry.Crc;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>SPT's fallback location: SPT_Runtime/&lt;ModPath&gt;/bundles/&lt;FileName&gt;.</summary>
    private static string ModFolderPathFor(string gameDir, ManifestEntry entry) =>
        Path.Combine(gameDir, RuntimeRelative, entry.ModPath.Replace('/', '\\'), "bundles", entry.FileName.Replace('/', '\\'));

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

        var crc = 0xFFFFFFFFu;
        using (var response = Http.GetAsync($"{backendUrl}/files/bundle/{encoded}", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = new FileStream(LongPath.Extended(tempPath), FileMode.Create);

            // Hash on the way past instead of re-reading the finished file. ComputeFile meant a
            // second full read of every bundle - a whole extra ~6.5 GB of I/O across a first run -
            // for a value we already have the bytes for.
            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, read);
                crc = ModSyncCrc32.Compute(buffer, 0, read, crc);
            }
        }

        var actual = crc ^ 0xFFFFFFFFu;
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
