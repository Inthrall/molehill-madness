<#
.SYNOPSIS
    Turns the generated art sheets into the terrain textures and backdrop decor the client loads.

.DESCRIPTION
    Four jobs, none of which the game should be doing at runtime.

    The generator's watermark goes. Two of the three terrain images carry a small four-pointed
    star in the bottom right corner. On a texture that tiles across a sixty metre map that
    sparkle would appear in a grid, which reads as a rendering fault rather than as dirt, so it
    is patched out here where it can be looked at rather than keyed out in a shader where it
    cannot. The patch is a copy of the same rows from further along the texture, chosen by trying
    a spread of offsets and keeping whichever one matches the border of the hole best, then
    feathered in. That matters because this art's dirt is banded: a patch taken from the wrong
    offset puts a stratum line at the wrong height and the seam is more obvious than the star.

    The magenta goes, becoming transparency. Same argument as the mole sheet: keyed at runtime
    every antialiased edge keeps a magenta fringe, so edge pixels get their magenta spill removed
    and a partial alpha instead of being thrown away.

    The decor sheets are cut into their cells and trimmed. Both are a four by two grid with every
    sprite in a row sharing a baseline, which is what lets the game plant a tuft on the ground by
    its own bottom edge without a table of offsets.

    Every cell of both sheets is scaled by the same factor, so their sizes stay in proportion to
    one another. The game turns output pixels into metres with a single constant, and a molehill
    is smaller than a mole because it was drawn smaller, not because anything here decided so.

    Run once when the source art changes. The outputs are committed, because a build should not
    depend on a machine having an imaging library.

.PARAMETER Source
    The directory the generated sheets are in.

.PARAMETER OutputDirectory
    Where the textures go. Defaults to client/art beside this repo.

.PARAMETER Report
    Say what would be written, and what the measurements came out as, without writing anything.
    Worth running first on new art: the horizon fraction and the watermark offsets it prints are
    the two numbers the client has hard-coded, and if the art has moved they need moving with it.

.EXAMPLE
    pwsh tools/scripts/import-art.ps1 -Source art -Report

.EXAMPLE
    pwsh tools/scripts/import-art.ps1 -Source art
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Source,

    [string] $OutputDirectory,

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

# ---- Pixel access ---------------------------------------------------------------------
#
# LockBits rather than GetPixel. The mole sheet script walks a megapixel with GetPixel and
# takes the best part of a minute over it; this one has six megapixels to get through and the
# same approach would make it something nobody runs. The layout is BGRA, one byte a channel.

class Raster {
    [int] $Width
    [int] $Height
    [byte[]] $Bytes

    Raster([int] $width, [int] $height) {
        $this.Width = $width
        $this.Height = $height
        $this.Bytes = New-Object 'byte[]' ($width * $height * 4)
    }

    [int] Offset([int] $x, [int] $y) {
        return (($y * $this.Width) + $x) * 4
    }
}

function Read-Raster([string] $path) {
    $bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path $path))

    try {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $locked = $bitmap.LockBits(
            $rect,
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

        try {
            $raster = [Raster]::new($bitmap.Width, $bitmap.Height)

            # Row by row, because a locked bitmap's stride is not necessarily its width.
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                [System.Runtime.InteropServices.Marshal]::Copy(
                    [System.IntPtr]::Add($locked.Scan0, $y * $locked.Stride),
                    $raster.Bytes,
                    $y * $bitmap.Width * 4,
                    $bitmap.Width * 4)
            }

            return $raster
        }
        finally {
            $bitmap.UnlockBits($locked)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function New-BitmapFrom([Raster] $raster, [bool] $premultiplied) {
    $format = if ($premultiplied) {
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb
    }
    else {
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    }

    $bitmap = New-Object System.Drawing.Bitmap $raster.Width, $raster.Height, $format
    $rect = New-Object System.Drawing.Rectangle 0, 0, $raster.Width, $raster.Height
    $locked = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $format)

    try {
        for ($y = 0; $y -lt $raster.Height; $y++) {
            [System.Runtime.InteropServices.Marshal]::Copy(
                $raster.Bytes,
                $y * $raster.Width * 4,
                [System.IntPtr]::Add($locked.Scan0, $y * $locked.Stride),
                $raster.Width * 4)
        }
    }
    finally {
        $bitmap.UnlockBits($locked)
    }

    return $bitmap
}

function Save-Raster([Raster] $raster, [string] $path) {
    $bitmap = New-BitmapFrom $raster $false

    try {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Host ("wrote   {0}  {1}x{2}" -f (Resolve-Path -Relative $path), $raster.Width, $raster.Height)
}

# ---- The generator's watermark ---------------------------------------------------------
#
# Where the star is in each sheet, found by blurring the corner and looking for the one
# compact blob that stands above its surroundings. Hard-coded rather than detected here,
# because detection on a texture full of light pebbles finds pebbles too, and a tool that
# quietly patches out a pebble is worse than one that has to be told. -Report prints how
# much lift is left inside each rectangle afterwards, which is the check that it worked.

$watermarks = @{
    'background underground.png' = @{ X = 872; Y = 872; Width = 64; Height = 64 }
    'background surface.png'     = @{ X = 1308; Y = 570; Width = 60; Height = 60 }
}

# How wide a band around the hole the patch fades in over. Wide enough that a small
# mismatch in the copied dirt is spread out rather than landing on a line.
$feather = 24

function Repair-Watermark([Raster] $raster, [hashtable] $where, [bool] $apply) {
    $left = $where.X - $feather
    $top = $where.Y - $feather
    $right = $where.X + $where.Width + $feather
    $bottom = $where.Y + $where.Height + $feather

    # Offsets to try. Never less than the hole's own width, or the patch would be copying
    # part of the star back over itself.
    $best = $null
    $bestScore = [double]::MaxValue

    foreach ($dx in @(-384, -360, -336, -312, -288, -264, -240, -216, -192, -168, -144, -120, -96,
                      96, 120, 144, 168, 192, 216, 240, 264, 288, 312, 336, 360, 384)) {
        foreach ($dy in @(-8, -4, 0, 4, 8)) {
            if ($left + $dx -lt 0 -or $right + $dx -ge $raster.Width) { continue }
            if ($top + $dy -lt 0 -or $bottom + $dy -ge $raster.Height) { continue }

            $mismatch = 0.0
            $edgeSamples = 0
            $interior = New-Object 'System.Collections.Generic.List[double]'

            for ($y = $top; $y -lt $bottom; $y += 2) {
                for ($x = $left; $x -lt $right; $x += 2) {
                    $inside = $x -ge $where.X -and $x -lt $where.X + $where.Width -and
                              $y -ge $where.Y -and $y -lt $where.Y + $where.Height

                    $there = $raster.Offset($x + $dx, $y + $dy)

                    if ($inside) {
                        # What the patch would put in the hole. Kept so a flat piece of dirt can
                        # be preferred over one with a pebble in it.
                        $interior.Add((
                            [int] $raster.Bytes[$there] +
                            [int] $raster.Bytes[$there + 1] +
                            [int] $raster.Bytes[$there + 2]) / 3.0)
                        continue
                    }

                    # The fade band, which is the part that has to agree with what is already
                    # there, so it is what the offset is chosen on.
                    $here = $raster.Offset($x, $y)

                    $mismatch += [Math]::Abs([int] $raster.Bytes[$here] - [int] $raster.Bytes[$there])
                    $mismatch += [Math]::Abs([int] $raster.Bytes[$here + 1] - [int] $raster.Bytes[$there + 1])
                    $mismatch += [Math]::Abs([int] $raster.Bytes[$here + 2] - [int] $raster.Bytes[$there + 2])
                    $edgeSamples++
                }
            }

            if ($edgeSamples -eq 0 -or $interior.Count -eq 0) { continue }

            $mismatch = $mismatch / $edgeSamples

            # The brightest thing in the copied dirt, measured against its own middle. What is
            # being hidden is a soft light blob, so a patch with a light pebble in it swaps one
            # bright spot for another and the check afterwards cannot tell them apart. Scoring
            # the same quantity the check reports is what makes that check mean something.
            $sorted = $interior.ToArray()
            [System.Array]::Sort($sorted)
            $peak = $sorted[$sorted.Length - 1] - $sorted[[int] [Math]::Floor($sorted.Length / 2)]
            $score = $mismatch + ($peak * 0.25)

            if ($score -lt $bestScore) {
                $bestScore = $score
                $best = @{ Dx = $dx; Dy = $dy; Mismatch = $mismatch; Peak = $peak }
            }
        }
    }

    if (-not $best) {
        throw 'No usable patch offset. The watermark rectangle is too close to an edge.'
    }

    Write-Host ("patch   offset {0},{1}  border mismatch {2:N2}  brightest copied pixel {3:N2}" -f
        $best.Dx, $best.Dy, $best.Mismatch, $best.Peak)

    if (-not $apply) {
        return
    }

    # Copied out of the original, not out of the raster being written, so that a patch never
    # reads pixels it has already changed.
    $from = [Raster]::new($raster.Width, $raster.Height)
    [System.Array]::Copy($raster.Bytes, $from.Bytes, $raster.Bytes.Length)

    for ($y = $top; $y -lt $bottom; $y++) {
        for ($x = $left; $x -lt $right; $x++) {
            # How far outside the hole this pixel is, in each direction.
            $out = [Math]::Max(
                [Math]::Max($where.X - $x, $x - ($where.X + $where.Width - 1)),
                [Math]::Max($where.Y - $y, $y - ($where.Y + $where.Height - 1)))

            if ($out -ge $feather) { continue }

            $weight = if ($out -le 0) { 1.0 } else { 1.0 - ([double] $out / $feather) }

            # Smoothed, so the join has no visible shoulder.
            $weight = $weight * $weight * (3.0 - (2.0 * $weight))

            $here = $raster.Offset($x, $y)
            $there = $from.Offset($x + $best.Dx, $y + $best.Dy)

            for ($channel = 0; $channel -lt 3; $channel++) {
                $mixed = ((1.0 - $weight) * [int] $from.Bytes[$here + $channel]) +
                         ($weight * [int] $from.Bytes[$there + $channel])
                $raster.Bytes[$here + $channel] = [byte] [Math]::Round($mixed)
            }
        }
    }
}

function Measure-Lift([Raster] $raster, [hashtable] $where) {
    # The most any pixel in the rectangle stands above the median of its own rows.
    #
    # Read against the control rectangle rather than on its own. A light pebble stands above
    # its rows every bit as much as a watermark does, so a patched region full of pebbles
    # scores higher than the star did and means nothing by itself. What says the star has gone
    # is the patched rectangle scoring about what ordinary dirt in the same rows scores.
    $rows = New-Object 'System.Collections.Generic.List[double]'
    $peak = 0.0

    for ($y = $where.Y; $y -lt $where.Y + $where.Height; $y++) {
        $rows.Clear()

        for ($x = 0; $x -lt $raster.Width; $x++) {
            $at = $raster.Offset($x, $y)
            $rows.Add(([int] $raster.Bytes[$at] + [int] $raster.Bytes[$at + 1] + [int] $raster.Bytes[$at + 2]) / 3.0)
        }

        $sorted = $rows.ToArray()
        [System.Array]::Sort($sorted)
        $median = $sorted[[int] ($sorted.Length / 2)]

        for ($x = $where.X; $x -lt $where.X + $where.Width; $x++) {
            $at = $raster.Offset($x, $y)
            $here = ([int] $raster.Bytes[$at] + [int] $raster.Bytes[$at + 1] + [int] $raster.Bytes[$at + 2]) / 3.0

            if ($here - $median -gt $peak) { $peak = $here - $median }
        }
    }

    return $peak
}

# ---- Chroma key -----------------------------------------------------------------------
#
# The same shape of test the mole sheet uses, turned to magenta: keyness is how far the two
# key channels stand above the third, which catches the dark magenta in a shadowed edge and
# ignores the artwork's own colours, none of which are magenta-dominant. "Close to pure
# magenta" would leave a fringe on every antialiased blade of grass.

$fullyKeyedAbove = 60
$noKeyBelow = 12

function Remove-Magenta([Raster] $raster) {
    $dropped = 0
    $feathered = 0

    for ($index = 0; $index -lt $raster.Bytes.Length; $index += 4) {
        # BGRA.
        $blue = [int] $raster.Bytes[$index]
        $green = [int] $raster.Bytes[$index + 1]
        $red = [int] $raster.Bytes[$index + 2]

        $keyness = [Math]::Min($red, $blue) - $green

        if ($keyness -ge $fullyKeyedAbove) {
            $raster.Bytes[$index] = 0
            $raster.Bytes[$index + 1] = 0
            $raster.Bytes[$index + 2] = 0
            $raster.Bytes[$index + 3] = 0
            $dropped++
            continue
        }

        if ($keyness -le $noKeyBelow) {
            $raster.Bytes[$index + 3] = 255
            continue
        }

        $alpha = 1.0 - ([double] ($keyness - $noKeyBelow) / ($fullyKeyedAbove - $noKeyBelow))
        $feathered++

        # Take the magenta spill off, or every blade of grass gets a pink rim against the sky.
        $spill = ($keyness) * (1.0 - $alpha)
        $raster.Bytes[$index] = [byte] [Math]::Max(0.0, $blue - $spill)
        $raster.Bytes[$index + 2] = [byte] [Math]::Max(0.0, $red - $spill)
        $raster.Bytes[$index + 3] = [byte] [Math]::Round(255 * $alpha)
    }

    Write-Host ("keyed   dropped {0}  edges {1}" -f $dropped, $feathered)
}

# ---- Cutting the decor sheets ----------------------------------------------------------
#
# Both sheets are four cells across and two down, with every sprite in a row standing on the
# same baseline, so a trimmed sprite's bottom edge is the ground it stands on and the game
# needs no table of anchor offsets.

$sheetColumns = 4
$sheetRows = 2

# Every cell of both sheets is scaled by this, so their sizes stay in proportion. Half, which
# puts the tallest sprite on the sheets at about a hundred and sixty pixels: enough for the
# closest the camera ever gets, and small enough that sixteen of them are a rounding error in
# the repository.
$decorScale = 0.5

# What a trimmed pixel counts as. Above nothing, so a stray keyed edge pixel does not pad a
# sprite out with a transparent margin the game would then plant it on.
$opaqueEnough = 8

function Get-ContentBounds([Raster] $raster, [int] $cellX, [int] $cellY, [int] $cellWidth, [int] $cellHeight) {
    $left = [int]::MaxValue
    $top = [int]::MaxValue
    $right = [int]::MinValue
    $bottom = [int]::MinValue

    for ($y = $cellY; $y -lt $cellY + $cellHeight; $y++) {
        for ($x = $cellX; $x -lt $cellX + $cellWidth; $x++) {
            if ([int] $raster.Bytes[$raster.Offset($x, $y) + 3] -lt $opaqueEnough) { continue }

            if ($x -lt $left) { $left = $x }
            if ($x -gt $right) { $right = $x }
            if ($y -lt $top) { $top = $y }
            if ($y -gt $bottom) { $bottom = $y }
        }
    }

    if ($right -lt $left) {
        return $null
    }

    return @{ X = $left; Y = $top; Width = $right - $left + 1; Height = $bottom - $top + 1 }
}

function Save-Sprite([Raster] $raster, [hashtable] $bounds, [string] $path) {
    # Premultiplied on the way in, because a bicubic shrink that interpolates straight alpha
    # drags the colour of fully transparent pixels into every edge, and this art is outlined in
    # near-black, so the result is a dark halo round each sprite against a cream sky.
    $cut = [Raster]::new($bounds.Width, $bounds.Height)

    for ($y = 0; $y -lt $bounds.Height; $y++) {
        for ($x = 0; $x -lt $bounds.Width; $x++) {
            $from = $raster.Offset($bounds.X + $x, $bounds.Y + $y)
            $to = $cut.Offset($x, $y)
            $alpha = [int] $raster.Bytes[$from + 3]

            for ($channel = 0; $channel -lt 3; $channel++) {
                $cut.Bytes[$to + $channel] = [byte] (([int] $raster.Bytes[$from + $channel] * $alpha) / 255)
            }

            $cut.Bytes[$to + 3] = [byte] $alpha
        }
    }

    $wide = [Math]::Max(1, [int] [Math]::Round($bounds.Width * $decorScale))
    $tall = [Math]::Max(1, [int] [Math]::Round($bounds.Height * $decorScale))

    $source = New-BitmapFrom $cut $true
    $target = New-Object System.Drawing.Bitmap $wide, $tall,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $canvas = [System.Drawing.Graphics]::FromImage($target)

        try {
            $canvas.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $canvas.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # Clamped rather than wrapped, or bicubic samples off the left edge of a sprite and
            # brings the right edge back with it.
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

            $where = New-Object System.Drawing.Rectangle 0, 0, $wide, $tall
            $canvas.DrawImage($source, $where, 0, 0, $bounds.Width, $bounds.Height,
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

    Write-Host ("wrote   {0}  {1}x{2}" -f (Resolve-Path -Relative $path), $wide, $tall)
}

function Measure-Horizon([Raster] $raster) {
    # Where the panorama's own foreground begins, which is the row the bright line along the top
    # of its near ground sits on. The client anchors that row to the map's surface, so this is the
    # one number about this image the game has to agree with.
    $previous = -1.0

    for ($y = [int] ($raster.Height / 2); $y -lt $raster.Height; $y++) {
        $total = 0.0

        for ($x = 0; $x -lt $raster.Width; $x++) {
            $at = $raster.Offset($x, $y)
            $total += [int] $raster.Bytes[$at] + [int] $raster.Bytes[$at + 1] + [int] $raster.Bytes[$at + 2]
        }

        $mean = $total / (3.0 * $raster.Width)

        if ($previous -ge 0 -and $mean - $previous -gt 8) {
            return @{ Row = $y; Fraction = [double] $y / $raster.Height }
        }

        $previous = $mean
    }

    return $null
}

# ---- The work -------------------------------------------------------------------------

# Source sheet to output texture. The three terrain images are full-bleed rather than keyed:
# they are ground, not sprites standing on it, so there is no background to pull out.
$tiles = @(
    @{ From = 'foreground underground.jpg'; To = 'terrain-dirt.png' }
    @{ From = 'background underground.png'; To = 'terrain-deep.png' }
    @{ From = 'background surface.png'; To = 'backdrop-surface.png' }
)

foreach ($tile in $tiles) {
    $path = Join-Path $Source $tile.From

    if (-not (Test-Path $path)) {
        throw "No sheet at $path"
    }

    Write-Host ""
    Write-Host $tile.From

    $raster = Read-Raster $path
    Write-Host ("sheet   {0}x{1}" -f $raster.Width, $raster.Height)

    if ($watermarks.ContainsKey($tile.From)) {
        $where = $watermarks[$tile.From]

        # Ordinary dirt in the same rows, which is what the patched rectangle should end up
        # scoring. Three rectangles to the left, so it is the same strata and not the same pixels.
        $control = @{
            X = $where.X - ($where.Width * 3)
            Y = $where.Y
            Width = $where.Width
            Height = $where.Height
        }

        Write-Host ("lift    watermark {0:N1}, elsewhere in the same rows {1:N1}" -f
            (Measure-Lift $raster $where), (Measure-Lift $raster $control))

        Repair-Watermark $raster $where (-not $Report)

        if (-not $Report) {
            Write-Host ("lift    patched {0:N1}, elsewhere in the same rows {1:N1}" -f
                (Measure-Lift $raster $where), (Measure-Lift $raster $control))
        }
    }

    if ($tile.To -eq 'backdrop-surface.png') {
        $horizon = Measure-Horizon $raster

        if ($horizon) {
            Write-Host ("horizon row {0} of {1}, which is {2:N4} of the way down" -f
                $horizon.Row, $raster.Height, $horizon.Fraction)
        }
        else {
            Write-Warning 'No horizon found. The client''s anchor fraction will not match this art.'
        }
    }

    # Every pixel opaque. The source has no alpha channel and a locked 32bpp read leaves it
    # zero, which would make the whole texture invisible.
    for ($index = 3; $index -lt $raster.Bytes.Length; $index += 4) {
        $raster.Bytes[$index] = 255
    }

    if (-not $Report) {
        Save-Raster $raster (Join-Path $OutputDirectory $tile.To)
    }
}

# The decor sheets, cut into their cells. Named per cell where the cells are different things
# and numbered where they are variants of one thing, because a caller picking a tuft at random
# wants an index and a caller wanting a molehill wants a molehill.
$sheets = @(
    @{ From = 'grass background.png'
       Names = @('grass-0', 'grass-1', 'grass-2', 'grass-3', 'grass-4', 'grass-5', 'grass-6', 'grass-7') }
    @{ From = 'grass background decor.png'
       Names = @('molehill-small', 'molehill', 'worm', 'flowers',
                 'flower', 'dandelion', 'snowdrops', 'stone') }
)

foreach ($sheet in $sheets) {
    $path = Join-Path $Source $sheet.From

    if (-not (Test-Path $path)) {
        throw "No sheet at $path"
    }

    Write-Host ""
    Write-Host $sheet.From

    $raster = Read-Raster $path
    Write-Host ("sheet   {0}x{1}" -f $raster.Width, $raster.Height)

    Remove-Magenta $raster

    $cellWidth = [int] ($raster.Width / $sheetColumns)
    $cellHeight = [int] ($raster.Height / $sheetRows)

    for ($cell = 0; $cell -lt $sheet.Names.Count; $cell++) {
        $column = $cell % $sheetColumns
        # Floored explicitly: a PowerShell [int] cast rounds, so cell three of four came out
        # in row one and cell six ran off the end of the sheet.
        $row = [int] [Math]::Floor($cell / $sheetColumns)

        $bounds = Get-ContentBounds $raster ($column * $cellWidth) ($row * $cellHeight) $cellWidth $cellHeight

        if (-not $bounds) {
            Write-Warning ("cell {0} of {1} is empty" -f $cell, $sheet.From)
            continue
        }

        Write-Host ("cell {0,-14} {1}x{2} at {3},{4}" -f
            $sheet.Names[$cell], $bounds.Width, $bounds.Height, $bounds.X, $bounds.Y)

        if (-not $Report) {
            Save-Sprite $raster $bounds (Join-Path $OutputDirectory ("decor/" + $sheet.Names[$cell] + ".png"))
        }
    }
}

Write-Host ""
Write-Host $(if ($Report) { 'Reported only. Nothing written.' } else { "Done. Output in $OutputDirectory" })
