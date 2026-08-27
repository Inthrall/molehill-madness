using Godot;
using MoleSim.Match;
using WeaponId = MoleSim.Match.WeaponId;

/// <summary>What a thumb landed on.</summary>
public enum TouchTarget
{
    /// <summary>The map, which pans and pinches and is not part of the plan.</summary>
    None = 0,

    /// <summary>The weapon wheel, which is flicked up and down.</summary>
    Wheel = 1,

    /// <summary>Hold to aim, release to stamp the shot.</summary>
    Fire = 2,

    /// <summary>The second charge, which does not spend the turn's shot.</summary>
    Dynamite = 3,

    /// <summary>Hold to tear the whole turn up.</summary>
    Reset = 4,

    /// <summary>Done. Lock it in and stop watching the clock.</summary>
    Commit = 5,

    /// <summary>Book a hop for this moment of the walk.</summary>
    Hop = 6,

    /// <summary>The movement stick, which walks the mole.</summary>
    Stick = 8,
}

/// <summary>
/// The phone layout: a stick that walks the mole, a wheel you flick, and a button each for the
/// things that happen at a moment.
/// </summary>
/// <remarks>
/// The first version had the map itself take a drag, because the route was drawn rather than
/// walked. That is gone: the mole is steered with the stick, and a drag on the map now pans the
/// camera, which is what a drag on a map means everywhere else in the world. Two fingers zoom.
///
/// The stick goes bottom left and everything that fires or commits goes bottom right, so the two
/// thumbs never reach across each other. The things that shape the plan sit on a shelf just above
/// the stick, within reach of the same thumb that is already steering, because that is the hand
/// that knows when the moment has arrived.
///
/// This draws the controls and reports what was touched. It never touches the match: the scene
/// routes a hit here into the same handful of verbs a keyboard reaches, so the rules cannot tell a
/// thumb from a key.
/// </remarks>
public partial class TouchControls : Control
{
    private Vector2 _fire;
    private Vector2 _dynamite;
    private Vector2 _reset;
    private Vector2 _commit;
    private Vector2 _hop;
    private Vector2 _stickHome;
    private Rect2 _wheel;
    private float _button;
    private float _small;
    private float _stickRadius;
    private float _glyph;

    private TouchTarget _pressed = TouchTarget.None;

    /// <summary>The plan being made, so the controls can show what is on it.</summary>
    public SeatPlanner? Planner { get; set; }

    /// <summary>How far the fire button has been dragged, for the aim readout.</summary>
    public Vector2 AimDrag { get; set; }

    /// <summary>How far the stick has been pushed, for the knob.</summary>
    public Vector2 StickPush { get; set; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Places the controls for the screen it finds itself on.</summary>
    public void LayOut(Vector2 screen)
    {
        // Sized off the shorter edge, so the buttons stay thumb-sized in either orientation
        // and on either a phone or a tablet.
        _button = Mathf.Clamp(Mathf.Min(screen.X, screen.Y) * 0.085f, 34f, 76f);
        _small = _button * 0.68f;
        _stickRadius = _button * 1.45f;
        _glyph = _button * 1.05f;

        float margin = _button * 0.55f;
        float right = screen.X - margin - _button;
        float bottom = screen.Y - margin - _button;

        // Everything that fires or commits goes under the right thumb.
        _fire = new Vector2(right, bottom);
        _dynamite = new Vector2(right - (_button * 2.2f), bottom);
        _commit = new Vector2(right, bottom - (_button * 2.2f));

        // The stick takes the bottom left corner, where a thumb rests without being told to.
        _stickHome = new Vector2(
            margin + _stickRadius, screen.Y - margin - _stickRadius);

        // Hop and reset go in a small column up the left edge, above the stick and clear of its
        // grab ring. They were a row across the middle of the screen to begin with, at full size,
        // which put dinner plates over the one part of the picture the player is trying to aim
        // into. They are pressed once or twice a turn, so they can be small and slightly out of
        // the way.
        //
        // Reset is the further of the two on purpose. It is the one press nobody wants to make by
        // accident, and it is a hold rather than a tap, so a little reach costs it nothing.
        float column = margin + _small;
        float lowest = _stickHome.Y - (_stickRadius * StickGrab) - (_small * 1.2f);
        float gap = _small * 2.3f;

        _hop = new Vector2(column, lowest);
        _reset = new Vector2(column, lowest - gap);

        // The wheel runs up the right edge above the buttons, clear of the topmost of them by
        // a margin, so a flick can never be mistaken for a press. Overlapping them was the
        // first attempt and it made the commit button eat the bottom of the wheel.
        float wheelHeight = _button * 4.2f;
        _wheel = new Rect2(
            right - _button,
            _commit.Y - (_button * 1.4f) - wheelHeight,
            _button * 2f,
            wheelHeight);
    }

    /// <summary>What is under a touch, so the scene knows whether it is a control or the map.</summary>
    public TouchTarget Hit(Vector2 at)
    {
        // The small column is tested before the stick. Its lowest button sits just outside the
        // stick's grab ring, and a thumb reaching for it lands a little short as often as not.
        if (Within(at, _hop, _small))
        {
            return TouchTarget.Hop;
        }

        if (Within(at, _reset, _small))
        {
            return TouchTarget.Reset;
        }

        // The stick with a generous margin. A thumb that lands a little outside it meant to steer,
        // and the alternative reading is a camera pan the player did not ask for.
        if (at.DistanceTo(_stickHome) <= _stickRadius * StickGrab)
        {
            return TouchTarget.Stick;
        }

        if (Within(at, _fire, _button))
        {
            return TouchTarget.Fire;
        }

        if (Within(at, _dynamite, _button))
        {
            return TouchTarget.Dynamite;
        }

        if (Within(at, _commit, _button))
        {
            return TouchTarget.Commit;
        }

        return _wheel.HasPoint(at) ? TouchTarget.Wheel : TouchTarget.None;
    }

    public void Press(TouchTarget target)
    {
        _pressed = target;
        QueueRedraw();
    }

    public void Release()
    {
        _pressed = TouchTarget.None;
        AimDrag = Vector2.Zero;
        StickPush = Vector2.Zero;
        QueueRedraw();
    }

    /// <summary>Where the fire button sits, which is where an aim is dragged from.</summary>
    public Vector2 FireAt => _fire;

    /// <summary>Where the stick sits, which is what a push is measured from.</summary>
    public Vector2 StickAt => _stickHome;

    /// <summary>How far the stick has to move for a full-speed push.</summary>
    public float StickTravel => _stickRadius;

    /// <summary>How far the stick has to travel up or down the wheel to turn it one notch.</summary>
    public float WheelNotch => _button * 0.8f;

    /// <summary>How far outside the stick's ring still counts as grabbing it.</summary>
    private const float StickGrab = 1.35f;

    private static bool Within(Vector2 at, Vector2 centre, float radius) =>
        at.DistanceTo(centre) <= radius;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        if (Planner is null || !Planner.IsPlanning)
        {
            return;
        }

        DrawStick();
        DrawWheel();
        DrawButton(_fire, TouchTarget.Fire, _button);
        DrawButton(_dynamite, TouchTarget.Dynamite, _button);
        DrawButton(_commit, TouchTarget.Commit, _button);
        DrawButton(_reset, TouchTarget.Reset, _small);
        DrawButton(_hop, TouchTarget.Hop, _small);
        DrawAimStick();
    }

    /// <summary>
    /// The movement stick: a ring to push out of, with a mole on the knob.
    /// </summary>
    /// <remarks>
    /// The mole on the knob is the whole instruction. Wordless is the direction, and the one thing
    /// a new player has to be told is which control is them, so the control wears it.
    ///
    /// It greys out when the round's eight seconds of walking are gone, which is the only way that
    /// fact can be reported without a sentence: the gauge above says the budget is full, and the
    /// stick says pushing will not achieve anything.
    /// </remarks>
    private void DrawStick()
    {
        bool spent = !Planner!.HasTimeLeft;
        Color rim = spent ? Palette.OnPanelDim : new Color(Palette.OnPanel, 0.4f);

        DrawCircle(_stickHome, _stickRadius, new Color(Palette.Panel, 0.75f));
        DrawArc(_stickHome, _stickRadius, 0, Mathf.Tau, 40, rim, 2f);

        // Four ticks around the rim, so it reads as something to push in any direction rather
        // than a button. Down is a dig, which is worth the hint of it being a real direction.
        for (int quarter = 0; quarter < 4; quarter++)
        {
            float angle = quarter * Mathf.Pi / 2f;
            Vector2 outward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            DrawLine(
                _stickHome + (outward * _stickRadius * 0.82f),
                _stickHome + (outward * _stickRadius * 0.96f), rim, 2f);
        }

        Vector2 knob = _stickHome + StickPush.LimitLength(_stickRadius);
        float knobRadius = _button * 0.62f;

        DrawCircle(knob, knobRadius, spent ? Palette.OnPanelDim : Palette.Panel);
        DrawArc(
            knob, knobRadius, 0, Mathf.Tau, 28,
            _pressed == TouchTarget.Stick ? Palette.OnPanel : rim, 2f);
        Glyphs.Mole(
            this, knob, knobRadius * 1.5f,
            spent ? new Color(Palette.OnPanel, 0.3f) : Palette.OnPanel);
    }

    /// <summary>
    /// The wheel: what is selected, large and in the middle, with its neighbours above and
    /// below to show which way it turns.
    /// </summary>
    private void DrawWheel()
    {
        SeatPlanner planner = Planner!;
        int at = System.Array.IndexOf(Arsenal.Wheel, planner.Weapon);
        Vector2 centre = _wheel.Position + (_wheel.Size / 2f);
        float spacing = _wheel.Size.Y / 3.1f;

        DrawRect(_wheel, Palette.Panel);

        for (int step = -1; step <= 1; step++)
        {
            int index = ((at + step) % Arsenal.Wheel.Length + Arsenal.Wheel.Length)
                % Arsenal.Wheel.Length;
            Vector2 slot = centre + new Vector2(0, step * spacing);
            bool selected = step == 0;

            // The neighbours were at sixty percent to begin with and were unreadable, which
            // defeats the point of showing them: a wheel you cannot see the next notch of is
            // just a button that changes to something unpredictable.
            float size = _glyph * (selected ? 1.05f : 0.78f);

            if (selected)
            {
                DrawCircle(slot, size * 0.68f, new Color(Palette.OnPanel, 0.16f));
            }

            Glyphs.Weapon(
                this, Arsenal.Wheel[index], slot, size,
                selected ? Palette.OnPanel : new Color(Palette.OnPanel, 0.42f));
        }

        // Chevrons, so it reads as something to flick rather than something to press.
        Chevron(_wheel.Position + new Vector2(_wheel.Size.X / 2f, 7f), -1);
        Chevron(_wheel.Position + new Vector2(_wheel.Size.X / 2f, _wheel.Size.Y - 7f), 1);
    }

    private void Chevron(Vector2 at, float direction)
    {
        float reach = _button * 0.2f;

        DrawColoredPolygon(
            new[]
            {
                at + new Vector2(0, reach * direction),
                at + new Vector2(-reach * 0.8f, -reach * 0.4f * direction),
                at + new Vector2(reach * 0.8f, -reach * 0.4f * direction),
            },
            new Color(Palette.OnPanel, 0.35f));
    }

    private void DrawButton(Vector2 at, TouchTarget target, float radius)
    {
        SeatPlanner planner = Planner!;
        bool down = _pressed == target;
        float glyph = radius * (_glyph / _button);

        DrawCircle(at, radius, down ? new Color(Palette.OnPanel, 0.22f) : Palette.Panel);
        DrawArc(
            at, radius, 0, Mathf.Tau, 32,
            down ? Palette.OnPanel : new Color(Palette.OnPanel, 0.3f),
            down ? 3f : 2f);

        switch (target)
        {
            case TouchTarget.Fire:
                Glyphs.Fire(this, at, glyph, Palette.Damage);
                break;

            case TouchTarget.Dynamite:
                // Dimmed when there are none left rather than hidden, so its absence is
                // legible as "spent" rather than as a layout that moved.
                Glyphs.Dynamite(
                    this, at, glyph * 0.9f,
                    planner.HasCharges ? Palette.OnPanel : Palette.OnPanelDim);
                DrawCount(at, radius, planner.Stock(WeaponId.BoomBeets), Palette.OnPanel);
                break;

            case TouchTarget.Commit:
                Glyphs.Committed(this, at, glyph, new Color(0.435f, 0.647f, 0.325f));
                break;

            case TouchTarget.Hop:
                Glyphs.Hop(
                    this, at, glyph,
                    planner.Hops.Count < SeatPlanner.MaxHops
                        ? Palette.OnPanel
                        : Palette.OnPanelDim);
                DrawCount(
                    at, radius, SeatPlanner.MaxHops - planner.Hops.Count, Palette.OnPanel);
                break;

            case TouchTarget.Reset:
                DrawReset(at, radius, glyph);
                break;

            default:
                break;
        }
    }

    /// <summary>How many are left, as pips around a button's rim rather than as a digit.</summary>
    private void DrawCount(Vector2 at, float radius, int count, Color ink)
    {
        for (int pip = 0; pip < Mathf.Min(count, 5); pip++)
        {
            float angle = -2.2f + (pip * 0.42f);

            DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * 1.08f),
                Mathf.Max(radius * 0.11f, 2f), ink);
        }
    }

    private void DrawReset(Vector2 at, float radius, float glyph)
    {
        SeatPlanner planner = Planner!;
        bool spent = planner.ResetsLeft <= 0;

        Glyphs.Reset(this, at, glyph, spent ? Palette.OnPanelDim : Palette.Damage);

        if (planner.ResetHeld > 0 && !spent)
        {
            DrawArc(
                at, radius * 0.86f, -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * (float)Mathf.Min(planner.ResetHeld, 1)),
                28, Palette.OnPanel, 4f);
        }

        DrawCount(at, radius, planner.ResetsLeft, Palette.Damage);
    }

    /// <summary>
    /// The aim, while the fire button is held: a stick pulled out of the button, whose length
    /// is the charge. One thumb, one gesture, direction and power together.
    /// </summary>
    private void DrawAimStick()
    {
        if (_pressed != TouchTarget.Fire || AimDrag.LengthSquared() < 1f)
        {
            return;
        }

        float reach = Mathf.Min(AimDrag.Length(), _button * 2.4f);
        Vector2 tip = _fire + (AimDrag.Normalized() * reach);

        DrawLine(_fire, tip, Palette.Damage, 5f);
        DrawCircle(tip, _button * 0.3f, Palette.Damage);
        DrawArc(_fire, _button * 2.4f, 0, Mathf.Tau, 40, new Color(Palette.Damage, 0.3f), 2f);
    }
}
