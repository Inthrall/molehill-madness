using Godot;
using Molehill.Online;

/// <summary>
/// The band that says "your turn is in, theirs is not".
/// </summary>
/// <remarks>
/// Waiting is where an online match spends nearly all of its life, and before this there was nothing
/// on the screen during it: a player committed and then sat looking at the map with no way to tell a
/// thinking opponent from a broken game. Which is the same failure the lobby's moving dots exist to
/// prevent, one screen later.
///
/// A band rather than a curtain, because the map is still worth looking at while you wait. Three
/// things go on it, and no words: who has not committed, whether we can reach the relay, and how long
/// the absent ones have left. The last only exists in Anytime pace, and it is the difference between
/// a screen with some urgency on it and a screen with none.
/// </remarks>
public partial class WaitingSign : Control
{
    private OnlineMatch? _online;
    private double _spun;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 90;
    }

    public override void _Process(double delta)
    {
        _spun += delta;
        QueueRedraw();
    }

    /// <summary>Shown while this platoon's plan is in and the others' are not.</summary>
    public void Watch(OnlineMatch? online, bool waiting)
    {
        _online = online;
        Visible = waiting && online is not null;
    }

    public override void _Draw()
    {
        if (_online is null)
        {
            return;
        }

        // Size, not GetViewportRect: the project stretches, so the viewport rect and the rect this
        // control is actually drawn into are different sizes, and laying out against the wrong one puts
        // the band off the top of the screen.
        Vector2 viewport = Size;
        float height = Mathf.Clamp(viewport.Y * 0.09f, 44f, 96f);
        float width = Mathf.Min(viewport.X * 0.6f, height * 7f);

        // Along the top, where the sky is. The bottom is where the interface already lives, and a
        // band down there both fought the panels for room and fell off the edge of a short screen.
        Rect2 band = new Rect2(
            new Vector2((viewport.X - width) / 2f, viewport.Y * 0.05f),
            new Vector2(width, height));

        DrawRect(band, new Color(Palette.Panel, 0.92f));
        DrawRect(band, new Color(Palette.OnPanel, 0.35f), false, 2f);

        float middle = band.Position.Y + (height / 2f);
        float glyph = height * 0.46f;

        DrawOutstanding(band, middle, glyph);
        DrawPulse(band, middle, height);
        DrawCountdown(band, middle, glyph);
    }

    /// <summary>
    /// A mole per platoon still to commit.
    /// </summary>
    /// <remarks>
    /// Uncoloured, because which platoons are outstanding is exactly the information simultaneous
    /// turns exist to hide. How many are is fair game and is what a player actually wants: it is the
    /// difference between one straggler and three.
    /// </remarks>
    private void DrawOutstanding(Rect2 band, float middle, float glyph)
    {
        int outstanding = Mathf.Max(_online!.WaitingOn, 0);
        float left = band.Position.X + (glyph * 0.9f);

        for (int mole = 0; mole < outstanding; mole++)
        {
            Glyphs.Mole(
                this,
                new Vector2(left + (mole * glyph * 1.15f), middle),
                glyph,
                new Color(Palette.OnPanel, 0.5f));
        }
    }

    /// <summary>
    /// Dots going round, dimmed when we cannot reach the relay.
    /// </summary>
    /// <remarks>
    /// The dim state is the whole point of drawing them at all: a player who cannot tell a tunnel
    /// from a slow friend will assume the game is broken and close it.
    /// </remarks>
    private void DrawPulse(Rect2 band, float middle, float height)
    {
        float size = height * 0.14f;
        Vector2 at = band.Position + (band.Size / 2f);
        Color ink = _online!.Struggling
            ? new Color(Palette.OnPanel, 0.28f)
            : Palette.OnPanel;

        for (int dot = 0; dot < 3; dot++)
        {
            float phase = (float)(_spun * 2.2d) - (dot * 0.4f);
            float lift = Mathf.Max(0f, Mathf.Sin(phase)) * size * 1.2f;

            DrawCircle(new Vector2(at.X + ((dot - 1) * size * 2.6f), middle - lift), size * 0.5f, ink);
        }
    }

    /// <summary>
    /// How much of the window is left, as an emptying bar. Anytime pace only.
    /// </summary>
    /// <remarks>
    /// A bar rather than a clock face, and no numerals, because the design spends its one numeral on
    /// damage. What a player needs from this is "loads of time" or "nearly out", and a bar says both
    /// at a glance from across a room.
    /// </remarks>
    private void DrawCountdown(Rect2 band, float middle, float glyph)
    {
        if (_online!.Deadline is not System.DateTimeOffset due
            || _online.Seating is not Seating seating
            || seating.WindowSeconds <= 0)
        {
            return;
        }

        double left = (due - System.DateTimeOffset.UtcNow).TotalSeconds;
        float fraction = Mathf.Clamp((float)(left / seating.WindowSeconds), 0f, 1f);

        float width = glyph * 3.2f;
        float height = glyph * 0.34f;
        Rect2 track = new Rect2(
            new Vector2(band.Position.X + band.Size.X - width - (glyph * 0.9f), middle - (height / 2f)),
            new Vector2(width, height));

        DrawRect(track, new Color(Palette.OnPanel, 0.2f));
        DrawRect(
            new Rect2(track.Position, new Vector2(track.Size.X * fraction, track.Size.Y)),
            // Reddens as it empties, which is the one place a colour change is worth more than a
            // shape: the last tenth of a window is the only part anybody needs to act on.
            fraction < 0.15f ? Palette.Seat(3) : Palette.OnPanel);
        DrawRect(track, new Color(Palette.OnPanel, 0.35f), false, 1.5f);
    }
}
