using System;
using System.IO;

namespace Molehill.Cli;

/// <summary>
/// Writes uncompressed 24-bit BMP files. Deliberately hand-rolled and dependency-free:
/// looking at terrain should never be a reason to add a package, and BMP is the one
/// format with no compression step to get wrong.
/// </summary>
internal static class BmpWriter
{
    private const int FileHeaderBytes = 14;
    private const int InfoHeaderBytes = 40;

    /// <summary>
    /// Writes a top-down pixel buffer. <paramref name="pixels"/> holds three bytes per
    /// pixel in red, green, blue order, row 0 being the top of the image.
    /// </summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length < width * height * 3)
        {
            throw new ArgumentException("Pixel buffer is too small for the image.", nameof(pixels));
        }

        // BMP rows are padded out to a four-byte boundary.
        int rowPadding = (4 - ((width * 3) % 4)) % 4;
        int rowBytes = (width * 3) + rowPadding;
        int pixelBytes = rowBytes * height;

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(FileHeaderBytes + InfoHeaderBytes + pixelBytes);
        writer.Write(0);
        writer.Write(FileHeaderBytes + InfoHeaderBytes);

        writer.Write(InfoHeaderBytes);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        Span<byte> padding = stackalloc byte[3];
        padding.Clear();

        // BMP stores rows bottom-up, and each pixel as blue, green, red.
        for (int y = height - 1; y >= 0; y--)
        {
            int rowStart = y * width * 3;

            for (int x = 0; x < width; x++)
            {
                int offset = rowStart + (x * 3);
                writer.Write(pixels[offset + 2]);
                writer.Write(pixels[offset + 1]);
                writer.Write(pixels[offset]);
            }

            if (rowPadding > 0)
            {
                writer.Write(padding.Slice(0, rowPadding));
            }
        }
    }
}
