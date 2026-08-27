using Godot;
using Molehill.Online;

/// <summary>
/// The screen between pressing play and the match existing: the code, and who has arrived.
/// </summary>
/// <remarks>
/// A host has one job here, which is to read five letters out to somebody, so the code is the
/// largest thing on the screen and everything else is small. A joiner has no job at all and needs
/// only to see that something is happening.
///
/// The moles fill in as seats are taken, which is the same language the menu uses for how many are
/// playing, so "two of four have arrived" needs no numeral and no sentence. Waiting is the state an
/// online match spends most of its life in, and the design's answer to "is it broken or is it just
/// waiting" is that a screen which is doing something should visibly be doing it. So the dots move.
/// </remarks>
public partial class Lobby : Control
{
    private OnlineMatch? _online;
    private double _spun;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 100;
    }

    public override void _Process(double delta)
    {
        _spun += delta;
        QueueRedraw();
    }

    public void Show(OnlineMatch online)
    {
        _online = online;
        Visible = true;
    }

    public override void _Draw()
    {
        if (_online is null)
        {
            return;
        }

        Vector2 viewport = Size;

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);
        MenuHill.Draw(this, viewport);

        DrawCode(viewport);
        DrawArrivals(viewport);
        DrawPulse(viewport);
    }

    /// <summary>
    /// The five letters, big enough to read off a screen held at arm's length across a room.
    /// </summary>
    private void DrawCode(Vector2 viewport)
    {
        string code = _online!.Code;

        if (code.Length == 0)
        {
            return;
        }

        float wide = Mathf.Min(viewport.X * 0.15f, viewport.Y * 0.2f);
        float tall = wide * 1.28f;
        float gap = wide * 0.16f;
        float left = (viewport.X - ((wide * code.Length) + (gap * (code.Length - 1)))) / 2f;
        float top = viewport.Y * 0.16f;

        for (int index = 0; index < code.Length; index++)
        {
            Rect2 tile = new Rect2(left + (index * (wide + gap)), top, wide, tall);

            DrawRect(tile, Palette.Panel);
            DrawRect(tile, Palette.OnPanel, false, 3f);

            Font font = ThemeDB.FallbackFont;
            string letter = code[index].ToString();
            int size = (int)(tall * 0.66f);
            Vector2 measured = font.GetStringSize(letter, HorizontalAlignment.Left, -1, size);

            DrawString(
                font,
                tile.Position + new Vector2(
                    (tile.Size.X - measured.X) / 2f,
                    (tile.Size.Y + (measured.Y * 0.62f)) / 2f),
                letter,
                HorizontalAlignment.Left,
                -1,
                size,
                Palette.OnPanel);
        }
    }

    /// <summary>One mole per seat, coloured once somebody is sitting in it.</summary>
    private void DrawArrivals(Vector2 viewport)
    {
        int seats = Mathf.Max(_online!.PlayerCount, 0);

        if (seats == 0)
        {
            return;
        }

        float size = Mathf.Clamp(viewport.X * 0.06f, 28f, 72f);
        float gap = size * 1.35f;
        float left = (viewport.X - (gap * (seats - 1))) / 2f;
        float top = viewport.Y * 0.6f;

        for (int seat = 0; seat < seats; seat++)
        {
            Vector2 at = new Vector2(left + (seat * gap), top);
            bool arrived = seat < _online.Seated;

            // An empty seat is an outline, so the row reads as "these are the places" rather than
            // as a row of grey moles that might be a different kind of platoon.
            if (arrived)
            {
                Glyphs.Mole(this, at, size, Palette.Seat(seat));
            }
            else
            {
                DrawArc(at, size * 0.42f, 0, Mathf.Tau, 28, new Color(Palette.OnPanel, 0.35f), 2f);
            }

            // This player's own seat, marked, because on a phone it is the only way to know which
            // platoon you will be.
            if (seat == _online.Seat)
            {
                DrawArc(at, size * 0.62f, 0, Mathf.Tau, 32, Palette.OnPanel, 2.5f);
            }
        }
    }

    /// <summary>
    /// Three dots going round. Something is happening, and here is the proof.
    /// </summary>
    /// <remarks>
    /// It also says which kind of something: struggling to reach the relay is drawn dimmer than
    /// waiting for a person, because a player who cannot tell a tunnel from a slow friend will
    /// assume the game is broken.
    /// </remarks>
    private void DrawPulse(Vector2 viewport)
    {
        float size = Mathf.Clamp(viewport.X * 0.02f, 6f, 14f);
        Vector2 middle = new Vector2(viewport.X / 2f, viewport.Y * 0.78f);
        // Ink rather than the panel cream: these sit on soil and on sky, not on a panel, and they are
        // the only thing on the screen proving anything is happening.
        Color ink = _online!.Struggling
            ? new Color(Palette.Ink, 0.3f)
            : Palette.Ink;

        for (int dot = 0; dot < 3; dot++)
        {
            float phase = (float)(_spun * 2.2d) - (dot * 0.4f);
            float lift = Mathf.Max(0f, Mathf.Sin(phase)) * size * 1.1f;

            DrawCircle(
                middle + new Vector2((dot - 1) * size * 2.4f, -lift),
                size * 0.5f,
                ink);
        }
    }
}
