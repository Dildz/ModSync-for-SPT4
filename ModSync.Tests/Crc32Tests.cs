using System.Text;
using ModSync.Utility;

namespace ModSync.Test;

/// <summary>
/// Checks our CRC-32 against published vectors and against a real round-trip.
///
/// This matters more than a normal hash test: the value has to match what the SPT SERVER
/// computed for a bundle and published in the `/singleplayer/bundles` manifest. If our
/// implementation disagrees by so much as the final XOR, HeadlessBundles rejects every
/// bundle it downloads and a headless client silently loses all modded assets.
///
/// SPT used to expose `SPT.Custom.Utils.Crc32` and we could simply have called it, but
/// **SPT 4.1.3 deleted that class**, so we carry our own - hence the need to prove it.
/// </summary>
[TestFixture]
public class Crc32Tests
{
    private static uint OfString(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        return Crc32.Compute(bytes, 0, bytes.Length) ^ 0xFFFFFFFFu;
    }

    // The canonical CRC-32/ISO-HDLC check value. Any implementation that gets "123456789"
    // right has the polynomial, the reflection, the init value and the final XOR all correct.
    [Test]
    public void CheckVector_Matches()
    {
        Assert.That(OfString("123456789"), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void EmptyInput_IsZero()
    {
        Assert.That(OfString(string.Empty), Is.EqualTo(0u));
    }

    [Test]
    public void KnownStrings_Match()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OfString("a"), Is.EqualTo(0xE8B7BE43u));
            Assert.That(OfString("abc"), Is.EqualTo(0x352441C2u));
        });
    }

    /// <summary>
    /// Compute() is fed one chunk at a time by ComputeFile (81920 bytes at a time), so a
    /// seed that does not carry correctly between calls would only show up on files larger
    /// than the buffer. Prove the chaining explicitly rather than trusting a small fixture.
    /// </summary>
    [Test]
    public void ChunkedInput_MatchesSinglePass()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");

        var single = Crc32.Compute(bytes, 0, bytes.Length) ^ 0xFFFFFFFFu;

        var crc = 0xFFFFFFFFu;
        crc = Crc32.Compute(bytes, 0, 4, crc);
        crc = Crc32.Compute(bytes, 4, bytes.Length - 4, crc);
        var chunked = crc ^ 0xFFFFFFFFu;

        Assert.That(chunked, Is.EqualTo(single));
        Assert.That(chunked, Is.EqualTo(0xCBF43926u));
    }

    /// <summary>ComputeFile streams from disk; it must agree with the in-memory path.</summary>
    [Test]
    public void ComputeFile_MatchesInMemory()
    {
        // Larger than ComputeFile's 81920-byte buffer, so this also exercises multi-chunk reads.
        var payload = new byte[200_000];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 31 % 251);

        var expected = Crc32.Compute(payload, 0, payload.Length) ^ 0xFFFFFFFFu;

        var path = Path.Combine(Path.GetTempPath(), $"modsync-crc32-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, payload);
            Assert.That(Crc32.ComputeFile(path), Is.EqualTo(expected));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
