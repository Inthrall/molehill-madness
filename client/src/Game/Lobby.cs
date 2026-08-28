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

    /// <summary>
    /// Where the other pace is being offered, or nothing.
    /// </summary>
    /// <remarks>
    /// Reported rather than handled, because this control ignores the mouse and the scene owns every
    /// press in the match. One place decides what a click means, which is why a press on the pause
    /// menu and a press on the map cannot both happen.
    /// </remarks>
    public Rect2 Offer { get; private set; }

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

        if (_online.Stage == OnlineStage.Queueing)
        {
            DrawPool(viewport);
            DrawPulse(viewport);
            return;
        }

        Offer = new Rect2();

        DrawCode(viewport);
        DrawArrivals(viewport);
        DrawPulse(viewport);
    }

    /// <summary>
    /// Waiting in the pool: what was asked for, and the other clock once the queue is thin.
    /// </summary>
    /// <remarks>
    /// There is no code here, because nobody is going to read one out: a matchmade lobby does not
    /// exist until the pool makes one. What there is instead is the thing that was asked for, drawn
    /// the same way the menu drew it a moment ago, so the screen is visibly the answer to the button
    /// that was pressed rather than a different screen that happens to be spinning.
    ///
    /// And, once the relay says the queue is slow, the other clock as something to press. That is
    /// the design's answer to a thin pool, and the reason it is a press rather than an automatic
    /// switch is that somebody who asked for a game right now has not agreed to one that takes a
    /// fortnight. Offered, in the design's own word.
    /// </remarks>
    private void DrawPool(Vector2 viewport)
    {
        OnlineMatch online = _online!;

        float glyph = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.16f, 64f, 180f);
        Vector2 middle = new Vector2(viewport.X / 2f, viewport.Y * 0.30f);

        // On a panel, because that is where every glyph in this game lives and the reason is not
        // taste. The glyph set is drawn in the panel ink, which is nearly the colour of the sky, and
        // the moles inside the hills are cut out in the paper colour: put on the sky with no panel
        // behind it, the whole thing came out as a white ghost with two red specks where the noses
        // were. Rendered before anybody would have noticed it in a playtest.
        Vector2 plate = new Vector2(glyph * 1.5f, glyph * 1.2f);
        Rect2 panel = new Rect2(middle - (plate / 2f), plate);

        DrawRect(panel, Palette.Panel);
        DrawRect(panel, new Color(Palette.OnPanel, 0.35f), false, 3f);

        Glyphs.Strangers(this, middle, glyph, Palette.OnPanel);

        // How many are being looked for, in the same row of seats the lobby uses when they start
        // filling. Hung off the panel rather than off a fraction of the screen: at 16:9 a fraction
        // put them exactly on the skyline, where a pale ring on the grass line is half a ring.
        int seats = Mathf.Max(online.PlayerCount, MatchSetup.PlayerCount);
        float mole = Mathf.Clamp(viewport.X * 0.045f, 22f, 54f);
        float gap = mole * 1.35f;
        float left = (viewport.X - (gap * (seats - 1))) / 2f;
        float row = panel.End.Y + (mole * 1.1f);

        for (int seat = 0; seat < seats; seat++)
        {
            DrawArc(
                new Vector2(left + (seat * gap), row), mole * 0.42f, 0, Mathf.Tau, 28,
                new Color(Palette.Ink, 0.4f), 3f);
        }

        if (!online.PoolIsSlow)
        {
            Offer = new Rect2();

            return;
        }

        // The other clock, as a panel to press. Drawn only once the relay says so, because an offer
        // that was there from the first second would read as a choice somebody got wrong rather than
        // as the game admitting nobody is about.
        float wide = Mathf.Clamp(viewport.X * 0.22f, 120f, 320f);
        float tall = wide * 0.44f;

        // Sat above the pulse rather than on it. At 0.68 the panel's bottom half was exactly where
        // the sweeping bar lives, so the bar ran through the middle of the offer and came out the
        // other side, which reads as a loading bar filling a button rather than as two things.
        Offer = new Rect2((viewport.X - wide) / 2f, viewport.Y * 0.55f, wide, tall);

        DrawRect(Offer, Palette.Panel);
        DrawRect(Offer, Palette.OnPanel, false, 3f);

        Vector2 heart = Offer.Position + (Offer.Size / 2f);

        // Whichever one is not being waited for. The pace glyphs are the menu's, so the offer is
        // recognisable as the other one of the two things chosen a moment ago.
        if (online.AskedFor == MatchPace.Live)
        {
            Glyphs.Moon(this, heart, tall * 0.62f, Palette.OnPanel);
        }
        else
        {
            Glyphs.Time(this, heart, tall * 0.62f, Palette.OnPanel);
        }
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
    /// A bar filling and emptying. Something is happening, and here is the proof.
    /// </summary>
    /// <remarks>
    /// Three dots before this, and dots turn out to be too small a promise: they are what a phone
    /// shows while it thinks, so a player reads them as a wait of no particular length and, when it
    /// does turn out to have no particular length, as a game that has hung. A bar is the shape of a
    /// thing in progress even when it cannot say how far through it is.
    ///
    /// It cannot say, and it does not pretend to. There is no total to measure against: the wait is
    /// for other people to arrive, and nobody knows how long that takes. So it sweeps rather than
    /// fills, which is the honest version, and is why the fill is a moving band rather than a level.
    ///
    /// It also says which kind of something. Struggling to reach the relay is drawn dimmer and
    /// slower than waiting for a person, because a player who cannot tell a tunnel from a slow
    /// friend will assume the game is broken.
    /// </remarks>
    private void DrawPulse(Vector2 viewport)
    {
        bool struggling = _online!.Struggling;
        float wide = Mathf.Clamp(viewport.X * 0.26f, 140f, 420f);
        float tall = Mathf.Clamp(viewport.X * 0.016f, 9f, 20f);
        Vector2 middle = new Vector2(viewport.X / 2f, viewport.Y * 0.78f);

        // Ink rather than the panel cream: this sits on soil and on sky, not on a panel.
        //
        // Struggling is told apart by the speed of the sweep and not, as it was, by drawing it
        // fainter. Before the relay has answered there is nothing else on this screen at all, not
        // even the row of seats, because nobody knows yet how many seats there are: a faint bar on
        // an empty hillside is the picture of a game that has hung, which is the opposite of what
        // it is for.
        Color ink = new Color(Palette.Ink, 0.85f);
        Rect2 track = new Rect2(middle.X - (wide / 2f), middle.Y - (tall / 2f), wide, tall);

        DrawRect(track, new Color(ink, 0.22f));

        // A band sweeping the length of it, easing at both ends so it reads as travelling rather
        // than as jumping back to the start.
        float sweep = (float)(_spun * (struggling ? 0.42d : 0.72d)) % 1f;
        float band = wide * 0.34f;
        float travel = (wide + band) * Ease(sweep);
        float left = Mathf.Max(track.Position.X, track.Position.X + travel - band);
        float right = Mathf.Min(track.End.X, track.Position.X + travel);

        if (right > left)
        {
            DrawRect(new Rect2(left, track.Position.Y, right - left, tall), ink);
        }
    }

    /// <summary>Smoothed at both ends, so the sweep leaves and arrives rather than snapping.</summary>
    private static float Ease(float along) => along * along * (3f - (2f * along));
}
