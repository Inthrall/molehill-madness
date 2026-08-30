# Performance

How to measure this game, what it measures at, and the two things that made every earlier attempt wrong.

```
pwsh tools/scripts/perf.ps1
pwsh tools/scripts/perf.ps1 -Seconds 20 -Resolutions 1280x720,1920x1080 -Quality high,low -Repeat 3
```

The script builds the C# (Godot will not), then runs the game once per configuration with `--perf`, which drives a four player match through the demo driver, measures frames, prints a table and quits with a verdict. The plan's Phase 3 question is whether four live viewports hold sixty frames a second, and this is what answers it.

## The two ways of measuring this that do not work

**With vertical sync on, every frame takes sixteen milliseconds.** That is the display's pace, not the machine's, and a game with ten times the headroom it needs reports exactly the same number as one with none. `--perf` turns vsync off and lifts the frame cap, so the figures are what the machine can do rather than what it was allowed to do. It follows that the frame rate in the table is not a frame rate anybody will ever see: it is headroom, expressed as a rate.

**A window nobody can see is a window nobody is drawing.** Minimised, hidden, or moved off the desktop, the compositor stops asking for frames, and the run still measures intervals, still fills in every row and still passes, because a game drawing nothing is very fast indeed. A minimised run measured a flat 6.9 ms a frame, zero draw calls, and reported a pass. The probe now refuses to report a run with no draw calls in it and exits 2.

So a measured run is on a screen. Which screen is the one thing that is chosen: `--panel` puts the window on the machine's smallest screen, which is the laptop's own panel, rather than on a monitor somebody is working on. `--perf` implies it.

## Where the window goes, and why it is not `--position`

`--position 9000,9000` was the habit for keeping development runs out of the way. It does not survive a desk with monitors on it, in two separate ways, and both were found by putting a window over the top of what the machine's owner was doing.

Windows will not leave a window entirely outside the virtual desktop: it puts it back somewhere visible. And the arithmetic for finding a spot that no monitor covers is done in the wrong units, because Windows reports screen bounds to a process in the units that process is aware of. An ordinary PowerShell session sees a 3840 wide monitor as 2560 and a panel at x=2890 as x=2312, while Godot is per-monitor aware and places windows in real pixels. A position worked out in one and handed to the other landed 578 pixels away from where it was sent.

`Screens.ToThePanel()` does it inside the game instead, off Godot's own screen list. Godot's screen numbers and Godot's window position cannot disagree with each other.

## Reading the table

```
  beat        panes  frames    p50    p95    p99  worst  scene   draw  rcpu    gpu  calls    over
  Planning       4     539    2.4    3.0    4.8   15.0    0.1    0.6   0.8   0.29    332   0.0%  ok
```

Rows are one per beat and pane count, because "the game runs at sixty" is not a claim anybody can act on: planning across four panes, a replay with one camera pushing in, and an aftermath sitting still are three different pictures with three different costs. The pane count is the one that was actually on the screen, so a single run walks the whole range the replay director cuts between rather than needing a switch to force it.

- `p50` to `worst` are the frame interval in milliseconds. `over` is the share of frames past 16.7 ms.
- `scene` is time inside the match scene's update, `draw` is the world views drawing themselves, `rcpu` is the renderer's own processor time and `gpu` is the graphics time for the frame. They do not sum to the frame: work overlaps, and the rest is the engine.
- Rows of fewer than sixty frames are marked `thin` and are not judged.
- Frames over 50 ms are listed separately underneath with when they happened and what was on the screen. The percentiles say whether the game is fast enough; that list says whether it ever stops, which is a different question with a different answer.

## What it measures at, on the machine it was written on

Intel Core Ultra 7 255H, NVIDIA RTX 500 Ada laptop GPU, debug build, four platoons, split screen.

| Resolution | Frames a second | Planning, four panes, p95 | GPU |
| --- | --- | --- | --- |
| 1280x720 | 635 | 3.0 ms | 0.29 ms |
| 1920x1080 | 637 | 3.0 ms | 0.29 ms |
| 2560x1440 | 367 | 5.5 ms | not measured |
| 3840x2160 | 290 | 6.2 ms | not measured |

**The Phase 3 performance task passes on this machine, with about five times the headroom it needs.** Four live viewports at 1080p cost three milliseconds of a sixteen millisecond budget.

The shape of it matters more than the numbers. The frame rate at 720p and at 1080p is the same, and the graphics time is a fifth of a millisecond either way, so this machine is not drawing-bound at all: it is bound by processor work per frame, which is the renderer submitting a few hundred draw calls and the four panes drawing themselves. That is why the resolution sweep flattens and why a cheaper shader changes nothing here.

## The noise floor, which is the useful number

Three runs of the same configuration ranged from 456 to 617 frames a second. **A difference smaller than about a quarter is not a difference on this machine**, so use `-Repeat 3` whenever two settings are being compared, and treat the spread rather than the average as the thing to beat.

That is how the quality setting was settled. `--quality=low` takes five samples of the cell field per fragment rather than nine, and three runs of each came out at 559 and 503 frames a second with the graphics time overlapping. The cheaper picture is not measurably cheaper here, so nothing selects it automatically, including a phone: see `Quality.cs`, where the decision is written down next to the numbers. Visually the two are the same picture, checked by rendering the same seed both ways and differencing the frames: five taps moves a dotted line of sub-pixel changes along the outline and nothing else.

## What has not been measured

- **A phone.** This is the whole of the low specification question and the only place the quality setting can be settled. `tools/scripts/deploy-android.sh` installs a build, and `--perf` prints to logcat like anything else. Nothing has run it because no device has been plugged in since the probe existed.
- **The Steam Deck**, which the plan asks for at two panes. Same probe, no hardware here.
- **An integrated GPU.** This laptop has one, and Windows hands OpenGL to the discrete card. Forcing the other one would be a better proxy for a low specification desktop than anything measured above.
- **A release export.** Everything here is a debug build running from the editor's assemblies.

## Two things that came out of building this

The terrain shader was reading mipmapped textures inside `if` branches, which is undefined in GLSL: the level of detail comes from how far neighbouring fragments have moved, and neighbours that took the other branch have not moved anywhere the hardware can see. It had always done this on the backdrop's split, and the level anything was read at was up to the driver. Those reads are `textureGrad` now, with the derivatives taken once in uniform control flow.

And the ground shader was reading both sides of a boundary everywhere on the screen. Most of a frame is well inside ground or well inside air, where the mix takes one picture at full weight and the other at none and read both anyway: the sky over a lawn was fetching soil, and every fragment of solid ground was fetching the countryside behind it. It takes the side it is on now, which is the same colour by definition and two or three fewer texture reads over nearly the whole screen. Neither shows up in the table above, because this machine had the room; both are the sort of thing a phone will notice.
