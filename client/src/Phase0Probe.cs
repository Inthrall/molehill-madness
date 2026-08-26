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
    private int _framesDrawn;

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
        GD.Print($"  user args [{string.Join("] [", OS.GetCmdlineUserArgs())}]");

        BuildInterface(combined);
    }

    private MarginContainer _margin = null!;
    private Label _title = null!;

    private void BuildInterface(string combined)
    {
        CanvasLayer layer = new CanvasLayer();
        AddChild(layer);

        MarginContainer margin = new MarginContainer
        {
            AnchorRight = 1,
            AnchorBottom = 1,
        };
        _margin = margin;
        layer.AddChild(margin);

        VBoxContainer column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 22);
        margin.AddChild(column);

        // Spacers above and below the text so the block sits in the middle of whatever
        // shape of screen this lands on. A phone held in portrait stretches the viewport
        // to well over twice its base height, and without this everything huddles at the
        // top of an empty screen.
        column.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        _title = MakeLabel("MOLEHILL  ·  PHASE 0 PROBE", 38, new Color(0.42f, 0.65f, 0.33f));
        column.AddChild(_title);

        // The verdict, as large as it can reasonably be: this is the line somebody reads
        // off a phone screen while standing next to the desktop that produced the other one.
        _headline = MakeLabel(
            _matchesDesktop ? "SIMULATIONS AGREE" : "MISMATCH",
            80,
            _matchesDesktop ? new Color(0.42f, 0.65f, 0.33f) : new Color(0.77f, 0.16f, 0.05f));
        _headline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_headline);

        _detail = MakeLabel(BuildDetail(combined), 30, new Color(0.18f, 0.14f, 0.10f));
        column.AddChild(_detail);

        column.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        _frameRate = MakeLabel("", 26, new Color(0.43f, 0.36f, 0.28f));
        column.AddChild(_frameRate);

        ApplyLayout();
        GetViewport().SizeChanged += ApplyLayout;
    }

    /// <summary>
    /// Sizes the text against the viewport rather than fixing it.
    /// </summary>
    /// <remarks>
    /// The stretch settings keep a base width of 1280 and let the height run, so the
    /// viewport is about 1280x2844 on a phone held upright and 1280x576 on its side. A
    /// font size that reads well in one is unusable in the other: fixed sizes tuned for
    /// portrait ran straight off the bottom of the screen in landscape. Scaling by
    /// viewport height and clamping the result keeps it readable either way, and
    /// re-running on rotation means turning the phone over does the right thing.
    /// </remarks>
    private void ApplyLayout()
    {
        float height = GetViewportRect().Size.Y;

        int titleSize = (int)Mathf.Clamp(height * 0.045f, 20f, 44f);
        int headlineSize = (int)Mathf.Clamp(height * 0.100f, 42f, 110f);
        int frameRateSize = (int)Mathf.Clamp(height * 0.030f, 15f, 32f);
        int separation = (int)Mathf.Clamp(height * 0.020f, 8f, 30f);
        int side = (int)Mathf.Max(32f, height * 0.03f);
        int bottom = (int)(GroundBandHeight() + (side / 2f));

        // Work out what room is left once everything of a fixed size has had its share,
        // then size the detail block to fit that. Choosing a size and hoping is how the
        // device and renderer lines ended up underneath the soil in landscape.
        const float LineSpacing = 1.35f;
        float reserved =
            side + bottom
            + ((titleSize + headlineSize + frameRateSize) * LineSpacing)
            + (separation * 5);

        int detailLines = _detail.Text.Split('\n').Length;
        float available = Mathf.Max(0f, height - reserved);
        int detailSize = (int)Mathf.Clamp(available / (detailLines * LineSpacing), 13f, 40f);

        _title.AddThemeFontSizeOverride("font_size", titleSize);
        _headline.AddThemeFontSizeOverride("font_size", headlineSize);
        _detail.AddThemeFontSizeOverride("font_size", detailSize);
        _frameRate.AddThemeFontSizeOverride("font_size", frameRateSize);

        if (_detail.GetParent() is VBoxContainer column)
        {
            column.AddThemeConstantOverride("separation", separation);
        }

        _margin.AddThemeConstantOverride("margin_left", side);
        _margin.AddThemeConstantOverride("margin_right", side);
        _margin.AddThemeConstantOverride("margin_top", side);

        // Keep the bottom of the text clear of the ground band the mole walks on.
        _margin.AddThemeConstantOverride("margin_bottom", bottom);
    }

    /// <summary>Height of the soil strip along the bottom, proportional to the screen.</summary>
    private float GroundBandHeight() => Mathf.Max(90f, GetViewportRect().Size.Y * 0.11f);

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

    /// <summary>
    /// Saves a screenshot and quits, so the layout can be checked without a device.
    /// Run with: godot --path client -- --probe-screenshot &lt;path&gt;
    /// </summary>
    private void CaptureIfAsked()
    {
        string[] arguments = OS.GetCmdlineUserArgs();
        int flag = System.Array.IndexOf(arguments, "--probe-screenshot");

        if (flag < 0 || flag + 1 >= arguments.Length)
        {
            return;
        }

        // Give the interface a couple of frames to lay itself out first.
        if (_framesDrawn < 3)
        {
            return;
        }

        Image image = GetViewport().GetTexture().GetImage();
        image.SavePng(arguments[flag + 1]);
        GD.Print($"  saved     {arguments[flag + 1]}");
        GetTree().Quit();
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        _framesDrawn++;
        CaptureIfAsked();

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
        float groundY = size.Y - GroundBandHeight();

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
