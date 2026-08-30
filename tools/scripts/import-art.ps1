<#
.SYNOPSIS
    Turns the generated art sheets into the textures, sprites and glyph strips the client loads.

.DESCRIPTION
    One command for all of the art, and one manifest describing every sheet in it. Five jobs.

    The generator's watermark goes from the tiling ground textures. On a texture that tiles across
    a sixty metre map that sparkle would appear in a grid, which reads as a rendering fault rather
    than as dirt. The patch is a copy of the same rows from further along the texture, feathered
    in, with the offset chosen by trying a spread of them and keeping whichever both joins up at
    the border and brings the least with it. The same stars elsewhere on the sprite sheets are left
    alone: a mole is thirty screen pixels wide, which makes its sparkle two, and chasing them into
    every cell would cost more than it buys.

    The key colour goes, becoming transparency. Green on most sheets, magenta on the decor, white
    on the lava strips, and both green and magenta on the energy ring, which has one inside the
    other. Keyed at load time instead, every antialiased edge would keep a fringe of the key
    colour, so edge pixels get their spill removed and a partial alpha rather than being thrown
    away.

    The sheets are cut into their cells. Every sheet is a uniform grid, which the gutter analysis
    in the survey confirmed one at a time, and several carry the artist's own labels under each
    cell, so a cell can be cropped before it is trimmed. Thin things are dropped: a cell border, a
    ground line under a mole's feet, a stray speck. That is a morphological opening on the alpha
    rather than a search for shapes, because it removes anything thinner than a claw and keeps
    everything thicker without needing to know what any of it is.

    An animation comes out as one strip with every frame the same size, and that matters more than
    the file count. Trimmed frame by frame, each frame's own bounding box becomes its origin and
    the mole jitters as the animation plays. Cut instead with one rectangle for the whole set, the
    union of what every frame occupies, and the motion the artist drew is the only motion there is.

    A mole comes out four times, once per platoon. Four platoons are told apart entirely by colour
    in this game, so one brown mole makes all four identical, and the trunks are the only part of
    the artwork that can carry a team colour without repainting the animal. Folds and highlights
    are kept: the pixel's own lightness is measured against the garment's average and reapplied to
    the target colour, so a dark fold stays a dark fold.

    Run once when the source art changes. The outputs are committed, because a build should not
    depend on a machine having an imaging library.

    The pixel work is a compiled helper rather than PowerShell. This started as PowerShell loops,
    which is fine for five sheets and is not what this is: twenty-four sheets, a hundred and forty
    cells, an opening pass over each of them and four recolours of every mole is a couple of
    hundred million pixel operations, and measured at the speed the loops ran it would have been
    over twenty minutes. Add-Type keeps that in one file with no new tool to install.

.PARAMETER Source
    The directory the generated sheets are in.

.PARAMETER OutputDirectory
    Where the art goes. Defaults to client/art beside this repo.

.PARAMETER Only
    Wildcard over the manifest's source names, for working on one sheet without waiting for the
    rest. Everything is imported when this is left off.

.PARAMETER Report
    Say what would be written, and what the measurements came out as, without writing anything.
    Worth running first on new art: the horizon fraction and the watermark lift it prints are the
    two numbers the client has hard-coded, and if the art has moved they need moving with it.

.EXAMPLE
    pwsh tools/scripts/import-art.ps1 -Source art -Report

.EXAMPLE
    pwsh tools/scripts/import-art.ps1 -Source art

.EXAMPLE
    pwsh tools/scripts/import-art.ps1 -Source art -Only 'mole *'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Source,

    [string] $OutputDirectory,

    [string] $Only,

    [switch] $Report
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo 'client/art'
}

if (-not (Test-Path $Source)) {
    throw "No art directory at $Source"
}

# ---- The pixel work -------------------------------------------------------------------
#
# Compiled once at startup. Everything below deals in a BGRA byte array, one row after
# another with no stride, because that is what a locked bitmap can be copied into row by row
# and what indexing arithmetic is simplest over.

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;

public static class MolehillArt
{
    public const int KeyNone = 0;
    public const int KeyGreen = 1;
    public const int KeyMagenta = 2;
    public const int KeyWhite = 3;
    public const int KeyBoth = 4;

    // Keyness at or over the first is background; under the second is untouched artwork; between
    // them is an edge pixel, part artwork and part background. Greenness and magentaness are how
    // far the key channels stand above the others, which catches the dark key colour in a
    // shadowed edge and ignores the artwork's own colours, none of which are key-dominant.
    private const int FullyKeyed = 60;
    private const int NoKey = 12;

    // White is measured the other way round, as how little colour a pixel has left.
    private const int FullyWhite = 240;
    private const int NoWhite = 200;

    // What counts as present, for bounds and for the opening. Above nothing, so a stray keyed
    // edge pixel does not pad a sprite out with a margin the game would then plant it on.
    private const int Present = 8;

    private static byte Round(double value)
    {
        if (value <= 0.0) { return 0; }
        if (value >= 255.0) { return 255; }

        return (byte)Math.Round(value, MidpointRounding.ToEven);
    }

    /// <summary>Makes every pixel opaque, for a source that arrived without an alpha channel.</summary>
    public static void Opaque(byte[] px)
    {
        for (int at = 3; at < px.Length; at += 4)
        {
            px[at] = 255;
        }
    }

    /// <summary>Turns the key colour into transparency. Returns dropped and feathered counts.</summary>
    public static int[] Key(byte[] px, int kind)
    {
        int dropped = 0;
        int feathered = 0;

        for (int at = 0; at < px.Length; at += 4)
        {
            int blue = px[at];
            int green = px[at + 1];
            int red = px[at + 2];

            int keyness;
            int mode = kind;

            if (kind == KeyBoth)
            {
                // One key inside the other, as on the energy ring: magenta around it and green
                // through the middle. Whichever this pixel is more of is the one to take off.
                int greenness = green - Math.Max(red, blue);
                int magentaness = Math.Min(red, blue) - green;

                mode = greenness >= magentaness ? KeyGreen : KeyMagenta;
                keyness = Math.Max(greenness, magentaness);
            }
            else if (kind == KeyGreen)
            {
                keyness = green - Math.Max(red, blue);
            }
            else if (kind == KeyMagenta)
            {
                keyness = Math.Min(red, blue) - green;
            }
            else
            {
                keyness = Math.Min(Math.Min(red, green), blue);
            }

            int fully = mode == KeyWhite ? FullyWhite : FullyKeyed;
            int none = mode == KeyWhite ? NoWhite : NoKey;

            if (keyness >= fully)
            {
                px[at] = 0;
                px[at + 1] = 0;
                px[at + 2] = 0;
                px[at + 3] = 0;
                dropped++;
                continue;
            }

            if (keyness <= none)
            {
                px[at + 3] = 255;
                continue;
            }

            double alpha = 1.0 - ((double)(keyness - none) / (fully - none));
            feathered++;

            if (mode == KeyWhite)
            {
                // A proper matte rather than a spill trim: an edge pixel here is the artwork mixed
                // with white in known proportions, so the white can be taken back out. Stripping a
                // channel the way the colour keys do would only turn the edge grey.
                px[at] = Round((blue - (255.0 * (1.0 - alpha))) / alpha);
                px[at + 1] = Round((green - (255.0 * (1.0 - alpha))) / alpha);
                px[at + 2] = Round((red - (255.0 * (1.0 - alpha))) / alpha);
            }
            else
            {
                // Take the spill off, or every edge keeps a rim of the key colour against the sky.
                double spill = keyness * (1.0 - alpha);

                if (mode == KeyGreen)
                {
                    px[at + 1] = Round(Math.Max(0.0, green - spill));
                }
                else
                {
                    px[at] = Round(Math.Max(0.0, blue - spill));
                    px[at + 2] = Round(Math.Max(0.0, red - spill));
                }
            }

            px[at + 3] = Round(255 * alpha);
        }

        return new int[] { dropped, feathered };
    }

    /// <summary>
    /// Drops anything thinner than the radius, and leaves everything thicker alone.
    /// </summary>
    /// <remarks>
    /// A morphological opening on the alpha: shrink by the radius, then grow back by it. What
    /// survives is whatever was at least that thick to begin with, which is the whole of a mole
    /// and none of a cell border, a ground line under its feet or a letter of the artist's own
    /// label. Done this way rather than by finding shapes and judging them, because the useful
    /// question is how thick a thing is, and that needs no idea of what any of it is.
    ///
    /// The radius is a per-sheet setting because it is a judgement about the thinnest thing worth
    /// keeping. Three removes a two-pixel cell border and keeps a claw. On the decor sheets it is
    /// off: a blade of grass is thinner than a claw.
    /// </remarks>
    public static int Open(byte[] px, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return 0;
        }

        byte[] mask = new byte[width * height];

        for (int cell = 0; cell < mask.Length; cell++)
        {
            mask[cell] = px[(cell * 4) + 3] >= Present ? (byte)1 : (byte)0;
        }

        Sweep(mask, width, height, radius, true);
        Sweep(mask, width, height, radius, false);

        int removed = 0;

        for (int cell = 0; cell < mask.Length; cell++)
        {
            if (mask[cell] != 0 || px[(cell * 4) + 3] == 0)
            {
                continue;
            }

            px[cell * 4] = 0;
            px[(cell * 4) + 1] = 0;
            px[(cell * 4) + 2] = 0;
            px[(cell * 4) + 3] = 0;
            removed++;
        }

        return removed;
    }

    // One half of the opening, separably: across then down. Separable because a square window's
    // minimum is the minimum of its row minima, which turns a radius-squared cost into a
    // radius-times-two one.
    private static void Sweep(byte[] mask, int width, int height, int radius, bool shrinking)
    {
        byte[] scratch = new byte[mask.Length];

        for (int pass = 0; pass < 2; pass++)
        {
            bool across = pass == 0;

            for (int down = 0; down < height; down++)
            {
                for (int along = 0; along < width; along++)
                {
                    byte best = shrinking ? (byte)1 : (byte)0;

                    for (int step = -radius; step <= radius; step++)
                    {
                        int x = across ? along + step : along;
                        int y = across ? down : down + step;

                        // Off the edge counts as empty, so a shape running off the side of a cell
                        // is trimmed rather than treated as thick.
                        byte here = (x < 0 || x >= width || y < 0 || y >= height)
                            ? (byte)0
                            : mask[(y * width) + x];

                        if (shrinking)
                        {
                            if (here < best) { best = here; }
                        }
                        else
                        {
                            if (here > best) { best = here; }
                        }
                    }

                    scratch[(down * width) + along] = best;
                }
            }

            Array.Copy(scratch, mask, mask.Length);
        }
    }

    /// <summary>The rectangle inside this cell that has anything in it, or null if it is empty.</summary>
    public static int[] Bounds(byte[] px, int width, int cellX, int cellY, int cellWide, int cellTall)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;

        for (int down = cellY; down < cellY + cellTall; down++)
        {
            for (int along = cellX; along < cellX + cellWide; along++)
            {
                if (px[(((down * width) + along) * 4) + 3] < Present)
                {
                    continue;
                }

                if (along < left) { left = along; }
                if (along > right) { right = along; }
                if (down < top) { top = down; }
                if (down > bottom) { bottom = down; }
            }
        }

        if (right < left)
        {
            return null;
        }

        return new int[] { left, top, right - left + 1, bottom - top + 1 };
    }

    /// <summary>
    /// Copies frames out side by side into one strip, premultiplied.
    /// </summary>
    /// <remarks>
    /// Premultiplied on the way out, because a bicubic shrink that interpolates straight alpha
    /// drags the colour of fully transparent pixels into every edge, and this art is outlined in
    /// near-black, so the result is a dark halo round every sprite against a cream sky.
    ///
    /// Only when the output is about to be resized, though. A strip kept at full size is saved
    /// straight out of these bytes, and premultiplying it there would darken every edge for
    /// nothing.
    ///
    /// Every frame is cut with the same size of rectangle, which is what keeps an animation still.
    /// Cut to its own bounds instead, each frame's bounding box becomes its origin and the mole
    /// jitters as the animation plays.
    /// </remarks>
    public static byte[] Strip(
        byte[] px, int width, int height, int[] originX, int[] originY, int wide, int tall,
        bool premultiply)
    {
        int frames = originX.Length;
        byte[] strip = new byte[frames * wide * tall * 4];

        for (int frame = 0; frame < frames; frame++)
        {
            for (int down = 0; down < tall; down++)
            {
                for (int along = 0; along < wide; along++)
                {
                    int sourceX = originX[frame] + along;
                    int sourceY = originY[frame] + down;

                    if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height)
                    {
                        continue;
                    }

                    int from = ((sourceY * width) + sourceX) * 4;
                    int to = ((down * frames * wide) + (frame * wide) + along) * 4;
                    int alpha = px[from + 3];

                    if (premultiply)
                    {
                        strip[to] = Round(px[from] * alpha / 255.0);
                        strip[to + 1] = Round(px[from + 1] * alpha / 255.0);
                        strip[to + 2] = Round(px[from + 2] * alpha / 255.0);
                    }
                    else
                    {
                        strip[to] = px[from];
                        strip[to + 1] = px[from + 1];
                        strip[to + 2] = px[from + 2];
                    }

                    strip[to + 3] = (byte)alpha;
                }
            }
        }

        return strip;
    }

    /// <summary>
    /// How light the garment is on average, so a recolour has something to measure against.
    /// </summary>
    /// <remarks>
    /// The trunks are the only strongly saturated red in the picture: the body browns sit near
    /// thirty degrees of hue at about half this saturation, and the outline and the eye are
    /// excluded by lightness. Returns the average and the count, because a count near zero means
    /// the thresholds do not match this art and a silent recolour of nothing is the worst outcome.
    /// </remarks>
    public static double[] Garment(byte[] px)
    {
        double total = 0.0;
        int count = 0;

        for (int at = 0; at < px.Length; at += 4)
        {
            if (px[at + 3] < Present) { continue; }

            double value;

            if (IsGarment(px[at + 2], px[at + 1], px[at], out value))
            {
                total += value;
                count++;
            }
        }

        return new double[] { count > 0 ? total / count : 1.0, count };
    }

    /// <summary>This platoon's copy, with the garment in its colour and every fold kept.</summary>
    public static byte[] Recolour(byte[] px, double red, double green, double blue, double average)
    {
        byte[] copy = new byte[px.Length];
        Array.Copy(px, copy, px.Length);

        for (int at = 0; at < copy.Length; at += 4)
        {
            if (copy[at + 3] < Present) { continue; }

            double value;

            if (!IsGarment(copy[at + 2], copy[at + 1], copy[at], out value))
            {
                continue;
            }

            // This pixel's own lightness against the garment's average, reapplied to the target,
            // so a dark fold stays a dark fold and a highlight stays a highlight.
            double ratio = value / average;

            copy[at] = Round(255.0 * blue * ratio);
            copy[at + 1] = Round(255.0 * green * ratio);
            copy[at + 2] = Round(255.0 * red * ratio);
        }

        return copy;
    }

    // The trunks are the only strongly saturated red in the picture, and "strongly" is the word
    // that had to be measured rather than guessed. At the mole sheet's old threshold of 0.45 this
    // matched twenty-eight thousand body pixels as well as the trunks, because this art's brown
    // sits at about a tenth of a hue turn with saturation to 0.65, and the generator's own noise
    // pushes plenty of it over the line: the result was a mole with green blotches all over it.
    // Measured on the baseline sheet, the trunks are thirty-three thousand pixels at saturation
    // 0.75 and above, averaging 187, 41, 39, and the body tops out at 0.65. Seventy separates them
    // with room either side.
    private const double GarmentMaxHue = 22.0;
    private const double GarmentMinHue = 344.0;
    private const double GarmentMinSaturation = 0.70;
    private const double GarmentMinValue = 0.18;

    private static bool IsGarment(int red, int green, int blue, out double value)
    {
        double r = red / 255.0;
        double g = green / 255.0;
        double b = blue / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double span = max - min;

        value = max;

        double saturation = max <= 0.0 ? 0.0 : span / max;

        if (saturation < GarmentMinSaturation || max < GarmentMinValue)
        {
            return false;
        }

        double hue = 0.0;

        if (span > 0.0)
        {
            if (max == r) { hue = 60.0 * (((g - b) / span) % 6.0); }
            else if (max == g) { hue = 60.0 * (((b - r) / span) + 2.0); }
            else { hue = 60.0 * (((r - g) / span) + 4.0); }
        }

        if (hue < 0.0) { hue += 360.0; }

        return hue <= GarmentMaxHue || hue >= GarmentMinHue;
    }

    /// <summary>
    /// The offset whose copy of these rows both joins up at the border and brings the least with it.
    /// </summary>
    /// <remarks>
    /// Both halves of that scoring earned their place. This dirt is banded, so a patch taken from
    /// the wrong offset puts a stratum line at the wrong height and the seam is more obvious than
    /// the watermark was. And a patch that happens to contain a light pebble swaps one bright spot
    /// for another, which is exactly what the check afterwards is looking for and leaves it unable
    /// to say whether the patch worked.
    /// </remarks>
    public static double[] Offset(
        byte[] px, int width, int height, int wx, int wy, int ww, int wh, int feather)
    {
        int left = wx - feather;
        int top = wy - feather;
        int right = wx + ww + feather;
        int bottom = wy + wh + feather;

        int[] candidates = new int[]
        {
            -384, -360, -336, -312, -288, -264, -240, -216, -192, -168, -144, -120, -96,
            96, 120, 144, 168, 192, 216, 240, 264, 288, 312, 336, 360, 384,
        };

        int[] nudges = new int[] { -8, -4, 0, 4, 8 };

        double bestScore = double.MaxValue;
        double bestDx = 0.0;
        double bestDy = 0.0;
        double bestMismatch = 0.0;
        double bestPeak = 0.0;
        bool found = false;

        double[] interior = new double[((right - left) / 2 + 2) * ((bottom - top) / 2 + 2)];

        foreach (int dx in candidates)
        {
            foreach (int dy in nudges)
            {
                if (left + dx < 0 || right + dx >= width) { continue; }
                if (top + dy < 0 || bottom + dy >= height) { continue; }

                double mismatch = 0.0;
                int edges = 0;
                int inside = 0;

                for (int y = top; y < bottom; y += 2)
                {
                    for (int x = left; x < right; x += 2)
                    {
                        int there = (((y + dy) * width) + (x + dx)) * 4;

                        if (x >= wx && x < wx + ww && y >= wy && y < wy + wh)
                        {
                            interior[inside++] =
                                (px[there] + px[there + 1] + px[there + 2]) / 3.0;
                            continue;
                        }

                        int here = ((y * width) + x) * 4;

                        mismatch += Math.Abs(px[here] - px[there]);
                        mismatch += Math.Abs(px[here + 1] - px[there + 1]);
                        mismatch += Math.Abs(px[here + 2] - px[there + 2]);
                        edges++;
                    }
                }

                if (edges == 0 || inside == 0) { continue; }

                mismatch = mismatch / edges;

                double[] sorted = new double[inside];
                Array.Copy(interior, sorted, inside);
                Array.Sort(sorted);

                double peak = sorted[inside - 1] - sorted[inside / 2];
                double score = mismatch + (peak * 0.25);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestDx = dx;
                    bestDy = dy;
                    bestMismatch = mismatch;
                    bestPeak = peak;
                    found = true;
                }
            }
        }

        if (!found)
        {
            return null;
        }

        return new double[] { bestDx, bestDy, bestMismatch, bestPeak };
    }

    /// <summary>Feathers the chosen patch over the hole.</summary>
    public static void Patch(
        byte[] px, int width, int height, int wx, int wy, int ww, int wh, int feather, int dx, int dy)
    {
        // Copied out of the original rather than out of what is being written, so a patch never
        // reads pixels it has already changed.
        byte[] from = new byte[px.Length];
        Array.Copy(px, from, px.Length);

        for (int y = wy - feather; y < wy + wh + feather; y++)
        {
            for (int x = wx - feather; x < wx + ww + feather; x++)
            {
                int out2 = Math.Max(
                    Math.Max(wx - x, x - (wx + ww - 1)),
                    Math.Max(wy - y, y - (wy + wh - 1)));

                if (out2 >= feather) { continue; }

                double weight = out2 <= 0 ? 1.0 : 1.0 - ((double)out2 / feather);

                // Smoothed, so the join has no visible shoulder.
                weight = weight * weight * (3.0 - (2.0 * weight));

                int here = ((y * width) + x) * 4;
                int there = (((y + dy) * width) + (x + dx)) * 4;

                for (int channel = 0; channel < 3; channel++)
                {
                    px[here + channel] = Round(
                        ((1.0 - weight) * from[here + channel]) + (weight * from[there + channel]));
                }
            }
        }
    }

    /// <summary>
    /// The most any pixel in a rectangle stands above the median of its own rows.
    /// </summary>
    /// <remarks>
    /// Read against a control rectangle rather than on its own. A light pebble stands above its
    /// rows every bit as much as a watermark does, so a patched region full of pebbles scores
    /// higher than the star did and means nothing by itself. What says the star has gone is the
    /// patched rectangle scoring about what ordinary dirt in the same rows scores.
    /// </remarks>
    public static double Lift(byte[] px, int width, int wx, int wy, int ww, int wh)
    {
        double peak = 0.0;
        double[] row = new double[width];

        for (int y = wy; y < wy + wh; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                row[x] = (px[at] + px[at + 1] + px[at + 2]) / 3.0;
            }

            double[] sorted = (double[])row.Clone();
            Array.Sort(sorted);
            double median = sorted[width / 2];

            for (int x = wx; x < wx + ww; x++)
            {
                int at = ((y * width) + x) * 4;
                double here = (px[at] + px[at + 1] + px[at + 2]) / 3.0;

                if (here - median > peak) { peak = here - median; }
            }
        }

        return peak;
    }

    /// <summary>
    /// The row the panorama's own foreground begins on, which is its horizon.
    /// </summary>
    /// <remarks>
    /// The client anchors that row to the map's surface, so this is the one number about this
    /// image the game has to agree with. Found as the first row below the middle whose mean
    /// brightness jumps, which is the bright line along the top of the near ground.
    /// </remarks>
    public static int Horizon(byte[] px, int width, int height)
    {
        double previous = -1.0;

        for (int y = height / 2; y < height; y++)
        {
            double total = 0.0;

            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                total += px[at] + px[at + 1] + px[at + 2];
            }

            double mean = total / (3.0 * width);

            if (previous >= 0.0 && mean - previous > 8.0)
            {
                return y;
            }

            previous = mean;
        }

        return -1;
    }

    /// <summary>
    /// How far the drawing reaches from the middle of a flat-backed image, as a fraction of half
    /// its width.
    /// </summary>
    /// <remarks>
    /// For the app icon, where the number decides how far the artwork is shrunk on the way into an
    /// Android adaptive layer. A launcher may mask that layer with any shape it likes and only
    /// promises to show a circle across two thirds of it, so anything outside that circle is at the
    /// mercy of whichever phone it is, and the shrink that keeps the drawing inside it depends
    /// entirely on how much margin the artist left.
    ///
    /// A radius rather than a bounding box, because the guarantee is a circle: a drawing whose
    /// corners are empty may be a good deal wider than one that fills them and still fit.
    /// </remarks>
    public static double Reach(byte[] px, int width, int height, int[] backing)
    {
        int red = backing[0];
        int green = backing[1];
        int blue = backing[2];

        double middleX = width / 2.0;
        double middleY = height / 2.0;
        double furthest = 0.0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                int apart = Math.Abs(px[at + 2] - red)
                    + Math.Abs(px[at + 1] - green)
                    + Math.Abs(px[at] - blue);

                if (apart <= 30) { continue; }

                double dx = x - middleX;
                double dy = y - middleY;
                double away = Math.Sqrt((dx * dx) + (dy * dy));

                if (away > furthest) { furthest = away; }
            }
        }

        return furthest / middleX;
    }

    /// <summary>
    /// The flat colour an icon is drawn on, as red, green and blue.
    /// </summary>
    /// <remarks>
    /// The median of the border ring rather than a corner pixel, for two reasons. It is what the
    /// adaptive background layer is filled with, and it meets the foreground layer along all four
    /// of the artwork's edges, so the colour that hides the seam is the middle of what those edges
    /// actually are: this drawing is four units per channel darker at the middle of an edge than in
    /// its corner, and pinning to either end of that spreads the whole of the difference onto the
    /// other. And a measurement resting on one pixel is a measurement one stray pixel can wreck,
    /// which would take <see cref="Reach"/> with it, since a background it does not recognise makes
    /// the entire image count as drawing.
    /// </remarks>
    public static int[] Backing(byte[] px, int width, int height)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();

        for (int x = 0; x < width; x++)
        {
            Border(px, width, x, 0, reds, greens, blues);
            Border(px, width, x, height - 1, reds, greens, blues);
        }

        for (int y = 1; y < height - 1; y++)
        {
            Border(px, width, 0, y, reds, greens, blues);
            Border(px, width, width - 1, y, reds, greens, blues);
        }

        return new int[] { Middle(reds), Middle(greens), Middle(blues) };
    }

    private static void Border(
        byte[] px, int width, int x, int y,
        List<byte> reds, List<byte> greens, List<byte> blues)
    {
        int at = ((y * width) + x) * 4;

        reds.Add(px[at + 2]);
        greens.Add(px[at + 1]);
        blues.Add(px[at]);
    }

    private static int Middle(List<byte> channel)
    {
        channel.Sort();

        return channel[channel.Count / 2];
    }
}
'@

# ---- Getting pixels in and out --------------------------------------------------------

function Read-Raster([string] $path) {
    $bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path $path))

    try {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $locked = $bitmap.LockBits(
            $rect,
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

        try {
            $bytes = New-Object 'byte[]' ($bitmap.Width * $bitmap.Height * 4)

            # Row by row, because a locked bitmap's stride is not necessarily its width.
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                [System.Runtime.InteropServices.Marshal]::Copy(
                    [System.IntPtr]::Add($locked.Scan0, $y * $locked.Stride),
                    $bytes,
                    $y * $bitmap.Width * 4,
                    $bitmap.Width * 4)
            }

            return @{ Width = $bitmap.Width; Height = $bitmap.Height; Bytes = $bytes }
        }
        finally {
            $bitmap.UnlockBits($locked)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function New-BitmapFrom([byte[]] $bytes, [int] $width, [int] $height, [bool] $premultiplied) {
    $format = if ($premultiplied) {
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb
    }
    else {
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height, $format
    $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $locked = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $format)

    try {
        for ($y = 0; $y -lt $height; $y++) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                $bytes,
                $y * $width * 4,
                [System.IntPtr]::Add($locked.Scan0, $y * $locked.Stride),
                $width * 4)
        }
    }
    finally {
        $bitmap.UnlockBits($locked)
    }

    return $bitmap
}

function Save-Png(
    [byte[]] $bytes, [int] $width, [int] $height, [bool] $premultiplied,
    [double] $scale, [string] $path) {

    # Straight out, when nothing is being resized. Going through the resampler at one to one
    # would be a copy with rounding in it, and these are the ground textures, which have to come
    # out of the importer identical to the ones already committed.
    if ($scale -eq 1.0 -and -not $premultiplied) {
        $only = New-BitmapFrom $bytes $width $height $false

        try {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            $only.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $only.Dispose()
        }

        return @{ Wide = $width; Tall = $height }
    }

    $wide = [Math]::Max(1, [int] [Math]::Round($width * $scale))
    $tall = [Math]::Max(1, [int] [Math]::Round($height * $scale))

    $source = New-BitmapFrom $bytes $width $height $premultiplied
    $target = New-Object System.Drawing.Bitmap $wide, $tall,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $canvas = [System.Drawing.Graphics]::FromImage($target)

        try {
            $canvas.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $canvas.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # Mirrored rather than wrapped at the edges, or bicubic samples off one side of a
            # sprite and brings the other side back with it.
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

            $where = New-Object System.Drawing.Rectangle 0, 0, $wide, $tall
            $canvas.DrawImage($source, $where, 0, 0, $width, $height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        }
        finally {
            $canvas.Dispose()
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $target.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $source.Dispose()
        $target.Dispose()
    }

    return @{ Wide = $wide; Tall = $tall }
}

# ---- The app icon -----------------------------------------------------------------------
#
# The one piece of art nothing in the game draws. It is the picture a launcher, a task bar and a
# store page use, and each of them wants it in a shape of its own, so this ends in five files
# rather than one.

# The side of an Android adaptive icon layer, and the width of the circle a launcher promises to
# show. Both from the platform: a layer is 108dp, the mask can be any shape inside the middle 72dp,
# and only the middle 66dp is guaranteed to survive whichever shape a given phone picked. At the
# four-times density these are exported at, that is 432 and 264 pixels.
$adaptiveLayer = 432
$adaptiveSafe = 264

# What Windows wants in an .ico, largest first. Sixteen is the one that ends up in a title bar and
# 256 is the one a file browser shows at its largest setting.
$icoSizes = @(256, 128, 64, 48, 32, 16)

function Save-Inset(
    [byte[]] $bytes, [int] $width, [int] $height, [int] $side, [int] $inner, [string] $path) {

    $source = New-BitmapFrom $bytes $width $height $false
    $target = New-Object System.Drawing.Bitmap $side, $side,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $canvas = [System.Drawing.Graphics]::FromImage($target)

        try {
            $canvas.Clear([System.Drawing.Color]::Transparent)
            $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $canvas.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # Mirrored at the edges for the same reason every other resize here is: bicubic samples
            # past the side of the source and would otherwise bring the far side back with it.
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

            $edge = [int] [Math]::Round(($side - $inner) / 2.0)
            $where = New-Object System.Drawing.Rectangle $edge, $edge, $inner, $inner
            $canvas.DrawImage($source, $where, 0, 0, $width, $height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        }
        finally {
            $canvas.Dispose()
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $target.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $source.Dispose()
        $target.Dispose()
    }
}

function Save-Solid([int[]] $rgb, [int] $side, [string] $path) {
    $target = New-Object System.Drawing.Bitmap $side, $side,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $canvas = [System.Drawing.Graphics]::FromImage($target)

        try {
            $canvas.Clear([System.Drawing.Color]::FromArgb(255, $rgb[0], $rgb[1], $rgb[2]))
        }
        finally {
            $canvas.Dispose()
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $target.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $target.Dispose()
    }
}

<#
.SYNOPSIS
    Writes a Windows .ico holding the same picture at several sizes.

.DESCRIPTION
    Hand-assembled, because System.Drawing can read an icon and cannot write one. The format is a
    six byte header, one sixteen byte entry per size, and then the images end to end, with each
    entry saying how long its image is and where it starts.

    The images are PNGs rather than the bitmaps the original format called for. Every Windows that
    can run this game reads those, and a bitmap entry would mean writing the upside-down DIB and the
    obsolete one-bit mask that goes with it by hand as well.

    A side of 256 is written as a zero, which is the format's way of saying so: the field is one
    byte and 256 does not fit in it.
#>
function Save-Ico(
    [byte[]] $bytes, [int] $width, [int] $height, [int[]] $sizes, [string] $path) {

    $source = New-BitmapFrom $bytes $width $height $false
    $images = @()

    try {
        foreach ($side in $sizes) {
            $target = New-Object System.Drawing.Bitmap $side, $side,
                ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

            try {
                $canvas = [System.Drawing.Graphics]::FromImage($target)

                try {
                    $canvas.InterpolationMode =
                        [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $canvas.PixelOffsetMode =
                        [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

                    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
                    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

                    $where = New-Object System.Drawing.Rectangle 0, 0, $side, $side
                    $canvas.DrawImage($source, $where, 0, 0, $width, $height,
                        [System.Drawing.GraphicsUnit]::Pixel, $attributes)
                }
                finally {
                    $canvas.Dispose()
                }

                $buffer = New-Object System.IO.MemoryStream
                $target.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
                $images += , @{ Side = $side; Bytes = $buffer.ToArray() }
                $buffer.Dispose()
            }
            finally {
                $target.Dispose()
            }
        }
    }
    finally {
        $source.Dispose()
    }

    $file = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $file

    try {
        $writer.Write([uint16] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] $images.Count)

        $at = 6 + (16 * $images.Count)

        foreach ($image in $images) {
            $writer.Write([byte] ($image.Side % 256))
            $writer.Write([byte] ($image.Side % 256))
            $writer.Write([byte] 0)
            $writer.Write([byte] 0)
            $writer.Write([uint16] 1)
            $writer.Write([uint16] 32)
            $writer.Write([uint32] $image.Bytes.Length)
            $writer.Write([uint32] $at)

            $at += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Bytes)
        }

        $writer.Flush()

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [System.IO.File]::WriteAllBytes($path, $file.ToArray())
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

<#
.SYNOPSIS
    Writes the whole icon family from one square drawing.

.DESCRIPTION
    Five files, because the platforms disagree about what an icon is.

    `app.png` is the picture itself, full size. It is what the project uses for its window and what
    a store listing wants.

    `launcher.png` is the same thing at 192, which is the icon Android shows on anything too old to
    ask for an adaptive one.

    The two adaptive layers are the interesting pair. A modern launcher takes a foreground and a
    background, masks them to whatever shape that phone's theme uses, and slides one against the
    other when the icon is touched. So the drawing goes on the foreground and the flat colour it was
    drawn on goes on the background, and the foreground is shrunk until the drawing fits inside the
    circle every mask is guaranteed to contain. How far it shrinks is measured off the artwork
    rather than fixed, since it depends entirely on the margin the artist left.

    The layers meet invisibly because the background is sampled from the artwork's own border: the
    foreground still carries that same flat colour out to its edges, so the seam is one brown
    against very nearly the identical brown, and the parallax slides brown over brown.

    `app.ico` is the Windows executable's icon.
#>
function Write-Icons(
    [hashtable] $sheet, [byte[]] $bytes, [int] $width, [int] $height,
    [string] $outputRoot, [bool] $apply) {

    if ($width -ne $height) {
        throw "An icon has to be square, and $($sheet.From) is ${width}x${height}."
    }

    $backing = [MolehillArt]::Backing($bytes, $width, $height)
    $reach = [MolehillArt]::Reach($bytes, $width, $height, $backing)

    # Bigger than the layer would mean artwork with no margin at all, which is a sheet that wants
    # redrawing rather than a number to clamp.
    $inner = [Math]::Min($adaptiveLayer, [int] [Math]::Floor($adaptiveSafe / $reach))

    # How much of the guaranteed circle is left over once the drawing is in it, either side. A
    # negative number is a drawing hanging out of the circle, which is the thing worth being told.
    $spare = ($adaptiveSafe - ($reach * $inner)) / 2.0

    Write-Host ("icon    background {0},{1},{2}  drawing reaches {3:N3} of the way out" -f
        $backing[0], $backing[1], $backing[2], $reach)
    Write-Host ("adapt   artwork at {0} of {1}, {2:N1} to spare inside the guaranteed {3}" -f
        $inner, $adaptiveLayer, $spare, $adaptiveSafe)

    if ($spare -lt 0) {
        Write-Warning 'The drawing does not fit the guaranteed circle. Some launchers will clip it.'
    }

    # Said plainly, because a report reads the watermark rather than the patch that removes it, and
    # the star is the furthest thing from the middle of this particular drawing.
    if ($Report -and $sheet.ContainsKey('Watermark')) {
        Write-Host 'note    measured before the watermark patch, so the reach is the star, not the art'
    }

    $into = if ($sheet.ContainsKey('Into')) { $sheet.Into + '/' } else { '' }
    $name = $sheet.Name

    Write-Host ("icons   {0}app.png, {0}launcher.png, two adaptive layers, {0}app.ico" -f $into)

    if (-not $apply) { return }

    $app = Join-Path $outputRoot "$into$name.png"
    $launcher = Join-Path $outputRoot "${into}launcher.png"
    $foreground = Join-Path $outputRoot "${into}adaptive-foreground.png"
    $background = Join-Path $outputRoot "${into}adaptive-background.png"
    $windows = Join-Path $outputRoot "$into$name.ico"

    Save-Png $bytes $width $height $false 1.0 $app | Out-Null
    Save-Png $bytes $width $height $false (192 / [double] $width) $launcher | Out-Null
    Save-Inset $bytes $width $height $adaptiveLayer $inner $foreground
    Save-Solid $backing $adaptiveLayer $background
    Save-Ico $bytes $width $height $icoSizes $windows

    Write-Host ("     -> {0}" -f (Resolve-Path -Relative (Split-Path -Parent $app)))
}

# ---- Platoons -------------------------------------------------------------------------
#
# Palette.Seat, in order. The same four colours the panes, the plan markers, the broadcast
# captions and the tally all use, so a mole's trunks match the platoon it belongs to.

$seats = @(
    @{ Name = 'green';  R = 0.294; G = 0.545; B = 0.231 }
    @{ Name = 'orange'; R = 0.780; G = 0.353; B = 0.157 }
    @{ Name = 'blue';   R = 0.306; G = 0.510; B = 0.651 }
    @{ Name = 'red';    R = 0.769; G = 0.165; B = 0.047 }
)

# How wide a band around a watermark the patch fades in over. Wide enough that a small
# mismatch in the copied dirt is spread out rather than landing on a line.
$feather = 24

$keyKinds = @{
    'none'    = [MolehillArt]::KeyNone
    'green'   = [MolehillArt]::KeyGreen
    'magenta' = [MolehillArt]::KeyMagenta
    'white'   = [MolehillArt]::KeyWhite
    'both'    = [MolehillArt]::KeyBoth
}

# ---- The manifest ---------------------------------------------------------------------
#
# Every sheet, and everything about it that is not visible in the pixels. The grids were read
# off a gutter analysis of each sheet one at a time rather than assumed; where a sheet carries
# the artist's own labels under each cell, Crop takes that band off before the cell is trimmed.
#
# Pack says what shape the output takes, and it is the one field worth reading twice.
#   whole  one image, trimmed, no cells. Ground textures and the lava strips.
#   cells  one file per cell, each trimmed to itself. Things used independently and at
#          different sizes: decor, projectiles, traps, glyphs.
#   strip  one file for the set, every frame the same size, cut with the union of what all of
#          them occupy. Animations, where a frame trimmed to itself would jitter.
#
# Open is the opening radius, which is a judgement about the thinnest thing on the sheet worth
# keeping. Three removes a cell border and a ground line and keeps a claw. It is off for the
# decor, where a blade of grass is thinner than a claw, and for the ground, which is solid.
#
# The scales are not derived from anything. They were set by importing at one scale, looking at
# the sizes, and settling them so a mole is about the same size whichever sheet its pose came
# from, because the artist drew the poses at whatever size suited the sheet.

$sheets = @(
    # ---- The ground ------------------------------------------------------------------
    @{ From = 'foreground underground.jpg'; Key = 'none'; Pack = 'whole'; Name = 'terrain-dirt'
       Scale = 1.0; Open = 0 }

    @{ From = 'background underground.png'; Key = 'none'; Pack = 'whole'; Name = 'terrain-deep'
       Scale = 1.0; Open = 0
       Watermark = @{ X = 872; Y = 872; Width = 64; Height = 64 } }

    @{ From = 'background surface.png'; Key = 'none'; Pack = 'whole'; Name = 'backdrop-surface'
       Scale = 1.0; Open = 0; Horizon = $true
       Watermark = @{ X = 1308; Y = 570; Width = 60; Height = 60 } }

    # Tiles sideways along the world, so it keeps its full width. The white above the crust is
    # the background here rather than a chroma key, which is why this one is matted rather than
    # spilled: an edge pixel is the artwork mixed with a known white and the white comes back out.
    # Watermarked as well, and it matters for the same reason: this one tiles along the bottom of
    # the map and up both sides once the lava starts closing in, so the star would appear in a row.
    @{ From = 'lava floor.png'; Key = 'white'; Pack = 'whole'; Name = 'lava-floor'
       Scale = 1.0; Open = 0
       Watermark = @{ X = 1912; Y = 360; Width = 64; Height = 64 } }

    @{ From = 'lava wall.png'; Key = 'white'; Pack = 'whole'; Name = 'lava-wall'
       Scale = 1.0; Open = 0
       Watermark = @{ X = 1912; Y = 360; Width = 64; Height = 64 } }

    # ---- The menu ---------------------------------------------------------------------
    #
    # Full bleed rather than trimmed or tiled: it is one picture behind one screen, stretched to
    # whatever shape the window is, so it has no key to pull out and no edges to match.
    @{ From = 'main menu background.jpg'; Key = 'none'; Pack = 'whole'; Name = 'menu-ground'
       Scale = 1.0; Open = 0 }

    @{ From = 'molehill madness title.jpg'; Key = 'green'; Pack = 'whole'; Name = 'menu-title'
       Scale = 0.6; Open = 3 }

    # ---- Garden dressing --------------------------------------------------------------
    @{ From = 'grass background.png'; Key = 'magenta'; Pack = 'cells'; Into = 'decor'
       Grid = @(4, 2); Scale = 0.5; Open = 0
       Names = @('grass-0', 'grass-1', 'grass-2', 'grass-3',
                 'grass-4', 'grass-5', 'grass-6', 'grass-7') }

    @{ From = 'grass background decor.png'; Key = 'magenta'; Pack = 'cells'; Into = 'decor'
       Grid = @(4, 2); Scale = 0.5; Open = 0
       Names = @('molehill-small', 'molehill', 'worm', 'flowers',
                 'flower', 'dandelion', 'snowdrops', 'stone') }

    # ---- Moles ------------------------------------------------------------------------
    #
    # Four copies of each, one per platoon. The trunks are the only part of the artwork that can
    # carry a team colour without repainting the animal.

    @{ From = 'mole baseline.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'stand'
       Grid = @(1, 1); Frames = 1; Scale = 0.30; Open = 4; Platoons = $true }

    @{ From = 'mole KO.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'ko'
       Grid = @(1, 1); Frames = 1; Scale = 0.30; Open = 4; Platoons = $true }

    # Five aims, from sixty degrees down to sixty degrees up. The artist's angle labels sit
    # under each one, so seventy pixels come off the bottom of every cell.
    @{ From = 'mole aim.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'aim'
       Grid = @(5, 1); Frames = 5; Scale = 0.58; Open = 4; Platoons = $true
       Crop = @(0, 0, 0, 70) }

    # Tumbling, four poses and then the same four mirrored, labelled underneath.
    @{ From = 'mole airborne.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'airborne'
       Grid = @(4, 2); Frames = 8; Scale = 0.64; Open = 4; Platoons = $true
       Crop = @(0, 0, 0, 35) }

    # Eight frames of walking, which the set went without for a while: a mole crossing the surface
    # stood still and slid along, and it was the most obvious gap in the whole of the art.
    @{ From = 'Mole Walk.jpg'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'walk'
       Grid = @(4, 2); Frames = 8; Scale = 0.78; Open = 4; Platoons = $true }

    @{ From = 'mole dig.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'dig'
       Grid = @(3, 2); Frames = 6; Scale = 0.62; Open = 4; Platoons = $true }

    @{ From = 'mole hit.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'hit'
       Grid = @(3, 1); Frames = 3; Scale = 0.45; Open = 4; Platoons = $true }

    @{ From = 'mole power claw.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'claws'
       Grid = @(3, 2); Frames = 6; Scale = 0.68; Open = 4; Platoons = $true }

    @{ From = 'mole rooted.png'; Key = 'green'; Pack = 'strip'; Into = 'mole'; Name = 'rooted'
       Grid = @(4, 2); Frames = 8; Scale = 0.62; Open = 4; Platoons = $true }

    # ---- Faces -----------------------------------------------------------------------
    #
    # Eight expressions of one mole's head, for the places that want a mole without wanting a whole
    # animation: the loading bar, the menu's player-count chooser, and later the scoreboard and the
    # field hospital.
    #
    # Not per platoon, and there is nothing on these heads that could be. Every other mole sheet
    # takes its team colour from the trunks, which are red and saturated and so easy to pick out of
    # the artwork; a head has no trunks. The first version of this sheet wore a white cap, which
    # looked like somewhere a colour could go until it was measured: the cap was exactly the same
    # white as the eyes and the teeth, 248 on all three channels, separated only by height in the
    # cell, and three percent of a cell is not a margin to bet eight faces on. The cap is gone now
    # and the question is settled. Whose mole it is goes on a disc behind the face.
    #
    # The whiskers are drawn inside the silhouette rather than sticking out of it, so the opening
    # radius cannot reach them: it erodes the alpha mask, and they are interior colour.

    @{ From = 'Mole face icons.jpg'; Key = 'green'; Pack = 'strip'; Into = 'glyph'; Name = 'face'
       Grid = @(4, 2); Frames = 8; Scale = 0.42; Open = 3 }

    # ---- The eight exits --------------------------------------------------------------
    #
    # Named for KnockoutExit rather than for the sheet, because the simulation has been choosing
    # between these eight since Phase 1 and the client's job is only to play the one it chose.
    # The ninth sheet is a plain launch that matches no exit; it comes in under its own name.

    @{ From = 'mole death 1.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'launch'
       Grid = @(4, 2); Frames = 6; Scale = 0.80; Open = 4; Platoons = $true }

    @{ From = 'mole death 2.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'poof'
       Grid = @(3, 2); Frames = 6; Scale = 0.68; Open = 4; Platoons = $true }

    @{ From = 'mole death 3.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'stretcher'
       Grid = @(3, 2); Frames = 6; Scale = 0.75; Open = 4; Platoons = $true }

    @{ From = 'mole death 4.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'birds'
       Grid = @(3, 2); Frames = 6; Scale = 0.75; Open = 4; Platoons = $true }

    @{ From = 'mole death 5.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'balloon'
       Grid = @(4, 2); Frames = 8; Scale = 0.80; Open = 4; Platoons = $true }

    # Imported and not used. Every cell of this sheet is a solid rectangle of dirt wall from edge to
    # edge, because the gag is a mole punched through a wall and the wall is drawn rather than
    # implied. Over a procedural hillside that is a pale slab dropped on the landscape whatever it is
    # anchored to, so MoleShapedHole borrows the spin-and-poof frames until there is a version of
    # this sheet with nothing behind the mole.
    @{ From = 'mole death 6.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'hole'
       Grid = @(3, 2); Frames = 6; Scale = 0.62; Open = 4; Platoons = $true }

    # Forty-two pixels off the bottom of every cell, which is a band of ground the artist drew in.
    # Planted on the real surface it would lay a three metre slab of somebody else's soil over the
    # actual ground, and the gag is a mole leaving rather than the ground it left from.
    @{ From = 'mole death 7.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'helmet'
       Grid = @(3, 2); Frames = 6; Scale = 0.70; Open = 4; Platoons = $true
       Crop = @(0, 0, 0, 42) }

    @{ From = 'mole death 8.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'sink'
       Grid = @(3, 2); Frames = 6; Scale = 0.72; Open = 4; Platoons = $true
       Crop = @(0, 0, 0, 40) }

    @{ From = 'mole death 9.png'; Key = 'green'; Pack = 'strip'; Into = 'exit'; Name = 'steam'
       Grid = @(4, 2); Frames = 7; Scale = 0.70; Open = 4; Platoons = $true }

    # ---- Things in the world ----------------------------------------------------------

    @{ From = 'projectiles.png'; Key = 'green'; Pack = 'cells'; Into = 'object'
       Grid = @(3, 3); Scale = 0.35; Open = 3
       Names = @('clod', 'beetle', 'acorn', 'acorns', 'beetroot', 'relic', 'gnome', 'sack') }

    @{ From = 'traps.png'; Key = 'green'; Pack = 'cells'; Into = 'object'
       Grid = @(3, 2); Scale = 0.35; Open = 3
       Names = @('mound', 'snaptrap', 'snare', 'vent', 'sandbag') }

    @{ From = 'crate.png'; Key = 'green'; Pack = 'cells'; Into = 'object'
       Grid = @(3, 3); Scale = 0.35; Open = 2
       Names = @('chute-0', 'chute-1', 'chute-2', 'landed', 'open', 'closed', 'marker') }

    # One picture of a whole beam rather than a grid, because a girder is laid in one piece at one
    # length: four metres of it, which is what the artist drew it as and what the simulation lays.
    # Wider than anything else here by a distance, so it is sized off its own length at draw time
    # rather than off the shared sprite pitch the other objects use.
    @{ From = 'girder.jpg'; Key = 'green'; Pack = 'cells'; Into = 'object'
       Grid = @(1, 1); Scale = 0.35; Open = 3
       Names = @('girder') }

    # ---- Effects ----------------------------------------------------------------------

    @{ From = 'explosion.png'; Key = 'green'; Pack = 'strip'; Into = 'effect'; Name = 'blast'
       Grid = @(5, 2); Frames = 8; Scale = 0.45; Open = 3 }

    # Magenta around the ring and green through the middle, so both keys come off this one.
    @{ From = 'energy effect.png'; Key = 'both'; Pack = 'strip'; Into = 'effect'; Name = 'ring'
       Grid = @(4, 2); Frames = 8; Scale = 0.45; Open = 3 }

    @{ From = 'geyser oil.png'; Key = 'green'; Pack = 'strip'; Into = 'effect'; Name = 'geyser'
       Grid = @(5, 1); Frames = 5; Scale = 0.35; Open = 3 }

    @{ From = 'drill.png'; Key = 'green'; Pack = 'strip'; Into = 'effect'; Name = 'drill'
       Grid = @(2, 3); Frames = 6; Scale = 0.35; Open = 3 }

    # ---- Glyphs -----------------------------------------------------------------------
    #
    # White silhouettes, numbered by WeaponId rather than named, because the artist drew all
    # fifteen in that order across three sheets and an index is what the game looks them up by.
    # White so they can be tinted to a platoon's colour at draw time, which is the property the
    # drawn-from-primitives glyphs had and the reason it is safe to replace them.

    @{ From = 'weapon glyph 1.png'; Key = 'green'; Pack = 'cells'; Into = 'glyph'
       Grid = @(3, 2); Scale = 0.30; Open = 3
       Names = @('weapon-01', 'weapon-02', 'weapon-03', 'weapon-04', 'weapon-05', 'weapon-06') }

    @{ From = 'weapon glyph 2.png'; Key = 'green'; Pack = 'cells'; Into = 'glyph'
       Grid = @(3, 2); Scale = 0.30; Open = 3
       Names = @('weapon-07', 'weapon-08', 'weapon-09', 'weapon-10', 'weapon-11', 'weapon-12') }

    @{ From = 'weapons glyph 3.jpg'; Key = 'green'; Pack = 'cells'; Into = 'glyph'
       Grid = @(3, 1); Scale = 0.30; Open = 3
       Names = @('weapon-13', 'weapon-14', 'weapon-15') }

    @{ From = 'icon glyphs.png'; Key = 'green'; Pack = 'cells'; Into = 'glyph'
       Grid = @(4, 3); Scale = 0.30; Open = 3
       Names = @('pause', 'settings', 'sound', 'mute', 'undo', 'aim',
                 'tick', 'cross', 'pack', 'heart', 'star') }

    @{ From = 'numbers and symbols.png'; Key = 'green'; Pack = 'cells'; Into = 'glyph'
       Grid = @(8, 2); Scale = 0.30; Open = 3
       Names = @('digit-0', 'digit-1', 'digit-2', 'digit-3', 'digit-4', 'digit-5', 'digit-6',
                 'digit-7', 'digit-8', 'digit-9', 'plus', 'minus', 'percent', 'colon', 'stop') }

    # The aim gauge, which every ability that has a direction points with: the two frames are the
    # empty track and the charged fill, and the fill is drawn clipped to however wound up the throw
    # is. A strip rather than two cells precisely because the fill sits on top of the track, and
    # only a set cut with one rectangle guarantees they line up: trimmed to their own bounds the
    # two arrows differ by two pixels horizontally, which is a fill that starts inside its own
    # track and a head that never quite reaches the end of it.
    #
    # Two rows on the sheet, the same arrow twice at slightly different proportions, and the top one
    # is the one taken. The bottom pair carries the generator's watermark inside the orange head,
    # where a patch would have to invent artwork rather than copy dirt from further along; taking
    # the clean row costs nothing, since the rows are the same drawing.
    @{ From = 'charge arrow.png'; Key = 'green'; Pack = 'strip'; Into = 'glyph'; Name = 'arrow'
       Grid = @(2, 2); Frames = 2; Scale = 0.30; Open = 3 }

    # ---- The app icon -----------------------------------------------------------------
    #
    # Keyed off nothing and opened at nothing, because unlike everything else here this one is
    # meant to keep its background: the brown it is drawn on is the icon's own colour, and it is
    # what the Android background layer is filled with. Watermarked in the bottom corner, where
    # the surrounding brown is flat, so the patch has an easy time of it.
    @{ From = 'app icon.png'; Key = 'none'; Pack = 'icon'; Into = 'icon'; Name = 'app'
       Scale = 1.0; Open = 0
       Watermark = @{ X = 872; Y = 872; Width = 64; Height = 64 } }
)

# ---- The work -------------------------------------------------------------------------

function Get-CellRect([hashtable] $sheet, [int] $width, [int] $height, [int] $index) {
    $columns = if ($sheet.ContainsKey('Grid')) { $sheet.Grid[0] } else { 1 }
    $rows = if ($sheet.ContainsKey('Grid')) { $sheet.Grid[1] } else { 1 }

    $cellWide = [int] ($width / $columns)
    $cellTall = [int] ($height / $rows)

    # Floored explicitly: a PowerShell [int] cast rounds, so cell three of four came out in row
    # one and cell six ran off the end of the sheet.
    $column = $index % $columns
    $row = [int] [Math]::Floor($index / $columns)

    $crop = if ($sheet.ContainsKey('Crop')) { $sheet.Crop } else { @(0, 0, 0, 0) }

    return @{
        X = ($column * $cellWide) + $crop[0]
        Y = ($row * $cellTall) + $crop[1]
        Width = $cellWide - $crop[0] - $crop[2]
        Height = $cellTall - $crop[1] - $crop[3]
    }
}

function Write-Sheet(
    [hashtable] $sheet, [byte[]] $bytes, [int] $width, [int] $height,
    [string] $prefix, [string] $outputRoot, [bool] $apply) {

    $into = if ($sheet.ContainsKey('Into')) { $sheet.Into } else { '' }
    $scale = $sheet.Scale
    $premultiply = $scale -ne 1.0

    if ($sheet.Pack -eq 'icon') {
        Write-Icons $sheet $bytes $width $height $outputRoot $apply

        return
    }

    if ($sheet.Pack -eq 'cells') {
        for ($index = 0; $index -lt $sheet.Names.Count; $index++) {
            $cell = Get-CellRect $sheet $width $height $index
            $bounds = [MolehillArt]::Bounds($bytes, $width, $cell.X, $cell.Y, $cell.Width, $cell.Height)

            if (-not $bounds) {
                Write-Warning ("cell {0} of {1} is empty" -f $index, $sheet.From)
                continue
            }

            $name = $prefix + $sheet.Names[$index]
            Write-Host ("cell {0,-16} {1}x{2}" -f $name, $bounds[2], $bounds[3])

            if (-not $apply) { continue }

            $cut = [MolehillArt]::Strip(
                $bytes, $width, $height, @($bounds[0]), @($bounds[1]), $bounds[2], $bounds[3],
                $premultiply)

            $leaf = if ($into) { "$into/$name.png" } else { "$name.png" }
            $where = Join-Path $outputRoot $leaf
            $size = Save-Png $cut $bounds[2] $bounds[3] $premultiply $scale $where
            Write-Host ("     -> {0}  {1}x{2}" -f (Resolve-Path -Relative $where), $size.Wide, $size.Tall)
        }

        return
    }

    # One frame for a whole image, otherwise as many as the manifest says.
    $frames = if ($sheet.Pack -eq 'whole') { 1 } else { $sheet.Frames }

    $originX = New-Object 'int[]' $frames
    $originY = New-Object 'int[]' $frames

    # The union of what every frame occupies, in cell coordinates. Cutting all of them with one
    # rectangle is what keeps an animation still: trimmed to its own bounds, each frame's
    # bounding box becomes its origin and the mole jitters as it plays.
    $left = [int]::MaxValue
    $top = [int]::MaxValue
    $right = [int]::MinValue
    $bottom = [int]::MinValue

    for ($frame = 0; $frame -lt $frames; $frame++) {
        $cell = Get-CellRect $sheet $width $height $frame
        $bounds = [MolehillArt]::Bounds($bytes, $width, $cell.X, $cell.Y, $cell.Width, $cell.Height)

        if (-not $bounds) {
            Write-Warning ("frame {0} of {1} is empty" -f $frame, $sheet.From)
            continue
        }

        $left = [Math]::Min($left, $bounds[0] - $cell.X)
        $top = [Math]::Min($top, $bounds[1] - $cell.Y)
        $right = [Math]::Max($right, $bounds[0] - $cell.X + $bounds[2])
        $bottom = [Math]::Max($bottom, $bounds[1] - $cell.Y + $bounds[3])
    }

    if ($right -le $left -or $bottom -le $top) {
        Write-Warning ("{0} has nothing in it" -f $sheet.From)
        return
    }

    for ($frame = 0; $frame -lt $frames; $frame++) {
        $cell = Get-CellRect $sheet $width $height $frame
        $originX[$frame] = $cell.X + $left
        $originY[$frame] = $cell.Y + $top
    }

    $wide = $right - $left
    $tall = $bottom - $top
    $name = $prefix + $sheet.Name

    Write-Host ("set  {0,-16} {1} frames of {2}x{3}" -f $name, $frames, $wide, $tall)

    if (-not $apply) { return }

    $strip = [MolehillArt]::Strip($bytes, $width, $height, $originX, $originY, $wide, $tall, $premultiply)
    $leaf = if ($into) { "$into/$name.png" } else { "$name.png" }
    $where = Join-Path $outputRoot $leaf
    $size = Save-Png $strip ($wide * $frames) $tall $premultiply $scale $where
    Write-Host ("     -> {0}  {1}x{2}" -f (Resolve-Path -Relative $where), $size.Wide, $size.Tall)
}

$total = 0

foreach ($sheet in $sheets) {
    if ($Only -and ($sheet.From -notlike $Only)) {
        continue
    }

    $path = Join-Path $Source $sheet.From

    if (-not (Test-Path $path)) {
        throw "No sheet at $path"
    }

    Write-Host ""
    Write-Host $sheet.From

    $raster = Read-Raster $path
    $width = $raster.Width
    $height = $raster.Height
    $bytes = $raster.Bytes

    Write-Host ("sheet   {0}x{1}" -f $width, $height)

    # Every pixel opaque to begin with. The sources have no alpha channel and a locked 32bpp read
    # leaves it zero, which would make the whole sheet invisible.
    [MolehillArt]::Opaque($bytes)

    if ($sheet.ContainsKey('Watermark')) {
        $where = $sheet.Watermark

        # Ordinary dirt in the same rows, which is about what the patched rectangle should end up
        # scoring. Three rectangles to the left, so it is the same strata and not the same pixels.
        $controlX = $where.X - ($where.Width * 3)

        Write-Host ("lift    watermark {0:N1}, elsewhere in the same rows {1:N1}" -f
            [MolehillArt]::Lift($bytes, $width, $where.X, $where.Y, $where.Width, $where.Height),
            [MolehillArt]::Lift($bytes, $width, $controlX, $where.Y, $where.Width, $where.Height))

        $offset = [MolehillArt]::Offset(
            $bytes, $width, $height, $where.X, $where.Y, $where.Width, $where.Height, $feather)

        if (-not $offset) {
            throw 'No usable patch offset. The watermark rectangle is too close to an edge.'
        }

        Write-Host ("patch   offset {0},{1}  border mismatch {2:N2}  brightest copied pixel {3:N2}" -f
            $offset[0], $offset[1], $offset[2], $offset[3])

        if (-not $Report) {
            [MolehillArt]::Patch(
                $bytes, $width, $height, $where.X, $where.Y, $where.Width, $where.Height,
                $feather, [int] $offset[0], [int] $offset[1])

            Write-Host ("lift    patched {0:N1}, elsewhere in the same rows {1:N1}" -f
                [MolehillArt]::Lift($bytes, $width, $where.X, $where.Y, $where.Width, $where.Height),
                [MolehillArt]::Lift($bytes, $width, $controlX, $where.Y, $where.Width, $where.Height))
        }
    }

    if ($sheet.ContainsKey('Horizon')) {
        $row = [MolehillArt]::Horizon($bytes, $width, $height)

        if ($row -lt 0) {
            Write-Warning 'No horizon found. The client''s anchor fraction will not match this art.'
        }
        else {
            Write-Host ("horizon row {0} of {1}, which is {2:N4} of the way down" -f
                $row, $height, ($row / [double] $height))
        }
    }

    $kind = $keyKinds[$sheet.Key]

    if ($kind -ne [MolehillArt]::KeyNone) {
        $counts = [MolehillArt]::Key($bytes, $kind)
        Write-Host ("keyed   dropped {0}  edges {1}" -f $counts[0], $counts[1])
    }

    if ($sheet.Open -gt 0) {
        $removed = [MolehillArt]::Open($bytes, $width, $height, $sheet.Open)
        Write-Host ("opened  dropped {0} pixels of anything thinner than {1}" -f $removed, $sheet.Open)
    }

    if ($sheet.ContainsKey('Platoons') -and $sheet.Platoons) {
        $garment = [MolehillArt]::Garment($bytes)

        Write-Host ("trunks  {0} pixels, average lightness {1:N3}" -f
            [int] $garment[1], $garment[0])

        if ($garment[1] -lt 1000) {
            Write-Warning 'Hardly any trunk pixels. The hue and saturation thresholds do not match this art.'
        }

        foreach ($seat in $seats) {
            $recoloured = [MolehillArt]::Recolour($bytes, $seat.R, $seat.G, $seat.B, $garment[0])
            Write-Sheet $sheet $recoloured $width $height ($seat.Name + '-') $OutputDirectory (-not $Report)
            $total++
        }
    }
    else {
        Write-Sheet $sheet $bytes $width $height '' $OutputDirectory (-not $Report)
        $total++
    }
}

Write-Host ""
Write-Host $(if ($Report) {
    "Reported only. Nothing written. $total sheets would go to $OutputDirectory"
}
else {
    "Done. $total sheets written to $OutputDirectory"
})
