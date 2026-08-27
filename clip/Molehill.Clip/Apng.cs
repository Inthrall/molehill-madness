using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Molehill.Clip
{
    /// <summary>
    /// Writes an animated PNG, which is the fallback the design pre-designed.
    /// </summary>
    /// <remarks>
    /// The plan's flagged unknown is whether a hardware encoder can turn a re-simulated round into a
    /// shareable video in five seconds on a mid-range phone, and it says the fallback ships "without
    /// shame" if it cannot. This is that fallback, and it is worth having even if the encoders work:
    /// it needs no platform plugin, no bundled binary and no licence, so it is the one path that is
    /// certain to exist on every target.
    ///
    /// Hand-written rather than taken from a library for the same reasons the plan codec is. APNG is
    /// PNG with three extra chunk types, the format fits on a page, and a dependency here would be a
    /// dependency in the client on every platform. It also means the output is something a person can
    /// reason about when a share sheet rejects it.
    ///
    /// The format, in the order it comes out: signature, IHDR, acTL, then per frame an fcTL followed
    /// by IDAT for the first and fdAT for the rest, then IEND. The first frame being an ordinary IDAT
    /// is what makes an APNG a valid PNG to anything that has never heard of the animation chunks,
    /// which is most of the software a clip will pass through.
    ///
    /// No inter-frame compression, no palette, no delta frames. A clip is a few seconds of a cartoon
    /// with large flat areas, zlib handles that well, and the alternative is an optimiser with bugs in
    /// it. If the files come out too big the answer is fewer frames or a smaller frame, both of which
    /// are one number.
    /// </remarks>
    public static class Apng
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Bytes per pixel. RGBA, because the client hands over what Godot gives it.</summary>
        private const int Channels = 4;

        /// <summary>
        /// Turns a run of frames into one animated PNG.
        /// </summary>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="frames">
        /// RGBA rows, top to bottom, one array per frame, each exactly width * height * 4 bytes.
        /// </param>
        /// <param name="fps">
        /// How fast to play it back. Written as a fraction, since APNG stores a delay as one and
        /// thirty frames a second is a third that does not fit in anything else.
        /// </param>
        /// <param name="loops">How many times to play. Zero means forever, which is what a clip wants.</param>
        public static byte[] Write(
            int width, int height, IReadOnlyList<byte[]> frames, int fps = 30, int loops = 0)
        {
            ArgumentNullException.ThrowIfNull(frames);

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "A frame has to have a size.");
            }

            if (frames.Count == 0)
            {
                throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
            }

            int expected = width * height * Channels;

            for (int frame = 0; frame < frames.Count; frame++)
            {
                if (frames[frame] is null || frames[frame].Length != expected)
                {
                    throw new ArgumentException(
                        $"Frame {frame} is {frames[frame]?.Length ?? 0} bytes; {expected} expected.",
                        nameof(frames));
                }
            }

            using MemoryStream file = new MemoryStream();
            file.Write(Signature, 0, Signature.Length);

            Header(file, width, height);
            Animation(file, frames.Count, loops);

            // The sequence number runs across fcTL and fdAT chunks together, which is the part of the
            // spec that is easy to get wrong: it is one counter shared by two chunk types, not one
            // each.
            int sequence = 0;

            for (int frame = 0; frame < frames.Count; frame++)
            {
                Control(file, ref sequence, width, height, fps);

                byte[] squeezed = Squeeze(width, height, frames[frame]);

                if (frame == 0)
                {
                    Chunk(file, "IDAT", squeezed);
                    continue;
                }

                // An fdAT is an IDAT with a sequence number glued to the front of it.
                byte[] numbered = new byte[squeezed.Length + 4];
                WriteInt(numbered, 0, sequence++);
                Buffer.BlockCopy(squeezed, 0, numbered, 4, squeezed.Length);

                Chunk(file, "fdAT", numbered);
            }

            Chunk(file, "IEND", Array.Empty<byte>());

            return file.ToArray();
        }

        private static void Header(Stream file, int width, int height)
        {
            byte[] data = new byte[13];
            WriteInt(data, 0, width);
            WriteInt(data, 4, height);
            data[8] = 8;    // Eight bits a channel.
            data[9] = 6;    // Truecolour with alpha.
            data[10] = 0;   // Deflate, which is the only compression PNG has.
            data[11] = 0;   // No filtering beyond the per-scanline byte.
            data[12] = 0;   // Not interlaced.

            Chunk(file, "IHDR", data);
        }

        /// <summary>The acTL chunk: how many frames and how many times round.</summary>
        private static void Animation(Stream file, int frames, int loops)
        {
            byte[] data = new byte[8];
            WriteInt(data, 0, frames);
            WriteInt(data, 4, loops);

            Chunk(file, "acTL", data);
        }

        /// <summary>
        /// The fcTL chunk: where a frame goes, how long it stays, and what happens next.
        /// </summary>
        /// <remarks>
        /// Every frame is the full size at the origin, because there is no delta encoding here. The
        /// dispose and blend operations are set to the pair that means "replace what was there": with
        /// full opaque frames anything else would be a way to accumulate a bug.
        /// </remarks>
        private static void Control(Stream file, ref int sequence, int width, int height, int fps)
        {
            byte[] data = new byte[26];
            WriteInt(data, 0, sequence++);
            WriteInt(data, 4, width);
            WriteInt(data, 8, height);
            WriteInt(data, 12, 0);
            WriteInt(data, 16, 0);

            // A delay is a fraction of a second, so thirty frames a second is exactly 1/30 rather
            // than the 0.033 that a decimal would round it to.
            WriteShort(data, 20, 1);
            WriteShort(data, 22, (ushort)Math.Clamp(fps, 1, ushort.MaxValue));

            data[24] = 0;   // Dispose: leave it, since the next frame covers it entirely.
            data[25] = 0;   // Blend: replace rather than composite over.

            Chunk(file, "fcTL", data);
        }

        /// <summary>
        /// Deflates one frame's scanlines, each with its filter byte.
        /// </summary>
        /// <remarks>
        /// Filter zero on every row: none. A real encoder would try the five filters per scanline and
        /// keep the best, which is where most of PNG's compression comes from, and would be worth
        /// doing if these files turn out too big. Starting with the simple one means a wrong file is a
        /// wrong file rather than a wrong filter choice.
        /// </remarks>
        private static byte[] Squeeze(int width, int height, byte[] rgba)
        {
            int stride = width * Channels;

            using MemoryStream squeezed = new MemoryStream();

            // ZLibStream writes the zlib wrapper PNG wants, which is what a raw DeflateStream leaves
            // out and is the classic way to produce a file every decoder rejects.
            using (ZLibStream deflate = new ZLibStream(
                squeezed, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] row = new byte[stride + 1];

                for (int line = 0; line < height; line++)
                {
                    row[0] = 0;
                    Buffer.BlockCopy(rgba, line * stride, row, 1, stride);
                    deflate.Write(row, 0, row.Length);
                }
            }

            return squeezed.ToArray();
        }

        private static void Chunk(Stream file, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteInt(length, 0, data.Length);
            file.Write(length, 0, 4);

            byte[] tagged = new byte[4 + data.Length];

            for (int at = 0; at < 4; at++)
            {
                tagged[at] = (byte)type[at];
            }

            Buffer.BlockCopy(data, 0, tagged, 4, data.Length);
            file.Write(tagged, 0, tagged.Length);

            byte[] crc = new byte[4];

            // The checksum covers the type and the data but not the length, which is the other part
            // of the format that is easy to get wrong and produces a file that opens nowhere.
            WriteInt(crc, 0, unchecked((int)Crc.Of(tagged)));
            file.Write(crc, 0, 4);
        }

        /// <summary>Big-endian, because every number in a PNG is.</summary>
        private static void WriteInt(byte[] into, int at, int value)
        {
            into[at] = (byte)((value >> 24) & 0xFF);
            into[at + 1] = (byte)((value >> 16) & 0xFF);
            into[at + 2] = (byte)((value >> 8) & 0xFF);
            into[at + 3] = (byte)(value & 0xFF);
        }

        private static void WriteShort(byte[] into, int at, ushort value)
        {
            into[at] = (byte)((value >> 8) & 0xFF);
            into[at + 1] = (byte)(value & 0xFF);
        }
    }

    /// <summary>The CRC-32 every PNG chunk ends with.</summary>
    /// <remarks>
    /// Hand-rolled because .NET's is in System.IO.Hashing, which is a package, and this is nine lines.
    /// </remarks>
    internal static class Crc
    {
        private static readonly uint[] Table = Build();

        public static uint Of(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte one in bytes)
            {
                crc = Table[(crc ^ one) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static uint[] Build()
        {
            uint[] table = new uint[256];

            for (uint entry = 0; entry < 256; entry++)
            {
                uint value = entry;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[entry] = value;
            }

            return table;
        }
    }
}
