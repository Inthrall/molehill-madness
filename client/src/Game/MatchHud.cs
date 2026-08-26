using Godot;
using MoleSim.Match;

/// <summary>
/// What everybody at the table shares: the clock, the tally, the wind and where the round is
/// up to.
/// </summary>
/// <remarks>
/// Wordless, as the design requires. The game is all-ages, ships without text chat and
/// generates its own names precisely so that nothing on screen has to be read, and a HUD full
/// of English would quietly undo all of that.
///
/// Digits survive, and only digits: damage numbers, and the seconds left on the clock. The
/// design keeps them because a numeral reads the same in every language a seven-year-old might
/// have, which is not true of a single word on this screen.
///
/// The clock sits dead centre because the design asks for it there. It belongs to the table
/// rather than to any one player, and the centre is the one place four people around one
/// screen can all see without it being in anybody's pane.
/// </remarks>
public partial class MatchHud : Control
{
    public struct State
    {
        /// <summary>Seconds left to plan, or negative when nobody is planning.</summary>
        public float ClockLeft;

        public float ClockLength;

        /// <summary>How many of each platoon are still up, as the viewer has seen it.</summary>
        public int[] Standing;

        /// <summary>Which platoons have locked their plan in.</summary>
        public bool[] Committed;

        public float Wind;

        /// <summary>Rounds resolved plus the one being planned.</summary>
        public int Round;

        /// <summary>-2 while playing, -1 for a draw, otherwise the winning seat.</summary>
        public int Winner;

        /// <summary>Where the spare grid cell is in a three-player split, if there is one.</summary>
        public Rect2 SpareCell;

        public bool HasSpareCell;

        /// <summary>
        /// Whether the screen is carved into panes. The clock belongs dead centre when it is,
        /// because that is the seam and belongs to nobody, and at the top when it is not,
        /// because the middle of a shared view is where the game is.
        /// </summary>
        public bool Split;

        /// <summary>
        /// How far down the screen the panes' own instruments reach, so the shared tally can sit
        /// below them rather than across the top-right pane's strip.
        /// </summary>
        public float TopClearance;
    }

    private State _state;

    public override void _Ready()
    {
        // Anchors alone leave the rect where it was, which for a Control built in code is
        // nowhere at all. Offsets are the half that gives it a size.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Apply(State state)
    {
        _state = state;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 viewport = GetViewportRect().Size;

        DrawTally(viewport);
        DrawWind(viewport);
        DrawClock(viewport);
        DrawOutcome(viewport);
    }

    /// <summary>
    /// The score, and the whole of it: one mole per mole still standing.
    /// </summary>
    /// <remarks>
    /// Goes in the spare cell of a three-player split where there is one, along the top seam
    /// when the screen is split, and along the bottom of a shared view, which is the one edge
    /// nothing else wants.
    /// </remarks>
    private void DrawTally(Vector2 viewport)
    {
        if (_state.Standing is null)
        {
            return;
        }

        if (_state.Split && !_state.HasSpareCell)
        {
            // Four panes leave no spare cell and no room for a four-row block, so the tally
            // lies along the top seam as a single strip instead.
            DrawTallyStrip(viewport);
            return;
        }

        int seats = _state.Standing.Length;
        float glyph = _state.HasSpareCell ? 26f : 17f;
        float rowHeight = glyph * 1.5f;
        float width = (glyph * 1.1f * MatchSettings.MolesPerPlatoon) + (glyph * 2.2f);
        float height = rowHeight * seats;

        Vector2 origin = _state.HasSpareCell
            ? _state.SpareCell.Position
                + ((_state.SpareCell.Size - new Vector2(width, height)) / 2f)
            : new Vector2((viewport.X - width) / 2f, viewport.Y - height - 14f);

        DrawRect(
            new Rect2(origin - new Vector2(glyph * 0.5f, glyph * 0.4f),
                new Vector2(width + glyph, height + (glyph * 0.8f))),
            Palette.Panel);

        for (int seat = 0; seat < seats; seat++)
        {
            float y = origin.Y + (seat * rowHeight) + (rowHeight / 2f);
            Color colour = Palette.Seat(seat);

            // A tick beside a platoon that has already committed, so the table can see at a
            // glance who everybody is waiting for.
            if (_state.Committed is not null && seat < _state.Committed.Length && _state.Committed[seat])
            {
                Glyphs.Committed(this, new Vector2(origin.X + (glyph * 0.6f), y), glyph * 0.8f, colour);
            }
            else
            {
                DrawCircle(new Vector2(origin.X + (glyph * 0.6f), y), glyph * 0.22f, new Color(colour, 0.5f));
            }

            for (int mole = 0; mole < MatchSettings.MolesPerPlatoon; mole++)
            {
                Vector2 at = new Vector2(
                    origin.X + (glyph * 1.7f) + (mole * glyph * 1.1f), y);

                if (mole < _state.Standing[seat])
                {
                    Glyphs.Mole(this, at, glyph, colour);
                }
                else
                {
                    DrawArc(at, glyph * 0.36f, 0, Mathf.Tau, 14, new Color(colour, 0.3f), 1.5f);
                }
            }
        }
    }

    /// <summary>
    /// The tally as one strip along the top seam: four platoons in a row, each with a tick if
    /// it has already committed. Compact enough to sit between two panes' instruments.
    /// </summary>
    private void DrawTallyStrip(Vector2 viewport)
    {
        int seats = _state.Standing.Length;
        float glyph = 15f;
        float group = (glyph * 1.05f * MatchSettings.MolesPerPlatoon) + (glyph * 1.4f);
        float width = group * seats;
        float height = glyph * 1.9f;
        Vector2 origin = new Vector2(
            (viewport.X - width) / 2f, Mathf.Max(_state.TopClearance, 6f));

        DrawRect(
            new Rect2(origin - new Vector2(glyph * 0.4f, 0), new Vector2(width + (glyph * 0.8f), height)),
            Palette.Panel);

        for (int seat = 0; seat < seats; seat++)
        {
            float left = origin.X + (seat * group);
            float y = origin.Y + (height / 2f);
            Color colour = Palette.Seat(seat);

            if (_state.Committed is not null && seat < _state.Committed.Length && _state.Committed[seat])
            {
                Glyphs.Committed(this, new Vector2(left + (glyph * 0.5f), y), glyph * 0.8f, colour);
            }
            else
            {
                DrawCircle(new Vector2(left + (glyph * 0.5f), y), glyph * 0.2f, new Color(colour, 0.55f));
            }

            for (int mole = 0; mole < MatchSettings.MolesPerPlatoon; mole++)
            {
                Vector2 at = new Vector2(left + (glyph * 1.3f) + (mole * glyph * 1.05f), y);

                if (mole < _state.Standing[seat])
                {
                    Glyphs.Mole(this, at, glyph, colour);
                }
                else
                {
                    DrawArc(at, glyph * 0.34f, 0, Mathf.Tau, 14, new Color(colour, 0.3f), 1.5f);
                }
            }
        }
    }

    private void DrawWind(Vector2 viewport)
    {
        Vector2 at = new Vector2(viewport.X - 60f, 32f);

        DrawRect(new Rect2(at.X - 50f, at.Y - 22f, 100f, 44f), Palette.Panel);
        Glyphs.Wind(
            this, at, 40f, _state.Wind / (float)MatchSettings.MaxWindSpeed.ToDecimal(),
            Palette.OnPanel);
    }

    /// <summary>
    /// The shared clock, counting the planning phase down for everybody at once.
    /// </summary>
    /// <remarks>
    /// A ring, and no digits. The design reserves numerals for damage, on the grounds that a
    /// numeral is the one mark that reads the same in every language a seven-year-old might
    /// have, and spending that on a countdown would be a waste of the exception. An emptying
    /// ring says the same thing and says it faster.
    ///
    /// It goes dead centre when the screen is split, which is the seam and belongs to the table
    /// rather than to any pane, and at the top of a shared view, where the middle is the game.
    /// </remarks>
    private void DrawClock(Vector2 viewport)
    {
        if (_state.ClockLeft < 0 || _state.ClockLength <= 0)
        {
            return;
        }

        float radius = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.05f, 20f, 46f);
        Vector2 at = _state.Split
            ? viewport / 2f
            : new Vector2(viewport.X / 2f, radius + 14f);

        float left = Mathf.Clamp(_state.ClockLeft / _state.ClockLength, 0f, 1f);
        bool pressing = _state.ClockLeft <= 5f;

        DrawCircle(at, radius * 1.12f, Palette.Panel);
        DrawArc(at, radius * 0.78f, 0, Mathf.Tau, 40, new Color(1, 1, 1, 0.15f), radius * 0.22f);
        DrawArc(
            at, radius * 0.78f, -Mathf.Pi / 2f, (-Mathf.Pi / 2f) + (Mathf.Tau * left), 40,
            pressing ? Palette.Damage : Palette.OnPanel, radius * 0.22f);

        // An hourglass in the middle of the ring, so what it is counting is never in doubt.
        Glyphs.Time(
            this, at, radius * 0.8f,
            pressing ? Palette.Damage : new Color(Palette.OnPanel, 0.75f));
    }

    /// <summary>Who took the flowerbed, as their colour filling the centre.</summary>
    private void DrawOutcome(Vector2 viewport)
    {
        if (_state.Winner == -2)
        {
            return;
        }

        Vector2 at = viewport / 2f;
        float radius = Mathf.Min(viewport.X, viewport.Y) * 0.12f;

        DrawCircle(at, radius, Palette.Panel);

        if (_state.Winner < 0)
        {
            // Everybody went out together, which with simultaneous turns is a real result and
            // not an error. Four snouts, no winner.
            for (int seat = 0; seat < 4; seat++)
            {
                float angle = seat * Mathf.Tau / 4f;
                Glyphs.Mole(
                    this,
                    at + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * 0.5f,
                    radius * 0.45f, new Color(Palette.Seat(seat), 0.5f));
            }

            return;
        }

        Glyphs.Mole(this, at, radius * 1.2f, Palette.Seat(_state.Winner));
    }
}
