using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Frame times over a driven run, filed under what was on the screen at the time.
/// </summary>
/// <remarks>
/// The plan's performance task asks a question with a number in it: four live viewports at sixty
/// frames a second on a mid PC, and two panes on a Deck. Nothing here could answer it, because
/// nothing was counting, and the rate a person reads off a window is the rate the display allowed
/// rather than the one the machine could manage.
///
/// So two things happen here that never happen in a game. Vertical sync goes off and the frame cap
/// is lifted, because a run paced by the monitor reports sixteen milliseconds a frame whatever the
/// machine had left over, and the headroom is the entire measurement. And every sample is filed
/// under the beat and the pane count it belonged to, because "the game runs at sixty" is not a
/// claim anybody can act on: planning across four panes, a replay with one camera pushing in, and
/// an aftermath sitting still are three different pictures with three different costs, and only one
/// of them is the thing the plan asked about.
///
/// Bucketing by the pane count that was actually up also means the cost of a pane comes out of
/// ordinary play rather than out of a switch invented to force it. The replay director already
/// varies its cut between one camera and four, so a single run walks the whole range and the rows
/// can be read against each other.
///
/// Static, for the reason <see cref="Online"/> is: the driver plays match after match and each one
/// is a new scene, so a probe owned by a scene would measure the least interesting half minute the
/// game has and then be thrown away with it.
/// </remarks>
public sealed class PerfProbe
{
    /// <summary>Sixty frames a second, as the milliseconds one of them gets.</summary>
    private const double BudgetMs = 1000d / 60d;

    /// <summary>
    /// How long to let the machine settle before believing any of it.
    /// </summary>
    /// <remarks>
    /// The opening seconds of a match are shader compilation, texture uploads and a garden being
    /// built, none of which happen again. Left in, they put a hundred millisecond frame in every
    /// run, and the worst-frame column then says the same thing whatever the change under test did.
    /// </remarks>
    private const double WarmUpSeconds = 2d;

    /// <summary>A row needs this many frames before its percentiles mean anything.</summary>
    private const int EnoughToJudge = 60;

    /// <summary>What one row of the report is measuring.</summary>
    private sealed class Bucket
    {
        public readonly List<double> Frames = new List<double>();

        public double SceneMs;

        public double DrawMs;

        public double RenderMs;

        public double GpuMs;

        public double DrawCalls;

        public void Add(double frameMs, double sceneMs, double drawMs, double renderMs, double gpuMs, double drawCalls)
        {
            Frames.Add(frameMs);
            SceneMs += sceneMs;
            DrawMs += drawMs;
            RenderMs += renderMs;
            GpuMs += gpuMs;
            DrawCalls += drawCalls;
        }
    }

    /// <summary>A frame this long is not a slow frame, it is a game that stopped.</summary>
    /// <remarks>
    /// Three times the budget. The percentiles are about whether the game is fast enough and this
    /// is about whether it ever stops, which is a different question with a different answer: a run
    /// can sit at three milliseconds and still lose half a second somewhere, and the player
    /// remembers the half second. They are listed with when they happened and what was on the
    /// screen, because that is what makes one findable.
    /// </remarks>
    private const double HitchMs = BudgetMs * 3d;

    /// <summary>One frame that stopped the game, and everything known about it.</summary>
    private readonly record struct Hitch(double At, double Ms, string Beat, int Panes);

    private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();
    private readonly List<Hitch> _hitches = new List<Hitch>();
    private readonly double _seconds;

    private ulong _lastFrameAt;
    private ulong _drawnMicros;
    private Rid _viewport;
    private double _warmedFor;
    private double _measuredFor;
    private bool _started;
    private bool _broken = true;
    private bool _finished;

    private PerfProbe(double seconds)
    {
        _seconds = seconds;
    }

    /// <summary>The run in progress, or null when nobody asked for one.</summary>
    public static PerfProbe? Current { get; private set; }

    /// <summary>
    /// Starts measuring, if the command line asked for it.
    /// </summary>
    /// <remarks>
    /// Called from the match rather than from the menu, because the menu is one still picture and
    /// measuring it would only ever produce a flattering number.
    /// </remarks>
    public static void StartIfAsked()
    {
        if (Current is not null)
        {
            return;
        }

        double? seconds = Flags.Perf();

        if (seconds is null)
        {
            return;
        }

        Current = new PerfProbe(seconds.Value);
        Current.Begin();
    }

    /// <summary>
    /// Notes one frame, told what the screen was showing while it was drawn.
    /// </summary>
    /// <param name="beat">Which part of the round this frame belonged to.</param>
    /// <param name="panes">How many world views were visible.</param>
    /// <param name="workBeganAt">
    /// Microseconds on the clock when the scene started this frame's work.
    /// </param>
    /// <remarks>
    /// The scene times itself rather than the probe reading Godot's own TIME_PROCESS monitor,
    /// which was the first attempt and reported eleven milliseconds of work inside a frame that
    /// took under two. Whatever that monitor is smoothing, it is not this frame, and a diagnostic
    /// column that cannot be true is worse than no column: the whole point of it is to say whether
    /// to go and look at the shader or at the draw code.
    /// </remarks>
    public void Frame(string beat, int panes, ulong workBeganAt)
    {
        if (_finished)
        {
            return;
        }

        ulong now = Time.GetTicksUsec();
        double ourMs = (now - workBeganAt) / 1000d;

        if (_broken)
        {
            // The first frame after a scene change has a menu, a load and a garden in it. That is a
            // real interval, and it belongs to none of the things being measured here.
            _broken = false;
            _lastFrameAt = now;
            return;
        }

        double frameMs = (now - _lastFrameAt) / 1000d;
        _lastFrameAt = now;

        if (!_started)
        {
            // Warm-up runs from the first frame of the first round, so it covers the shaders and
            // the textures rather than the loading bar in front of them.
            _warmedFor += frameMs / 1000d;

            if (_warmedFor < WarmUpSeconds)
            {
                return;
            }

            _started = true;
            _drawnMicros = 0;
            GD.Print($"perf: warmed up, measuring for {_seconds:0.#} s");
            return;
        }

        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double renderMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
        double gpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);

        For($"{beat}/{panes}").Add(frameMs, ourMs, _drawnMicros / 1000d, renderMs, gpuMs, drawCalls);
        _drawnMicros = 0;

        _measuredFor += frameMs / 1000d;

        if (frameMs >= HitchMs)
        {
            _hitches.Add(new Hitch(_measuredFor, frameMs, beat, panes));
        }

        if (_measuredFor >= _seconds)
        {
            Finish();
        }
    }

    /// <summary>
    /// Takes the time one world view spent drawing itself.
    /// </summary>
    /// <remarks>
    /// Separate from the scene's own figure, and the split is the point of having either. A view
    /// draws during rendering rather than during the update, so timing <c>_Process</c> alone
    /// reported nothing at all: the panes are where the client's own work is, and how much of a
    /// frame they account for is the difference between a shader to cut down and draw code to
    /// tidy up.
    /// </remarks>
    public void Drew(ulong beganAt)
    {
        _drawnMicros += Time.GetTicksUsec() - beganAt;
    }

    /// <summary>
    /// Says that the next interval spans a scene change and is nobody's frame.
    /// </summary>
    /// <remarks>
    /// The driver goes back to the menu between matches and starts another. Measured through, that
    /// gap arrives as one frame of several hundred milliseconds, filed under whichever row was last
    /// up, where it reads exactly like a stutter somebody should go and find.
    /// </remarks>
    public void Break()
    {
        _broken = true;
    }

    private void Begin()
    {
        // Both of them, or the run measures the monitor. Vsync off on its own still leaves Godot's
        // own cap in place wherever one has been set.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;

        Vector2I window = DisplayServer.WindowGetSize();

        int panel = Screens.ToThePanel();
        Vector2I at = DisplayServer.WindowGetPosition();

        // Per-viewport timing, which is the only instrument here that can see the shader. On a
        // machine with headroom the frame time says nothing about a change to the ground: the frame
        // is over before the GPU is the reason for anything, and high and low quality measure the
        // same. The GPU column is what the tuning is actually read off.
        _viewport = ((SceneTree)Engine.GetMainLoop()).Root.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);

        GD.Print("perf: vsync off, frame cap lifted");
        GD.Print(
            $"perf: window on screen {panel} of {DisplayServer.GetScreenCount()}, "
            + $"at {at.X},{at.Y}");
        GD.Print($"perf: {RenderingServer.GetVideoAdapterName()}");
        GD.Print($"perf: {OS.GetProcessorName()}, {OS.GetProcessorCount()} threads");
        GD.Print(
            $"perf: {window.X}x{window.Y}, quality {Quality.Chosen()}, "
            + $"{(OS.IsDebugBuild() ? "debug" : "release")} build");
    }

    private Bucket For(string key)
    {
        if (!_buckets.TryGetValue(key, out Bucket? bucket))
        {
            bucket = new Bucket();
            _buckets[key] = bucket;
        }

        return bucket;
    }

    private void Finish()
    {
        _finished = true;

        int frames = 0;

        foreach (Bucket counted in _buckets.Values)
        {
            frames += counted.Frames.Count;
        }

        GD.Print(string.Empty);
        GD.Print(
            $"perf: {_measuredFor:0.0} s measured, {frames} frames, "
            + $"{frames / Math.Max(_measuredFor, 0.001):0} a second");
        GD.Print(string.Empty);
        GD.Print(
            "  beat        panes  frames    p50    p95    p99  worst  scene   draw  rcpu    gpu"
            + "  calls    over");

        bool passed = true;
        List<string> keys = new List<string>(_buckets.Keys);
        keys.Sort(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            Bucket bucket = _buckets[key];
            List<double> sorted = new List<double>(bucket.Frames);
            sorted.Sort();

            int count = sorted.Count;
            double p95 = At(sorted, 0.95);
            int over = 0;

            foreach (double frameMs in sorted)
            {
                if (frameMs > BudgetMs)
                {
                    over++;
                }
            }

            bool judged = count >= EnoughToJudge;
            bool ok = !judged || p95 <= BudgetMs;
            passed &= ok;

            string[] parts = key.Split('/');
            string verdict = judged ? (ok ? "ok" : "OVER") : "thin";

            GD.Print(
                $"  {parts[0],-11} {parts[1],4} {count,7} "
                + $"{At(sorted, 0.5),6:0.0} {p95,6:0.0} {At(sorted, 0.99),6:0.0} "
                + $"{sorted[count - 1],6:0.0} {bucket.SceneMs / count,6:0.0} "
                + $"{bucket.DrawMs / count,6:0.0} {bucket.RenderMs / count,5:0.0} "
                + $"{bucket.GpuMs / count,6:0.00} {bucket.DrawCalls / count,6:0} "
                + $"{over * 100d / count,5:0.0}%  {verdict}");
        }

        // A run with no draw calls in it drew nothing, and every number above is a measurement of
        // an idle process. That is not a hypothetical: a window that is minimised, hidden, or moved
        // off the desktop stops the compositor asking for frames, and the run still reports frame
        // intervals, still fills every row, and still passes, because a game drawing nothing is
        // very fast indeed. It was two runs of that before the draw call column gave it away.
        //
        // So the report refuses rather than warns. A performance number nobody can tell is false is
        // worse than no number, and this one comes with a verdict attached.
        double calls = 0;

        foreach (Bucket counted in _buckets.Values)
        {
            calls += counted.DrawCalls;
        }

        if (calls <= 0)
        {
            GD.Print(string.Empty);
            GD.Print("perf: REFUSED, nothing was drawn during the run");
            GD.Print("perf: the window has to be on a screen. Minimised or hidden, the frames");
            GD.Print("perf: are never asked for and the timings are of an idle process.");

            if (Engine.GetMainLoop() is SceneTree idle)
            {
                idle.Quit(2);
            }

            return;
        }

        if (_hitches.Count > 0)
        {
            GD.Print(string.Empty);
            GD.Print($"  {_hitches.Count} frame(s) over {HitchMs:0} ms:");

            foreach (Hitch hitch in _hitches)
            {
                GD.Print(
                    $"    {hitch.At,6:0.0} s  {hitch.Ms,7:0.0} ms  {hitch.Beat}, {hitch.Panes} pane(s)");
            }
        }

        double videoMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024d * 1024d);

        GD.Print(string.Empty);
        GD.Print($"perf: video memory {videoMb:0} MB");
        GD.Print(
            passed
                ? $"perf: PASS, every judged row inside the {BudgetMs:0.0} ms budget at p95"
                : $"perf: FAIL, a judged row is over the {BudgetMs:0.0} ms budget at p95");
        GD.Print("perf: rows of fewer than 60 frames are marked thin and are not judged");

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.Quit(passed ? 0 : 1);
        }
    }

    /// <summary>The value a fraction of the way through an already sorted list.</summary>
    private static double At(List<double> sorted, double fraction)
    {
        int index = (int)Math.Round(fraction * (sorted.Count - 1), MidpointRounding.AwayFromZero);

        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
