using Godot;
using MoleSim.Match;

/// <summary>
/// Programmer-art instrumentation for the playtest build.
/// </summary>
/// <remarks>
/// The shipping HUD is wordless, and that is a Phase 3 job with an icon set behind it. This
/// is its opposite on purpose: it names everything, because the thing under test is whether
/// the loop is fun, and a tester squinting at an unlabelled glyph gives feedback about the
/// glyph instead.
///
/// The two gauges are the exception. Stamina and time are the whole planning decision, and
/// they are bars here because they will be bars in the finished game, so what testers react
/// to is the real thing rather than a stand-in for it.
///
/// Panels are ink on purpose. The sky is the document's paper cream, so a cream panel over
/// it is invisible, which is exactly the bug the first render of this screen had.
/// </remarks>
public partial class MatchHud : Control
{
    public struct State
    {
        public string Beat;
        public int Round;
        public int Seat;
        public Color SeatColour;
        public WeaponId Weapon;
        public int MoleIndex;
        public float StaminaSpent;
        public float StaminaTotal;
        public int TicksUsed;
        public bool OverBudget;
        public int ResetsLeft;

        /// <summary>How far through the hold-to-reset gesture, from zero to one.</summary>
        public float ResetHeld;

        public bool HasShot;
        public float Wind;
        public int[] Standing;
        public int LastRoundDamage;
        public int Winner;
    }

    /// <summary>
    /// How much of the screen the panels take. Public because the world has to be framed
    /// between them: anything the camera puts behind a panel might as well not be drawn.
    /// </summary>
    public const float StripHeight = 74f;

    public const float PromptHeight = 42f;

    private static readonly Color Panel = new Color(0.18f, 0.14f, 0.10f, 0.90f);
    private static readonly Color Text = new Color(0.949f, 0.945f, 0.894f);
    private static readonly Color Dim = new Color(0.949f, 0.945f, 0.894f, 0.55f);

    private static readonly Color[] SeatColours =
    {
        new Color(0.294f, 0.545f, 0.231f),
        new Color(0.780f, 0.353f, 0.157f),
        new Color(0.306f, 0.510f, 0.651f),
        new Color(0.769f, 0.165f, 0.047f),
    };

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

        DrawTopStrip(viewport.X);
        DrawPlatoons();
        DrawPrompt(viewport);
    }

    private void DrawTopStrip(float width)
    {
        Font font = ThemeDB.FallbackFont;

        DrawRect(new Rect2(0, 0, width, StripHeight), Panel);

        DrawRect(new Rect2(14, 12, 20, 20), _state.SeatColour);
        DrawString(
            font, new Vector2(44, 30),
            $"Round {_state.Round}   Seat {_state.Seat + 1}   Mole {_state.MoleIndex + 1}",
            HorizontalAlignment.Left, -1, 19, Text);
        DrawString(
            font, new Vector2(44, 56), $"{_state.Weapon}   Q/E changes it",
            HorizontalAlignment.Left, -1, 14, Dim);

        // The two numbers the whole planning decision hangs on. Time is how much of the
        // eight seconds the route eats; puff is whether the digging in it is affordable.
        float gaugeX = width - 350;

        DrawGauge(
            font, gaugeX, 12, "Time",
            Mathf.Clamp(_state.TicksUsed / (float)MatchSettings.TicksPerRound, 0f, 1f),
            new Color(0.306f, 0.510f, 0.651f));

        DrawGauge(
            font, gaugeX, 40, "Puff",
            _state.StaminaTotal <= 0
                ? 0f
                : Mathf.Clamp(_state.StaminaSpent / _state.StaminaTotal, 0f, 1f),
            _state.OverBudget
                ? new Color(0.769f, 0.165f, 0.047f)
                : new Color(0.435f, 0.647f, 0.325f));

        // Wind, as an arrow whose length is its strength. Nothing else about a launcher's
        // arc matters as much, and nothing else is as easy to forget.
        Vector2 windAt = new Vector2(width - 70, 37);
        DrawString(font, windAt + new Vector2(-4, -14), "Wind", HorizontalAlignment.Center, -1, 12, Dim);
        DrawLine(windAt, windAt + new Vector2(_state.Wind * 6f, 0), Text, 3f);
        DrawCircle(windAt, 3.5f, Text);
    }

    private void DrawGauge(Font font, float x, float y, string label, float fraction, Color fill)
    {
        DrawString(font, new Vector2(x, y + 15), label, HorizontalAlignment.Left, -1, 14, Dim);
        DrawRect(new Rect2(x + 42, y, 180, 18), new Color(1, 1, 1, 0.12f));
        DrawRect(new Rect2(x + 42, y, 180 * fraction, 18), fill);
    }

    /// <summary>Who is still standing, as one dot per mole. The score, and the whole of it.</summary>
    private void DrawPlatoons()
    {
        if (_state.Standing is null)
        {
            return;
        }

        float top = StripHeight + 10;
        DrawRect(new Rect2(0, top, 118, _state.Standing.Length * 24), Panel);

        for (int seat = 0; seat < _state.Standing.Length; seat++)
        {
            float y = top + 8 + (seat * 24);
            DrawRect(new Rect2(12, y, 12, 12), SeatColours[seat]);

            for (int mole = 0; mole < MatchSettings.MolesPerPlatoon; mole++)
            {
                Vector2 at = new Vector2(38 + (mole * 18), y + 6);

                if (mole < _state.Standing[seat])
                {
                    DrawCircle(at, 5f, SeatColours[seat]);
                }
                else
                {
                    DrawArc(at, 5f, 0, Mathf.Tau, 12, new Color(SeatColours[seat], 0.35f), 1.5f);
                }
            }
        }
    }

    private void DrawPrompt(Vector2 viewport)
    {
        Font font = ThemeDB.FallbackFont;

        string prompt = _state.Beat switch
        {
            "Planning" => _state.HasShot
                ? "Drag a route  |  right-drag to re-aim  |  hold R to reset  |  SPACE commits"
                : "Drag a route  |  right-drag and release to stamp the shot  |  SPACE commits",
            "Resolving" => "Everything happens at once. SPACE skips.",
            "Aftermath" => $"That round dealt {_state.LastRoundDamage}. SPACE for the next one.",
            _ => _state.Winner >= 0
                ? $"Seat {_state.Winner + 1} takes the flowerbed."
                : "Everybody went out together.",
        };

        float top = viewport.Y - PromptHeight;
        DrawRect(new Rect2(0, top, viewport.X, PromptHeight), Panel);
        DrawString(
            font, new Vector2(14, top + 27), prompt, HorizontalAlignment.Left, -1, 15, Text);

        if (_state.Beat != "Planning")
        {
            return;
        }

        // The reset tokens: one a turn, more out of the crates. The design calls this the
        // most watched pixel on the screen, so it gets to be obvious, and the ring filling
        // up shows the hold registering.
        int shown = Mathf.Max(_state.ResetsLeft, 1);

        for (int token = 0; token < shown; token++)
        {
            Vector2 at = new Vector2(viewport.X - 30 - (token * 32), top + (PromptHeight / 2f));

            DrawCircle(
                at, 11f,
                token < _state.ResetsLeft
                    ? new Color(0.769f, 0.165f, 0.047f)
                    : new Color(1, 1, 1, 0.18f));

            if (token == 0 && _state.ResetHeld > 0 && _state.ResetsLeft > 0)
            {
                DrawArc(
                    at, 15f, -Mathf.Pi / 2f,
                    (-Mathf.Pi / 2f) + (Mathf.Tau * Mathf.Min(_state.ResetHeld, 1f)),
                    24, Text, 3f);
            }
        }
    }
}
