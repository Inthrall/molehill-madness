<#
.SYNOPSIS
    Measures how long frames take, at one resolution or across several.

.DESCRIPTION
    The plan's performance task asks whether four live viewports hold sixty frames a second, and
    that question cannot be answered by watching the game: with vertical sync on, every frame takes
    sixteen milliseconds whatever the machine had left over. The client's --perf switch turns vsync
    off, measures for a while and prints a table; this runs it, once per configuration, and puts
    the headline numbers of each run beside each other.

    Resolution was expected to be the axis worth sweeping, on the grounds that everything expensive
    here is per fragment. It is not, on this hardware: the frame rate at 720p and at 1080p is the
    same, and the graphics time is a fifth of a millisecond either way, so what the game costs is
    processor work per frame rather than pixels. The sweep is still the first thing to run on a new
    machine, because that answer is a fact about the machine rather than about the game.

    Runs open on the smallest screen, which is the laptop's own panel, so a sweep does not land on
    a monitor somebody is working on. The game decides that for itself: see Screens.cs, and
    docs/perf.md for what happened when this script tried to decide it instead.

    The C# is built first on purpose. Godot does not compile it from the command line, so a run
    without a build measures the assembly from last time and reports it as though it were this one.

.EXAMPLE
    pwsh tools/scripts/perf.ps1

.EXAMPLE
    pwsh tools/scripts/perf.ps1 -Resolutions 1280x720,1920x1080 -Quality high,low -Repeat 3
#>

[CmdletBinding()]
param(
    # How long to measure for, once warmed up. Thirty seconds covers a few rounds and both halves
    # of the replay cut.
    [double] $Seconds = 30,

    # Which screen sizes to measure at. Sixteen by nine, always: the canvas stretches to fill
    # whatever it is given, so anything else measures a different picture from the one that ships,
    # with the top and bottom of it cropped off. A size larger than the laptop panel runs, and hangs
    # off the edge of it.
    [string[]] $Resolutions = @('1280x720', '1920x1080'),

    # Which quality settings to measure. Two runs where there is something to compare.
    [string[]] $Quality = @('high'),

    # The same garden every time, so two runs differ by the thing under test and nothing else.
    [int] $Seed = 99,

    # How many times to run each configuration.
    #
    # More than one, whenever two settings are being compared. A single run of each said the low
    # quality setting was ten percent slower than the high one, which is the opposite of what it is
    # for, and repeats showed the gap was smaller than the spread between runs of the same setting.
    # A machine with other work on it does not produce the same number twice, and a difference
    # inside that spread is not a difference.
    [int] $Repeat = 1,

    # Where Godot is, if it is not in the usual place beside this repository.
    [string] $Godot = $null
)

$ErrorActionPreference = 'Stop'

# Split on commas as well as on spaces, because pwsh -File hands every argument over as one string
# and a sweep asked for as -Resolutions 1280x720,1920x1080 arrives as a single nonsense resolution.
# Godot then takes 1280x720,1920x1080,2560x1440 and opens a window 1280 by 4002, which is a run
# that measures something real and answers a question nobody asked.
$Resolutions = $Resolutions -split ',' | Where-Object { $_ }
$Quality = $Quality -split ',' | Where-Object { $_ }

$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')

if (-not $Godot) {
    $Godot = Get-ChildItem -Path 'C:\Personal\godot' -Recurse -Filter 'Godot_v*_console.exe' `
        -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Godot -or -not (Test-Path $Godot)) {
    throw 'No Godot console binary found. Pass -Godot with the path to Godot_v*_console.exe.'
}

Write-Host "editor  $Godot"
Write-Host "repo    $root"
Write-Host ''

Write-Host 'Building the C#, because Godot will not.'
& dotnet build (Join-Path $root 'client' 'Molehill.Client.csproj') -v:q --nologo | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw 'The client did not build.'
}

$summary = @()

foreach ($resolution in $Resolutions) {
    foreach ($setting in $Quality) {
      foreach ($attempt in 1..$Repeat) {
        Write-Host ''
        Write-Host "== $resolution, quality $setting, run $attempt of $Repeat ==" -ForegroundColor Cyan

        # Where the window goes is the game's decision, not this script's: it moves itself to the
        # machine's smallest screen, which is the laptop panel rather than a monitor somebody is
        # working on. Worked out in PowerShell and passed in with --position, it landed 578 pixels
        # away from where it was sent, because the two processes count screen pixels differently.
        $arguments = @(
            '--path', (Join-Path $root 'client'),
            '--resolution', $resolution,
            '--',
            '--demo', '--split', '--players=4', '--mute',
            "--seed=$Seed",
            "--perf=$Seconds",
            "--quality=$setting"
        )

        $output = & $Godot @arguments 2>&1 | ForEach-Object { $_.ToString() }
        $output | Where-Object { $_ -match '^(perf:|  \w|  beat)' } | ForEach-Object { Write-Host $_ }

        # A run that drew nothing is refused by the game rather than reported, because a window
        # nobody can see is also a window nobody is drawing.
        if ($output | Select-String -Pattern '^perf: REFUSED') {
            throw 'The run drew nothing. See the lines above.'
        }

        $rate = ($output | Select-String -Pattern '(\d+) a second' | Select-Object -First 1)
        $planning = ($output | Select-String -Pattern '^\s+Planning\s+4\s' | Select-Object -First 1)
        $verdict = ($output | Select-String -Pattern '^perf: (PASS|FAIL)' | Select-Object -First 1)

        # Columns, in order: beat, panes, frames, p50, p95, p99, worst, scene, draw, rcpu, gpu,
        # calls, over. Split on whitespace after trimming, so the first field is the beat.
        $columns = if ($planning) { $planning.Line.Trim() -split '\s+' } else { $null }

        $summary += [pscustomobject] @{
            Resolution  = $resolution
            Quality     = $setting
            Run         = $attempt
            PerSecond   = if ($rate) { [int] $rate.Matches[0].Groups[1].Value } else { 0 }
            PlanningP95 = if ($columns) { [double] $columns[4] } else { 0 }
            PlanningGpu = if ($columns) { [double] $columns[10] } else { 0 }
            Verdict     = if ($verdict) { $verdict.Matches[0].Groups[1].Value } else { 'NO RESULT' }
        }
      }
    }
}

Write-Host ''
Write-Host 'Every run' -ForegroundColor Cyan
$summary | Format-Table -AutoSize

if ($Repeat -gt 1) {
    Write-Host 'Averaged, with the spread' -ForegroundColor Cyan

    $summary | Group-Object Resolution, Quality | ForEach-Object {
        $runs = $_.Group

        [pscustomobject] @{
            Resolution  = $runs[0].Resolution
            Quality     = $runs[0].Quality
            Runs        = $runs.Count
            PerSecond   = [int] ($runs | Measure-Object PerSecond -Average).Average
            Spread      = "{0}-{1}" -f ($runs | Measure-Object PerSecond -Minimum).Minimum,
                                       ($runs | Measure-Object PerSecond -Maximum).Maximum
            PlanningP95 = [math]::Round(($runs | Measure-Object PlanningP95 -Average).Average, 2)
            PlanningGpu = [math]::Round(($runs | Measure-Object PlanningGpu -Average).Average, 3)
        }
    } | Format-Table -AutoSize
}

if ($summary.Verdict -contains 'FAIL' -or $summary.Verdict -contains 'NO RESULT') {
    exit 1
}
