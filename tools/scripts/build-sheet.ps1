<#
.SYNOPSIS
    Lays loose frames out as a grid sheet on a flat key colour, ready for import-art.ps1.

.DESCRIPTION
    The importer takes sheets. Generators hand back frames, one file at a time or pulled out of a
    clip, and this is the step in between. It does as little as possible: a frame goes into its
    cell at its own size, and nothing is trimmed, recentred or recoloured, because every one of
    those is the importer's job and doing it twice is how an animation starts to wander.

    The background is the key colour the importer looks for, sampled off the sheets already in
    art/ rather than guessed, so a composed sheet keys exactly like a drawn one.

    Two checks are worth the trouble because both failures are silent. Frames that are not all
    the same size cannot be laid out without moving the subject, so the sizes are reported and
    mixed ones are called out. A frame that is opaque to its corners has no transparency to
    composite, which means it arrived with a background of its own and the key underneath it will
    never be seen: that is the usual way a clip from a video model fails here, and it fails
    looking almost right, as a sprite cut out with its own rectangle of sky attached.

.PARAMETER Frames
    The frame files, in the order they play. Ignored when -From is given.

.PARAMETER From
    A directory of frames, taken in name order. Zero-pad the numbers or frame 10 sorts before
    frame 2.

.PARAMETER Columns
    Cells across. The rows follow from the frame count.

.PARAMETER Key
    green, magenta or white. Whichever sits furthest from what is drawn.

.PARAMETER Report
    Say what would be written, and what the frames measured, without writing anything.
#>
[CmdletBinding(DefaultParameterSetName = 'Files')]
param(
    [Parameter(ParameterSetName = 'Files', Mandatory)]
    [string[]] $Frames,

    [Parameter(ParameterSetName = 'Directory', Mandatory)]
    [string] $From,

    [string] $Filter = '*.png',

    [Parameter(Mandatory)]
    [int] $Columns,

    [ValidateSet('green', 'magenta', 'white')]
    [string] $Key = 'green',

    [Parameter(Mandatory)]
    [string] $Out,

    [switch] $Report
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Sampled from art/explosion.png, art/grass background.png and art/lava floor.png. The importer
# keys on how far the key channels stand above the others rather than on an exact match, so these
# only have to be as saturated as what is already in the folder.
$keyColours = @{
    'green'   = [System.Drawing.Color]::FromArgb(255, 41, 230, 39)
    'magenta' = [System.Drawing.Color]::FromArgb(255, 254, 0, 249)
    'white'   = [System.Drawing.Color]::FromArgb(255, 254, 254, 254)
}

$paths = if ($PSCmdlet.ParameterSetName -eq 'Directory')
{
    Get-ChildItem -Path $From -Filter $Filter -File | Sort-Object Name | ForEach-Object { $_.FullName }
}
else
{
    $Frames | ForEach-Object { (Resolve-Path $_).Path }
}

if (-not $paths)
{
    throw 'No frames found.'
}

$rows = [int] [Math]::Ceiling($paths.Count / $Columns)
Write-Host ("{0} frames into {1}x{2}, keyed {3}" -f $paths.Count, $Columns, $rows, $Key)

$images = @()
$opaque = @()

foreach ($path in $paths)
{
    $image = [System.Drawing.Bitmap]::new($path)
    $images += $image

    # What matters is whether the importer will find a background here, and there are two ways
    # for that to be true: the frame is transparent and gets the key composited under it, or it
    # arrived already drawn on the key. Only a frame that is opaque AND not keyed is a problem,
    # so both are measured. Keyness is the importer's own measure, the margin the key channels
    # hold over the others.
    $keyed = $false
    $step = [Math]::Max(1, [int]($image.Width / 32))

    for ($x = 0; $x -lt $image.Width -and -not $keyed; $x += $step)
    {
        # Parenthesised because the comma binds tighter than the minus, so the obvious spelling
        # builds the array first and then tries to subtract one from it.
        foreach ($y in @(0, ($image.Height - 1)))
        {
            $pixel = $image.GetPixel($x, $y)

            if ($pixel.A -lt 250)
            {
                $keyed = $true
                break
            }

            $keyness = switch ($Key)
            {
                'green'   { $pixel.G - [Math]::Max($pixel.R, $pixel.B) }
                'magenta' { [Math]::Min($pixel.R, $pixel.B) - $pixel.G }
                'white'   { [Math]::Min([Math]::Min($pixel.R, $pixel.G), $pixel.B) - 200 }
            }

            if ($keyness -ge 60)
            {
                $keyed = $true
                break
            }
        }
    }

    if (-not $keyed)
    {
        $opaque += (Split-Path $path -Leaf)
    }

    Write-Host ("  {0,-28} {1}x{2}{3}" -f (Split-Path $path -Leaf), $image.Width, $image.Height,
        $(if ($keyed) { '' } else { '   no background to key' }))
}

# Cast, because Measure-Object hands back a double and the bitmap constructor binds a double pair
# to an overload that then refuses the arguments.
$cellWide = [int] ($images | ForEach-Object { $_.Width } | Measure-Object -Maximum).Maximum
$cellTall = [int] ($images | ForEach-Object { $_.Height } | Measure-Object -Maximum).Maximum

$sizes = $images | ForEach-Object { "$($_.Width)x$($_.Height)" } | Sort-Object -Unique
if ($sizes.Count -gt 1)
{
    Write-Warning ("frames are not all one size ({0}). Each one is centred in a {1}x{2} cell, which moves the subject between frames and is exactly what the union cut in import-art.ps1 cannot undo." -f ($sizes -join ', '), $cellWide, $cellTall)
}

if ($opaque.Count -eq $images.Count)
{
    Write-Warning "no frame has a background the importer can key: they are opaque to their edges and not drawn on $Key. They need generating against a flat $Key background, or cutting out before they get here. This is the usual way frames out of a video model fail."
}
elseif ($opaque.Count -gt 0)
{
    Write-Warning ("{0} of {1} frames have no background to key and will keep their own: {2}" -f $opaque.Count, $images.Count, ($opaque -join ', '))
}

Write-Host ("sheet {0}x{1}, cell {2}x{3}" -f ($cellWide * $Columns), ($cellTall * $rows), $cellWide, $cellTall)

if ($Report)
{
    $images | ForEach-Object { $_.Dispose() }
    Write-Host 'Reported only. Nothing written.'
    return
}

$sheet = [System.Drawing.Bitmap]::new($cellWide * $Columns, $cellTall * $rows)
$canvas = [System.Drawing.Graphics]::FromImage($sheet)
$canvas.Clear($keyColours[$Key])
$canvas.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor

for ($index = 0; $index -lt $images.Count; $index++)
{
    $image = $images[$index]
    $column = $index % $Columns
    $row = [int] [Math]::Floor($index / $Columns)

    # Drawn at its own size. Scaling here would resample artwork the importer is about to scale
    # again, and two resamples of a hard cartoon outline is one too many.
    $left = ($column * $cellWide) + [int](($cellWide - $image.Width) / 2)
    $top = ($row * $cellTall) + [int](($cellTall - $image.Height) / 2)
    $canvas.DrawImage($image, $left, $top, $image.Width, $image.Height)
}

$canvas.Dispose()
$images | ForEach-Object { $_.Dispose() }

$directory = Split-Path $Out -Parent
if ($directory -and -not (Test-Path $directory))
{
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()

Write-Host ("wrote {0}" -f (Resolve-Path $Out))
