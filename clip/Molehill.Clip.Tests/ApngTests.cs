using System.IO.Compression;
using Molehill.Clip;

namespace Molehill.Clip.Tests;

/// <summary>
/// The animated PNG writer.
/// </summary>
/// <remarks>
/// A file format is the one kind of code where "it looks right to me" is worth nothing, because the
/// thing that has to accept it is somebody else's decoder. So the structural checks here are backed by
/// a real one: the pixels are pulled back out through an independent inflate and compared, which fails
/// if the scanline filter bytes, the stride, the zlib wrapper or the chunk boundaries are wrong.
///
/// The client does the other half of the verification, loading a written file back through Godot's own
/// PNG decoder, because that is a decoder nobody here wrote.
/// </remarks>
[TestFixture]
public sealed class ApngTests
{
    private const int Wide = 6;
    private const int Tall = 4;

    /// <summary>A frame of a recognisable pattern, so a stride error shows up as a wrong pixel.</summary>
    private static byte[] Frame(byte tint)
    {
        byte[] rgba = new byte[Wide * Tall * 4];

        for (int y = 0; y < Tall; y++)
        {
            for (int x = 0; x < Wide; x++)
            {
                int at = ((y * Wide) + x) * 4;
                rgba[at] = (byte)(x * 40);
                rgba[at + 1] = (byte)(y * 60);
                rgba[at + 2] = tint;
                rgba[at + 3] = 255;
            }
        }

        return rgba;
    }

    // ---- The container ------------------------------------------------------------------

    [Test]
    public void ItStartsWithThePngSignature()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(10) });

        Assert.That(
            file[..8],
            Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    /// <summary>
    /// The chunk order the spec requires, and the reason the first frame is an IDAT rather than an
    /// fdAT: that is what makes an APNG a valid still PNG to anything that has never heard of the
    /// animation chunks, which is most of the software a clip will pass through.
    /// </summary>
    [Test]
    public void TheChunksComeInTheRightOrder()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(10), Frame(20), Frame(30) });

        Assert.That(
            Chunks(file).Select(chunk => chunk.Type),
            Is.EqualTo(new[]
            {
                "IHDR", "acTL",
                "fcTL", "IDAT",
                "fcTL", "fdAT",
                "fcTL", "fdAT",
                "IEND",
            }));
    }

    [Test]
    public void EveryChunkChecksumIsRight()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(10), Frame(20) });

        // Chunks() recomputes each CRC from the bytes on disk and reports whether it matched, so an
        // off-by-one in what the checksum covers fails here.
        Assert.That(Chunks(file).All(chunk => chunk.Sound), Is.True);
    }

    [Test]
    public void TheHeaderSaysWhatWasAskedFor()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(10) });
        byte[] header = Chunks(file).Single(chunk => chunk.Type == "IHDR").Data;

        Assert.That(BigInt(header, 0), Is.EqualTo(Wide));
        Assert.That(BigInt(header, 4), Is.EqualTo(Tall));
        Assert.That(header[8], Is.EqualTo(8), "Eight bits a channel.");
        Assert.That(header[9], Is.EqualTo(6), "Truecolour with alpha.");
    }

    [Test]
    public void TheAnimationChunkCountsTheFrames()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(1), Frame(2), Frame(3), Frame(4) });
        byte[] animation = Chunks(file).Single(chunk => chunk.Type == "acTL").Data;

        Assert.That(BigInt(animation, 0), Is.EqualTo(4));
        Assert.That(BigInt(animation, 4), Is.EqualTo(0), "Zero plays means loop forever.");
    }

    /// <summary>
    /// The sequence number is one counter shared by fcTL and fdAT rather than one each, which is the
    /// part of the spec most likely to be got wrong and produces a file that plays only its first
    /// frame.
    /// </summary>
    [Test]
    public void TheSequenceNumbersRunUnbrokenAcrossBothChunkTypes()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(1), Frame(2), Frame(3) });

        List<int> sequence = new List<int>();

        foreach (Chunk chunk in Chunks(file))
        {
            if (chunk.Type is "fcTL" or "fdAT")
            {
                sequence.Add(BigInt(chunk.Data, 0));
            }
        }

        Assert.That(sequence, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
    }

    /// <summary>
    /// A delay is stored as a fraction, so thirty frames a second is exactly one thirtieth rather
    /// than the 0.033 a decimal would round it to.
    /// </summary>
    [Test]
    public void TheFrameDelayIsAnExactFraction()
    {
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(1) }, fps: 30);
        byte[] control = Chunks(file).First(chunk => chunk.Type == "fcTL").Data;

        Assert.That(BigShort(control, 20), Is.EqualTo(1), "Numerator.");
        Assert.That(BigShort(control, 22), Is.EqualTo(30), "Denominator.");
    }

    // ---- The pixels ---------------------------------------------------------------------

    /// <summary>
    /// The check that is worth more than all the structural ones: the pixels come back.
    /// </summary>
    /// <remarks>
    /// Inflated with .NET's own zlib rather than anything from this project, and compared byte for
    /// byte against what went in. A wrong stride, a missing scanline filter byte, a raw deflate stream
    /// without the zlib wrapper, or a chunk boundary in the wrong place all fail here, and every one
    /// of them would have produced a file that looked structurally fine.
    /// </remarks>
    [Test]
    public void TheFirstFramesPixelsSurviveTheRoundTrip()
    {
        byte[] original = Frame(77);
        byte[] file = Apng.Write(Wide, Tall, new[] { original });

        byte[] back = Inflate(Chunks(file).Single(chunk => chunk.Type == "IDAT").Data);

        Assert.That(back, Has.Length.EqualTo(Tall * ((Wide * 4) + 1)), "One filter byte a row.");
        Assert.That(Unfilter(back), Is.EqualTo(original));
    }

    [Test]
    public void ALaterFramesPixelsSurviveToo()
    {
        byte[] second = Frame(200);
        byte[] file = Apng.Write(Wide, Tall, new[] { Frame(10), second });

        // An fdAT is an IDAT with a four-byte sequence number in front of it.
        byte[] data = Chunks(file).Single(chunk => chunk.Type == "fdAT").Data;

        Assert.That(Unfilter(Inflate(data[4..])), Is.EqualTo(second));
    }

    // ---- Refusals -----------------------------------------------------------------------

    [Test]
    public void AnAnimationNeedsAtLeastOneFrame()
    {
        Assert.That(
            () => Apng.Write(Wide, Tall, Array.Empty<byte[]>()),
            Throws.ArgumentException);
    }

    /// <summary>
    /// A frame of the wrong size is refused rather than written, because a file with one short
    /// scanline in it is a file that opens and looks corrupt, which is far harder to diagnose than a
    /// failure at the point the mistake was made.
    /// </summary>
    [Test]
    public void AFrameOfTheWrongSizeIsRefused()
    {
        Assert.That(
            () => Apng.Write(Wide, Tall, new[] { Frame(1), new byte[7] }),
            Throws.ArgumentException);
    }

    [Test]
    public void AFrameWithNoSizeIsRefused()
    {
        Assert.That(
            () => Apng.Write(0, Tall, new[] { Array.Empty<byte>() }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    // ---- Reading a PNG back -------------------------------------------------------------

    private readonly record struct Chunk(string Type, byte[] Data, bool Sound);

    /// <summary>Walks the file, checking each chunk's length and checksum as it goes.</summary>
    private static List<Chunk> Chunks(byte[] file)
    {
        List<Chunk> found = new List<Chunk>();
        int at = 8;

        while (at + 12 <= file.Length)
        {
            int length = BigInt(file, at);
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            byte[] data = file[(at + 8)..(at + 8 + length)];

            uint stated = (uint)BigInt(file, at + 8 + length);
            uint actual = Checksum(file[(at + 4)..(at + 8 + length)]);

            found.Add(new Chunk(type, data, stated == actual));

            at += 12 + length;
        }

        return found;
    }

    private static byte[] Inflate(byte[] squeezed)
    {
        using MemoryStream source = new MemoryStream(squeezed);
        using ZLibStream inflate = new ZLibStream(source, CompressionMode.Decompress);
        using MemoryStream loose = new MemoryStream();

        inflate.CopyTo(loose);

        return loose.ToArray();
    }

    /// <summary>Strips the per-scanline filter byte, which is zero on every row here.</summary>
    private static byte[] Unfilter(byte[] filtered)
    {
        int stride = Wide * 4;
        byte[] rgba = new byte[Wide * Tall * 4];

        for (int line = 0; line < Tall; line++)
        {
            int from = line * (stride + 1);

            Assert.That(filtered[from], Is.EqualTo(0), $"Row {line} is filtered.");
            Buffer.BlockCopy(filtered, from + 1, rgba, line * stride, stride);
        }

        return rgba;
    }

    private static int BigInt(byte[] bytes, int at) =>
        (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];

    private static int BigShort(byte[] bytes, int at) => (bytes[at] << 8) | bytes[at + 1];

    /// <summary>CRC-32, written out again here so the test is not checking code against itself.</summary>
    private static uint Checksum(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte one in bytes)
        {
            crc ^= one;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
