<#
.SYNOPSIS
    Rebuilds 'art/explosion regrid.png' from 'art/explosion.png', which cannot be imported as it
    stands.

.DESCRIPTION
    The sheet as it arrived is two rows on different pitches: five cells of 291 across the top and
    three of 485 across the bottom, each with its own faint borders. import-art.ps1 assumes one
    uniform grid, and there is no grid that fits both rows, so the seven drawings have to be lifted
    off individually and laid out again.

    Two things make that work. Each window is centred on its own drawing rather than on the cell
    the drawing sits in, because the drawings are not centred in their cells: their middles drift
    from y 174 to y 195 down the top row, and a window cut on the cell keeps that drift, which puts
    the blast in a different place in every frame and makes the explosion wander as it plays.

    What counts as the middle is not the bounding box. The box takes in the rocks thrown clear of
    the fire, and they are not thrown evenly, so on the frames carrying the most debris the box
    centre sits well off the ring the explosion is actually made of. For the five frames that are a
    ring, the centre is the centroid of the fire, which colour separates cleanly: the ramp runs dark
    red to yellow and is strongly saturated, where the rocks are grey and the outlines black.

    The last two frames have no ring left, only embers and falling debris, so neither rule fits
    them: the fire is a handful of surviving pixels nowhere near the middle, and the bounding box is
    pulled about by whichever rock flew furthest. Those two are centred on the weight of everything
    drawn instead, which puts the cloud's own middle where the ring's middle was. The artist drew
    the debris bunched to one side in each of them and differently in the two, so left on their
    boxes they threw the picture eleven pixels one way and ten the other, a fifth of the frame
    between consecutive frames, and that is what made the explosion lurch as it faded.

    And each window is masked to the cell its drawing came from. The windows have to be wider than
    a top-row cell, because the bottom row's ring is 343 across and the importer cuts every frame
    with one rectangle, so a window centred on a top-row drawing reaches into its neighbour. Every
    drawing is wholly inside its own cell, so anything outside that cell belongs to somebody else
    and is painted out.

    The centres are measured off the artwork rather than assumed. Assuming a layout is what put the
    ring through a cell boundary in the first place.

    Run this when the source sheet changes, then import-art.ps1 -Only 'explosion regrid.png'.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\..\art\explosion.png'),
    [string] $Out = (Join-Path $PSScriptRoot '..\..\art\explosion regrid.png'),
    [int] $Wide = 380,
    [int] $Tall = 380
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Centre is the blast origin, measured off the sheet: the centroid of the fire where there is a ring
# to take it from, and the middle of everything drawn on the two frames where there is not. Cell is
# the rectangle the drawing was drawn in, which is the mask. Listed in playing order, which is not
# the order they sit in: the bottom row's ring carries more fire than the top row's last frame and
# belongs before it, not after.
$frames = @(
    @{ Name = 'flash';     CentreX =  146; CentreY = 173; Cell = @(   0,   0, 291, 360) }
    @{ Name = 'fireball';  CentreX =  438; CentreY = 179; Cell = @( 291,   0, 291, 360) }
    @{ Name = 'ring';      CentreX =  727; CentreY = 184; Cell = @( 582,   0, 291, 360) }
    @{ Name = 'breaking';  CentreX = 1022; CentreY = 194; Cell = @( 873,   0, 291, 360) }
    @{ Name = 'arcs';      CentreX =  236; CentreY = 545; Cell = @(   0, 360, 485, 360) }
    @{ Name = 'fragments'; CentreX = 1290; CentreY = 226; Cell = @(1164,   0, 292, 360) }
    @{ Name = 'debris';    CentreX =  755; CentreY = 571; Cell = @( 485, 360, 485, 360) }
)

$columns = 4
$rows = [int] [Math]::Ceiling($frames.Count / $columns)

$sheet = [System.Drawing.Bitmap]::new((Resolve-Path $Source).Path)
$key = [System.Drawing.Color]::FromArgb(255, 41, 230, 39)

$regrid = [System.Drawing.Bitmap]::new($Wide * $columns, $Tall * $rows)
$canvas = [System.Drawing.Graphics]::FromImage($regrid)
$canvas.Clear($key)

for ($index = 0; $index -lt $frames.Count; $index++)
{
    $frame = $frames[$index]
    $cell = $frame.Cell

    $left = $frame.CentreX - [int]($Wide / 2)
    $top = $frame.CentreY - [int]($Tall / 2)

    $fromLeft = [Math]::Max($left, $cell[0])
    $fromTop = [Math]::Max($top, $cell[1])
    $fromRight = [Math]::Min($left + $Wide, $cell[0] + $cell[2])
    $fromBottom = [Math]::Min($top + $Tall, $cell[1] + $cell[3])

    $intoX = (($index % $columns) * $Wide) + ($fromLeft - $left)
    $intoY = ([int] [Math]::Floor($index / $columns) * $Tall) + ($fromTop - $top)

    $canvas.DrawImage(
        $sheet,
        [System.Drawing.Rectangle]::new(
            $intoX, $intoY, ($fromRight - $fromLeft), ($fromBottom - $fromTop)),
        [System.Drawing.Rectangle]::new(
            $fromLeft, $fromTop, ($fromRight - $fromLeft), ($fromBottom - $fromTop)),
        [System.Drawing.GraphicsUnit]::Pixel)

    "  {0,-10} centre {1,4},{2,3}  masked to {3},{4} {5}x{6}" -f `
        $frame.Name, $frame.CentreX, $frame.CentreY, $cell[0], $cell[1], $cell[2], $cell[3]
}

$canvas.Dispose()
$sheet.Dispose()

$full = [System.IO.Path]::GetFullPath($Out)
$regrid.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$regrid.Dispose()

"wrote {0} frames of {1}x{2} to {3}" -f $frames.Count, $Wide, $Tall, $full
