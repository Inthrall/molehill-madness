using Godot;
using MoleSim.Diagnostics;

/// <summary>
/// The Phase 0 gate, on screen.
/// </summary>
/// <remarks>
/// This scene answers the two questions that decide whether the whole technical plan
/// stands up, and it answers them in a form somebody can read off a phone held at arm's
/// length:
///
/// 1. Does Godot's C# export actually run on this device, at a sensible frame rate?
/// 2. Does the simulation compute the same answers here as it does on a desktop?
///
/// The second is the one that matters. The COMBINED line is a single hash folding every
/// part of the simulation that could plausibly differ between platforms. If a phone and a
/// desktop print the same sixteen characters, the fixed-point arithmetic, the seeded
/// generator and the terrain hashing all agree, and online cross-play is possible. If
/// they differ, nothing above this gate is worth building until it is understood.
/// </remarks>
public partial class Phase0Probe : Node2D
{
    private const string ExpectedFromDesktop = "D1096413CA1B6CF8";

    private DeterminismFingerprint _fingerprint;
    private long _probeMilliseconds;
    private bool _matchesDesktop;

    private Label _headline = null!;
    private Label _detail = null!;
    private Label _frameRate = null!;

    private double _elapsed;
    private float _moleX;
    private int _moleDirection = 1;

    public override void _Ready()
    {
        // Run the probe before anything else, so a device that cannot even get through it
        // fails loudly rather than showing a cheerful walking mole over a broken sim.
        ulong startedAt = Time.GetTicksMsec();
        _fingerprint = DeterminismProbe.Run();
        _probeMilliseconds = (long)(Time.GetTicksMsec() - startedAt);

        string combined = _fingerprint.Combined.ToString("X16");
        _matchesDesktop = combined == ExpectedFromDesktop;

        GD.Print("Molehill Phase 0 probe");
        GD.Print($"  combined  {combined}");
        GD.Print($"  expected  {ExpectedFromDesktop}");
        GD.Print($"  agrees    {_matchesDesktop}");
        GD.Print($"  took      {_probeMilliseconds} ms");

        BuildInterface(combined);
    }

    private void BuildInterface(string combined)
    {
        CanvasLayer layer = new CanvasLayer();
        AddChild(layer);

        MarginContainer margin = new MarginContainer
        {
            AnchorRight = 1,
            AnchorBottom = 1,
        };
        margin.AddThemeConstantOverride("margin_left", 48);
        margin.AddThemeConstantOverride("margin_top", 40);
        margin.AddThemeConstantOverride("margin_right", 48);
        margin.AddThemeConstantOverride("margin_bottom", 40);
        layer.AddChild(margin);

        VBoxContainer column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 18);
        margin.AddChild(column);

        Label title = MakeLabel("MOLEHILL  ·  PHASE 0 PROBE", 30, new Color(0.42f, 0.65f, 0.33f));
        column.AddChild(title);

        // The verdict, as large as it can reasonably be: this is the line somebody reads
        // off a phone screen while standing next to the desktop that produced the other one.
        _headline = MakeLabel(
            _matchesDesktop ? "SIMULATIONS AGREE" : "MISMATCH",
            56,
            _matchesDesktop ? new Color(0.42f, 0.65f, 0.33f) : new Color(0.77f, 0.16f, 0.05f));
        column.AddChild(_headline);

        _detail = MakeLabel(BuildDetail(combined), 24, new Color(0.18f, 0.14f, 0.10f));
        column.AddChild(_detail);

        Control spacer = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(spacer);

        _frameRate = MakeLabel("", 22, new Color(0.43f, 0.36f, 0.28f));
        column.AddChild(_frameRate);
    }

    private string BuildDetail(string combined)
    {
        string agreement = _fingerprint.TerrainHashesAgree ? "yes" : "NO, grid bug";

        return
            $"combined   {combined}\n" +
            $"desktop    {ExpectedFromDesktop}\n" +
            "\n" +
            $"fix64      {_fingerprint.Arithmetic:X16}\n" +
            $"vec2       {_fingerprint.DriftX:X16} {_fingerprint.DriftY:X16}\n" +
            $"cell       {_fingerprint.CellX}, {_fingerprint.CellY}\n" +
            $"rng        {_fingerprint.Randomness:X16}\n" +
            $"terrain    {_fingerprint.TerrainRolling:X16}\n" +
            $"rolling ok {agreement}\n" +
            "\n" +
            $"probe took {_probeMilliseconds} ms\n" +
            $"device     {OS.GetName()} · {OS.GetProcessorName()}\n" +
            $"renderer   {RenderingServer.GetVideoAdapterName()}";
    }

    private static Label MakeLabel(string text, int fontSize, Color colour)
    {
        Label label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;

        // A placeholder mole walking a flat floor. Not a simulation, just proof that the
        // engine is running C# and drawing at a sensible rate on this hardware.
        float speed = 220f;
        _moleX += (float)delta * speed * _moleDirection;

        float width = GetViewportRect().Size.X;
        if (_moleX > width - 140f)
        {
            _moleX = width - 140f;
            _moleDirection = -1;
        }
        else if (_moleX < 140f)
        {
            _moleX = 140f;
            _moleDirection = 1;
        }

        if (_frameRate is not null)
        {
            _frameRate.Text =
                $"{Engine.GetFramesPerSecond():0} fps    " +
                $"{GetViewportRect().Size.X:0} x {GetViewportRect().Size.Y:0}    " +
                "tap to re-run the probe";
        }

        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        bool tapped = @event is InputEventScreenTouch { Pressed: true }
            or InputEventMouseButton { Pressed: true };

        if (!tapped)
        {
            return;
        }

        // Re-running proves the answer is stable rather than a lucky first pass.
        ulong startedAt = Time.GetTicksMsec();
        _fingerprint = DeterminismProbe.Run();
        _probeMilliseconds = (long)(Time.GetTicksMsec() - startedAt);

        string combined = _fingerprint.Combined.ToString("X16");
        _matchesDesktop = combined == ExpectedFromDesktop;

        _headline.Text = _matchesDesktop ? "SIMULATIONS AGREE" : "MISMATCH";
        _headline.AddThemeColorOverride(
            "font_color",
            _matchesDesktop ? new Color(0.42f, 0.65f, 0.33f) : new Color(0.77f, 0.16f, 0.05f));
        _detail.Text = BuildDetail(combined);
    }

    public override void _Draw()
    {
        Vector2 size = GetViewportRect().Size;
        float groundY = size.Y - 120f;

        // The document's palette, so the probe already looks like the game it belongs to.
        Color sky = new Color(0.949f, 0.945f, 0.894f);
        Color soil = new Color(0.847f, 0.749f, 0.596f);
        Color grass = new Color(0.435f, 0.647f, 0.325f);
        Color ink = new Color(0.18f, 0.141f, 0.102f);
        Color lava = new Color(0.878f, 0.290f, 0.094f);

        DrawRect(new Rect2(0, 0, size.X, size.Y), sky);
        DrawRect(new Rect2(0, groundY, size.X, size.Y - groundY), soil);
        DrawRect(new Rect2(0, groundY, size.X, 10f), grass);
        DrawRect(new Rect2(0, size.Y - 18f, size.X, 18f), lava);

        // A mole: a body, a snout that faces the way it is going, and a bob in its step.
        float bob = Mathf.Sin((float)_elapsed * 9f) * 5f;
        Vector2 centre = new Vector2(_moleX, groundY - 26f + bob);

        DrawCircle(centre, 26f, ink);
        DrawCircle(centre + new Vector2(-9f * _moleDirection, -20f), 7f, ink);
        DrawCircle(centre + new Vector2(9f * _moleDirection, -20f), 7f, ink);
        DrawCircle(centre + new Vector2(22f * _moleDirection, 4f), 9f, new Color(0.847f, 0.502f, 0.478f));
        DrawArc(centre + new Vector2(0f, -6f), 20f, Mathf.Pi, Mathf.Tau, 24, sky, 5f);

        // Its shadow stays put while it bobs, which is most of what sells the walk.
        DrawSetTransform(new Vector2(_moleX, groundY + 4f), 0f, new Vector2(1f, 0.25f));
        DrawCircle(Vector2.Zero, 24f, new Color(0f, 0f, 0f, 0.15f));
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }
}
