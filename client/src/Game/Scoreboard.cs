using Godot;

/// <summary>
/// How the match went, and the way back to the menu.
/// </summary>
/// <remarks>
/// The end of a match used to be a single mole glyph in the middle of the screen and no way out of
/// the application at all. The gate this whole phase exists to pass is worded "four humans laughing
/// and asking for a rematch", and a build that cannot grant one fails it for a reason that has
/// nothing to do with whether the game is any good.
///
/// Wordless, like everything else. Who won is a flower, since the moles are fighting over a
/// flowerbed. Who was left standing is mole glyphs, filled for the ones still up and hollow for the
/// ones carried off. The order of the rows is the result: winner at the top, then whoever lasted
/// longest. The one numeral is damage, which is the exception the design already carves out on the
/// grounds that a numeral reads the same in every language a seven-year-old might have, and here it
/// is doing the same job it does over a mole's head.
/// </remarks>
public partial class Scoreboard : Control
{
    /// <summary>One platoon's afternoon.</summary>
    public readonly struct Standing
    {
        public Standing(int seat, int survivors, int damageTaken, int outAtRound, bool won)
        {
            Seat = seat;
            Survivors = survivors;
            DamageTaken = damageTaken;
            OutAtRound = outAtRound;
            Won = won;
        }

        public int Seat { get; }

        public int Survivors { get; }

        public int DamageTaken { get; }

        /// <summary>The round this platoon lost its last mole, or zero if it never did.</summary>
        public int OutAtRound { get; }

        public bool Won { get; }
    }

    private Standing[] _rows = System.Array.Empty<Standing>();
    private Vector2 _back;
    private float _button;
    private bool _shown;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    /// <summary>Puts the match's result up, in finishing order.</summary>
    public void Show(Standing[] rows)
    {
        _rows = rows;
        _shown = true;
        Visible = true;
        QueueRedraw();
    }

    public bool IsShowing => _shown;

    /// <summary>Whether a press landed on the way out. Anywhere counts; the button is the hint.</summary>
    public bool TouchedTheWayOut(Vector2 at) => _shown && at.DistanceTo(_back) <= _button * 2.5f;

    public override void _Draw()
    {
        if (!_shown || _rows.Length == 0)
        {
            return;
        }

        Vector2 viewport = Size;
        _button = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.055f, 22f, 46f);

        // The match is still on screen behind this, so it is dimmed rather than covered: the last
        // thing that happened is worth still being able to see.
        DrawRect(new Rect2(Vector2.Zero, viewport), new Color(Palette.Ink, 0.72f));

        float rowHeight = Mathf.Clamp(viewport.Y * 0.13f, 44f, 96f);
        float width = Mathf.Min(viewport.X * 0.72f, rowHeight * 9f);
        float height = (rowHeight * _rows.Length) + (rowHeight * 1.5f);
        Vector2 origin = new Vector2(
            (viewport.X - width) / 2f, (viewport.Y - height) / 2f);

        DrawRect(new Rect2(origin, new Vector2(width, height)), Palette.Panel);

        int worst = 1;

        foreach (Standing row in _rows)
        {
            worst = Mathf.Max(worst, row.DamageTaken);
        }

        for (int index = 0; index < _rows.Length; index++)
        {
            Row(_rows[index], new Rect2(
                origin.X, origin.Y + (index * rowHeight), width, rowHeight), worst);
        }

        _back = new Vector2(
            origin.X + (width / 2f), origin.Y + height - (rowHeight * 0.7f));

        DrawCircle(_back, _button, new Color(Palette.OnPanel, 0.14f));
        DrawArc(_back, _button, 0, Mathf.Tau, 32, new Color(Palette.OnPanel, 0.5f), 2f);
        Glyphs.Back(this, _back, _button * 1.15f, Palette.OnPanel);
    }

    private void Row(Standing row, Rect2 into, int worst)
    {
        float glyph = into.Size.Y * 0.46f;
        float middle = into.Position.Y + (into.Size.Y / 2f);
        Color colour = Palette.Seat(row.Seat);
        float left = into.Position.X + (glyph * 0.9f);

        // The winner takes the flowerbed, so the winner gets the flower.
        if (row.Won)
        {
            Glyphs.Flower(this, new Vector2(left, middle), glyph * 1.25f, colour);
        }
        else
        {
            Glyphs.Mole(this, new Vector2(left, middle), glyph, new Color(colour, 0.75f));
        }

        // Who is still standing, and who was carried off.
        float from = left + (glyph * 1.5f);

        for (int mole = 0; mole < MoleSim.Match.MatchSettings.MolesPerPlatoon; mole++)
        {
            Vector2 at = new Vector2(from + (mole * glyph * 0.95f), middle);

            if (mole < row.Survivors)
            {
                Glyphs.Mole(this, at, glyph * 0.8f, colour);
            }
            else
            {
                DrawArc(at, glyph * 0.28f, 0, Mathf.Tau, 16, new Color(colour, 0.32f), 2f);
            }
        }

        // What it cost them, as a bar and as the one numeral the design keeps.
        float barLeft = from + (glyph * 4.4f);
        float barWidth = Mathf.Max(
            into.Position.X + into.Size.X - barLeft - (glyph * 2.4f), glyph);
        float barHeight = Mathf.Max(glyph * 0.34f, 5f);
        Rect2 bar = new Rect2(barLeft, middle - (barHeight / 2f), barWidth, barHeight);

        DrawRect(bar, new Color(Palette.OnPanel, 0.12f));
        DrawRect(
            new Rect2(bar.Position, new Vector2(barWidth * row.DamageTaken / worst, barHeight)),
            new Color(Palette.Damage, 0.85f));

        DrawString(
            ThemeDB.FallbackFont,
            new Vector2(barLeft + barWidth + (glyph * 0.4f), middle + (glyph * 0.3f)),
            row.DamageTaken.ToString(),
            HorizontalAlignment.Left, -1, (int)(glyph * 0.75f),
            new Color(Palette.OnPanel, 0.75f));
    }
}
