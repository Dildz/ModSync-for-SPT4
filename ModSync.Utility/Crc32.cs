using System.IO;

namespace ModSync.Utility;

/// <summary>
/// Standard CRC-32 (IEEE 802.3: polynomial 0xEDB88320, init 0xFFFFFFFF, final XOR 0xFFFFFFFF).
///
/// SPT computes bundle CRCs with this algorithm, and the server publishes the result in the
/// `/singleplayer/bundles` manifest as `BundleItem.Crc`. We need it to verify bundles we
/// download for a headless client (see HeadlessBundles).
///
/// SPT used to expose `SPT.Custom.Utils.Crc32` for exactly this, but **4.1.3 removed that
/// class** along with the rest of the client-side bundle validation path, so we carry our own.
/// It is ~20 lines and has no dependencies; taking one on a game assembly that upstream is
/// actively deleting from would be the worse trade.
///
/// ModSync's own file sync uses ImoHash, not CRC32 - the two are unrelated. This exists purely
/// to match the value the SPT server reports for a bundle.
/// </summary>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
                entry = (entry & 1) == 1 ? (entry >> 1) ^ Polynomial : entry >> 1;
            table[i] = entry;
        }

        return table;
    }

    /// <summary>CRC-32 of a byte range.</summary>
    public static uint Compute(byte[] buffer, int offset, int count, uint seed = 0xFFFFFFFFu)
    {
        var crc = seed;
        for (var i = offset; i < offset + count; i++)
            crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xFF];

        return crc;
    }

    /// <summary>
    /// CRC-32 of a file, streamed. Bundles run to tens of megabytes, so this deliberately
    /// avoids reading the whole file into memory the way a naive ReadAllBytes would.
    /// The caller is responsible for passing an extended-length path where needed.
    /// </summary>
    public static uint ComputeFile(string path)
    {
        var crc = 0xFFFFFFFFu;
        var buffer = new byte[81920];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, false);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            crc = Compute(buffer, 0, read, crc);

        return crc ^ 0xFFFFFFFFu;
    }
}
