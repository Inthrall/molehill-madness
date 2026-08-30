# Render checks

How to look at a screen without booting the editor, and the one trap that makes the answer wrong.

```
godot --path client --resolution 1280x720 \
  --write-movie frames/f.png --fixed-fps 30 --quit-after 60 -- --demo --mute --panel
```

`--panel` puts the window on the machine's smallest screen, which is the laptop's own panel, so a run does not land on a monitor somebody is working on. `--headless` does not work with `--write-movie` here: the terrain shader needs a rendering device and the canvas item silently falls back to a plain texture draw without one, which is worse than a crash because the frames look almost right.

## `--position 9000,9000` was the old advice and it is wrong

It was here for two years of runs and it does not survive a desk with monitors on it. Windows will not leave a window entirely outside the virtual desktop; it puts it back somewhere visible. And working out a spot that no monitor covers is done in the wrong units, because Windows reports screen bounds to a process in the units that process is aware of, while Godot places windows in real pixels: a position calculated in PowerShell as 2890,2160 put the window at 3468,2592, which was on a screen.

Minimising instead is worse rather than better, because a window nobody can see is a window nobody is drawing: the frames are never asked for, and a run that records them gets a file full of whatever was in the buffer. `--panel` asks Godot which screens exist, in Godot's own numbers, and moves the window to the smallest one. See `Screens.cs` and [`perf.md`](perf.md).

## Import the art first, or the ground is missing

The terrain textures and the garden decor are ordinary Godot resources, so they need importing before anything can load them, and a run that cannot load them prints one error line and draws a grey square. Two steps, and only after the art has changed:

```
pwsh tools/scripts/import-art.ps1 -Source art
godot --path client --headless --import
```

Add `-Only 'mole *'` to work on one family of sheets without waiting for the rest. Run the importer with `-Report` first if the source art itself is new. It prints the panorama's horizon fraction and how much of the watermark is left in each patched rectangle, and both of those are numbers the client has hard-coded against the art it was written for.

## Render at 16:9, always

The project is 1280x720 with `stretch/mode="canvas_items"` and `stretch/aspect="expand"`. Expand means the canvas grows in whichever dimension the window has spare, so a 900x600 window (3:2) does not letterbox: it gives the game a 1280x853 canvas. `--write-movie` then captures 1280x720 of it, cropping about 66 pixels off the top and the same off the bottom.

The frames look plausible and the edges are missing. Anything anchored near the top or bottom of the screen appears clipped when it is not, and worse, anything genuinely clipped looks the same. Two layout "faults" were chased on that evidence before the cause turned up; one was real and one was the crop.

So pass a 16:9 resolution and the capture matches the canvas. `1280x720` or `960x540`.

## Lay out against `Size`, not `GetViewportRect()`

For a `Control` under a `CanvasLayer`, `Size` is the rect it is actually drawn into. `GetViewportRect().Size` is the viewport, which under an expanding stretch is a different shape. Laying out against the viewport puts things off the edge of the canvas, and the crop above then hides the evidence.

## What renders find that tests do not

Nearly every presentation bug in this project has failed silently: invisible tunnels, garden clutter huddled in the map margins, a shader falling back to a plain draw, a turf line quietly erased, a resume button sitting on top of the title. None raised an error and none would have failed an assertion anybody thought to write. Reading the frames is the check.

Two habits follow. Prefer a proportion test to a presence test, because "at least one tunnel exists" passes on a map with one tunnel nobody could ever reach. And read the console output rather than filtering it: the only evidence that the terrain shader had failed to compile was one line that had been scrolled past.
