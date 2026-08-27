<#
.SYNOPSIS
    Turns one green-screen mole sprite sheet into four platoon-coloured ones.

.DESCRIPTION
    Two jobs, both of which have to happen before the art is usable in the game.

    The green goes, becoming transparency. Keying it at runtime would leave a green fringe on every
    antialiased edge, which against this game's cream sky would read as a halo round every mole, so
    the edge pixels get their green spill removed and a partial alpha instead of being thrown away.

    The bandana is recoloured per platoon. Four platoons are told apart entirely by colour in this
    game, so one brown mole makes all four identical, and the bandana is the only part of the
    artwork that can carry a team colour without repainting the animal. Folds and highlights are
    kept: the pixel's own lightness is measured against the bandana's average and reapplied to the
    target colour, so a dark fold stays a dark fold.

    Run once when the source art changes. The outputs are committed, because a build should not
    depend on a machine having an imaging library.

.PARAMETER Source
    The green-screen sheet.

.PARAMETER OutputDirectory
    Where the four sheets go. Defaults to client/art beside this repo.

.PARAMETER Report
    Classify and print counts without writing anything. Worth running first on new art: if the
    bandana count is near zero or wildly large, the thresholds are wrong for it.

.EXAMPLE
    pwsh tools/scripts/recolour-moles.ps1 -Source art/mole-sheet.png -Report
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
    throw "No sheet at $Source"
}

# Palette.Seat, in order. These are the same four colours the panes, the plan markers, the
# broadcast captions and the tally all use, so a bandana matches the platoon it belongs to.
$seats = @(
    @{ Name = 'green';  R = 0.294; G = 0.545; B = 0.231 }
    @{ Name = 'orange'; R = 0.780; G = 0.353; B = 0.157 }
    @{ Name = 'blue';   R = 0.306; G = 0.510; B = 0.651 }
    @{ Name = 'red';    R = 0.769; G = 0.165; B = 0.047 }
)

# Green screen. Greenness is how far the green channel stands above the strongest of the other two,
# which is a far better test than "close to pure green": it catches the dark green in shadowed
# edges and ignores the mole's own colours, none of which are green-dominant.
$fullyGreenAbove = 60      # greenness at or over this is background
$noGreenBelow = 12         # greenness under this is untouched artwork

# Bandana. The only strongly saturated red in the picture: the body browns sit near thirty degrees
# of hue at about half this saturation, and the outline and eye are excluded by lightness.
$bandanaMaxHue = 22.0
$bandanaMinHue = 344.0
$bandanaMinSaturation = 0.45
$bandanaMinValue = 0.18

function Get-Hsv([double] $r, [double] $g, [double] $b) {
    $max = [Math]::Max($r, [Math]::Max($g, $b))
    $min = [Math]::Min($r, [Math]::Min($g, $b))
    $span = $max - $min
    $hue = 0.0

    if ($span -gt 0) {
        if ($max -eq $r) { $hue = 60.0 * ((($g - $b) / $span) % 6.0) }
        elseif ($max -eq $g) { $hue = 60.0 * ((($b - $r) / $span) + 2.0) }
        else { $hue = 60.0 * ((($r - $g) / $span) + 4.0) }
    }

    if ($hue -lt 0) { $hue += 360.0 }

    $saturation = if ($max -le 0) { 0.0 } else { $span / $max }

    return @{ H = $hue; S = $saturation; V = $max }
}

function Test-Bandana($hsv) {
    if ($hsv.S -lt $bandanaMinSaturation -or $hsv.V -lt $bandanaMinValue) { return $false }

    return ($hsv.H -le $bandanaMaxHue) -or ($hsv.H -ge $bandanaMinHue)
}

$sheet = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
$width = $sheet.Width
$height = $sheet.Height

Write-Host "sheet   $width x $height"

# First pass: classify, and learn how light the bandana is on average so the recolour has something
# to measure each pixel against.
$kept = 0
$dropped = 0
$feathered = 0
$bandanaPixels = 0
$bandanaValue = 0.0

$class = New-Object 'byte[,]' $width, $height     # 0 background, 1 artwork, 2 bandana
$alpha = New-Object 'double[,]' $width, $height

for ($y = 0; $y -lt $height; $y++) {
    for ($x = 0; $x -lt $width; $x++) {
        $pixel = $sheet.GetPixel($x, $y)
        $greenness = $pixel.G - [Math]::Max($pixel.R, $pixel.B)

        if ($greenness -ge $fullyGreenAbove) {
            $class[$x, $y] = 0
            $alpha[$x, $y] = 0.0
            $dropped++
            continue
        }

        if ($greenness -gt $noGreenBelow) {
            # An edge pixel, part mole and part background.
            $alpha[$x, $y] = 1.0 - (($greenness - $noGreenBelow) / ($fullyGreenAbove - $noGreenBelow))
            $feathered++
        }
        else {
            $alpha[$x, $y] = 1.0
        }

        $hsv = Get-Hsv ($pixel.R / 255.0) ($pixel.G / 255.0) ($pixel.B / 255.0)

        if (Test-Bandana $hsv) {
            $class[$x, $y] = 2
            $bandanaPixels++
            $bandanaValue += $hsv.V
        }
        else {
            $class[$x, $y] = 1
        }

        $kept++
    }
}

$bandanaAverage = if ($bandanaPixels -gt 0) { $bandanaValue / $bandanaPixels } else { 1.0 }

Write-Host "kept    $kept"
Write-Host "dropped $dropped  (background)"
Write-Host "edges   $feathered  (partial alpha)"
Write-Host "bandana $bandanaPixels  (average lightness $([Math]::Round($bandanaAverage, 3)))"

if ($bandanaPixels -eq 0) {
    Write-Warning 'No bandana pixels found. The hue and saturation thresholds do not match this art.'
}

if ($Report) {
    $sheet.Dispose()
    return
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($seat in $seats) {
    $out = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    for ($y = 0; $y -lt $height; $y++) {
        for ($x = 0; $x -lt $width; $x++) {
            if ($class[$x, $y] -eq 0) {
                $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0))
                continue
            }

            $pixel = $sheet.GetPixel($x, $y)
            $r = $pixel.R / 255.0
            $g = $pixel.G / 255.0
            $b = $pixel.B / 255.0

            if ($class[$x, $y] -eq 2) {
                # The bandana, in this platoon's colour, keeping this pixel's own lightness.
                $hsv = Get-Hsv $r $g $b
                $ratio = $hsv.V / $bandanaAverage
                $r = $seat.R * $ratio
                $g = $seat.G * $ratio
                $b = $seat.B * $ratio
            }
            elseif ($alpha[$x, $y] -lt 1.0) {
                # Take the green spill off an edge pixel, or every mole gets a green rim.
                $spill = ($g - [Math]::Max($r, $b)) * (1.0 - $alpha[$x, $y])
                $g = [Math]::Max(0.0, $g - $spill)
            }

            $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(
                [int][Math]::Round(255 * [Math]::Min(1.0, $alpha[$x, $y])),
                [int][Math]::Round(255 * [Math]::Min(1.0, [Math]::Max(0.0, $r))),
                [int][Math]::Round(255 * [Math]::Min(1.0, [Math]::Max(0.0, $g))),
                [int][Math]::Round(255 * [Math]::Min(1.0, [Math]::Max(0.0, $b)))))
        }
    }

    $path = Join-Path $OutputDirectory "mole-$($seat.Name).png"
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    Write-Host "wrote   $path"
}

$sheet.Dispose()
