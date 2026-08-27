using Godot;

/// <summary>
/// The lobby: how many platoons are sitting down, and a button to start.
/// </summary>
/// <remarks>
/// The design's couch model is that whoever opens the lobby picks the player count up front, two,
/// three or four, with four the default and a hard ceiling rather than a first step. That is all
/// this is. There is no online code to type in yet, no pace to choose and no AI to play against,
/// because the design rules out opponent AI everywhere.
///
/// Wordless like the rest of it. The count is shown as that many platoon-coloured moles rather
/// than as a numeral, because the design spends its one numeral on damage, and a row of moles says
/// "this many of you" more directly than a digit would anyway.
///
/// It exists because there was no way back from a finished match except to close the application,
/// and the phase gate is worded "four humans laughing and asking for a rematch".
/// </remarks>
public partial class MenuScene : Control
{
    private int _players = MatchSetup.MostPlayers;
    private readonly Rect2[] _choices = new Rect2[MatchSetup.MostPlayers - MatchSetup.FewestPlayers + 1];
    private Vector2 _play;
    private float _button;
    private bool _startAtOnce;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // The driver has nobody to press anything, so it walks straight through. Deferred by a
        // frame rather than done here, because changing scene from inside _Ready tears down the
        // tree that is still being built.
        _startAtOnce = Flags.Driven();

        if (Flags.WantsTouch())
        {
            Input.EmulateMouseFromTouch = false;
            Input.EmulateTouchFromMouse = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_startAtOnce)
        {
            _startAtOnce = false;
            Start();
            return;
        }

        QueueRedraw();
    }

    // ---- Input -----------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                return;

            case InputEventScreenTouch { Pressed: true } touch:
                HandlePress(touch.Position);
                return;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                HandlePress(click.Position);
                return;

            default:
                return;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space:
            case Key.Enter:
            case Key.KpEnter:
                Start();
                break;

            case Key.Left:
            case Key.A:
                Choose(_players - 1);
                break;

            case Key.Right:
            case Key.D:
                Choose(_players + 1);
                break;

            // Typing the number is input rather than reading it, so the wordless rule is intact.
            case Key.Key2:
                Choose(2);
                break;

            case Key.Key3:
                Choose(3);
                break;

            case Key.Key4:
                Choose(4);
                break;

            default:
                break;
        }
    }

    private void HandlePress(Vector2 at)
    {
        if (at.DistanceTo(_play) <= _button * 1.35f)
        {
            Start();
            return;
        }

        for (int index = 0; index < _choices.Length; index++)
        {
            if (_choices[index].HasPoint(at))
            {
                Choose(MatchSetup.FewestPlayers + index);
                return;
            }
        }
    }

    private void Choose(int players)
    {
        _players = Mathf.Clamp(players, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);
        QueueRedraw();
    }

    private void Start()
    {
        MatchSetup.PlayerCount = _players;

        // A rematch should be a different garden, so the seed moves. Under the driver it does not,
        // because a match that changed every run would break every comparison the render checks
        // are built on, and a named seed beats both.
        MatchSetup.Seed = Flags.Seed()
            ?? (Flags.Driven()
                ? MatchSetup.DrivenSeed
                : (ulong)(Time.GetUnixTimeFromSystem() * 1000d));

        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Match.tscn");
    }

    // ---- Drawing ---------------------------------------------------------------------

    public override void _Draw()
    {
        Vector2 viewport = Size;

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);
        DrawHill(viewport);

        _button = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.075f, 30f, 64f);

        DrawBadge(viewport);
        DrawChoices(viewport);
        DrawPlay(viewport);
    }

    /// <summary>
    /// A hillside along the bottom, so the menu is standing somewhere rather than floating.
    /// </summary>
    /// <remarks>
    /// Drawn from the same two soil colours the map uses, out of a handful of cosine terms rather
    /// than a texture, which keeps the menu on the same no-assets footing as everything else.
    /// </remarks>
    private void DrawHill(Vector2 viewport)
    {
        const int steps = 48;
        Vector2[] surface = new Vector2[steps + 3];

        for (int step = 0; step <= steps; step++)
        {
            float across = step / (float)steps;

            surface[step] = new Vector2(across * viewport.X, SurfaceAt(across, viewport));
        }

        surface[steps + 1] = new Vector2(viewport.X, viewport.Y);
        surface[steps + 2] = new Vector2(0, viewport.Y);

        DrawColoredPolygon(surface, Palette.Of(MoleSim.Terrain.Material.LooseSoil));

        // The turf line on top, which is what makes it read as ground rather than as a shape.
        Vector2[] turf = new Vector2[steps + 1];

        for (int step = 0; step <= steps; step++)
        {
            turf[step] = surface[step];
        }

        DrawPolyline(turf, Palette.Of(MoleSim.Terrain.Material.Turf), viewport.Y * 0.012f);
    }

    /// <summary>Where the hillside is at a given point across the screen.</summary>
    /// <remarks>
    /// Shared by the hill and the moles standing on it. The first version drew a spoil heap at a
    /// fixed height and the hill somewhere else, and the heap floated in the sky with the moles
    /// balanced on it, which is a funny picture but not the intended one.
    /// </remarks>
    private static float SurfaceAt(float across, Vector2 viewport) =>
        (viewport.Y * 0.42f)
            + (Mathf.Cos(across * 7.1f) * viewport.Y * 0.045f)
            + (Mathf.Cos((across * 2.7f) + 1.4f) * viewport.Y * 0.03f);

    /// <summary>
    /// Four platoons coming up out of the hill. As close to a title as a wordless game gets.
    /// </summary>
    private void DrawBadge(Vector2 viewport)
    {
        float size = Mathf.Clamp(viewport.X * 0.075f, 40f, viewport.Y * 0.16f);
        Color soil = Palette.Of(MoleSim.Terrain.Material.LooseSoil);

        for (int seat = 0; seat < MatchSetup.MostPlayers; seat++)
        {
            float across = 0.5f + ((seat - 1.5f) * size * 0.95f / viewport.X);
            float ground = SurfaceAt(across, viewport);
            Vector2 at = new Vector2(across * viewport.X, ground - (size * 0.34f));

            // Each one out of its own molehill, which is the game's whole silhouette.
            DrawColoredPolygon(
                new[]
                {
                    at + new Vector2(-size * 0.82f, size * 0.5f),
                    at + new Vector2(0, -size * 0.12f),
                    at + new Vector2(size * 0.82f, size * 0.5f),
                },
                soil);

            Glyphs.Mole(this, at, size * 0.62f, Palette.Seat(seat));
        }
    }

    /// <summary>Two, three or four, each shown as that many platoons.</summary>
    private void DrawChoices(Vector2 viewport)
    {
        float glyph = _button * 0.62f;
        float height = _button * 1.5f;
        float gap = _button * 0.5f;
        float[] widths = new float[_choices.Length];
        float total = 0f;

        for (int index = 0; index < _choices.Length; index++)
        {
            widths[index] = (glyph * 1.05f * (MatchSetup.FewestPlayers + index)) + _button;
            total += widths[index] + gap;
        }

        float left = (viewport.X - total + gap) / 2f;
        float top = viewport.Y * 0.55f;

        for (int index = 0; index < _choices.Length; index++)
        {
            int players = MatchSetup.FewestPlayers + index;
            bool chosen = players == _players;

            _choices[index] = new Rect2(left, top, widths[index], height);

            // Both states are panels. The unselected ones were a fifteen percent wash over soil to
            // begin with, which on a tan hillside is not a control at all.
            DrawRect(_choices[index], chosen ? Palette.Panel : new Color(Palette.Ink, 0.5f));
            DrawRect(
                _choices[index],
                chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.25f), false,
                chosen ? 3f : 1.5f);

            for (int seat = 0; seat < players; seat++)
            {
                Glyphs.Mole(
                    this,
                    new Vector2(
                        left + (_button * 0.5f) + (glyph * 0.52f) + (seat * glyph * 1.05f),
                        top + (height / 2f)),
                    glyph,
                    chosen ? Palette.Seat(seat) : new Color(Palette.Seat(seat), 0.65f));
            }

            left += widths[index] + gap;
        }
    }

    private void DrawPlay(Vector2 viewport)
    {
        _play = new Vector2(viewport.X / 2f, viewport.Y * 0.81f);

        DrawCircle(_play, _button, Palette.Panel);
        DrawArc(_play, _button, 0, Mathf.Tau, 40, new Color(Palette.OnPanel, 0.55f), 3f);
        Glyphs.Play(this, _play + new Vector2(_button * 0.06f, 0), _button * 1.1f, Palette.OnPanel);
    }
}
