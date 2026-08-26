using Godot;
using MoleSim.Match;
using WeaponId = MoleSim.Match.WeaponId;

/// <summary>What a thumb landed on.</summary>
public enum TouchTarget
{
    /// <summary>The map. Laying ink, in other words.</summary>
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

    /// <summary>Arm hop placement, then tap the route.</summary>
    Hop = 6,

    /// <summary>Dig in where the route ends.</summary>
    Brace = 7,
}

/// <summary>
/// The phone layout: a weapon wheel you flick, a button to fire, one to plant, one to reset
/// and one to commit.
/// </summary>
/// <remarks>
/// Straight out of the design, which specifies exactly this and for a good reason: a phone has
/// no room for a cursor and no second button, so the gestures have to be a drag for the route
/// and a thumb for everything else. The map takes the drag; these take the taps.
///
/// Everything lives on the right, in one thumb's reach, and nothing overlaps the top strip
/// where the gauges are. Aiming is a hold on the fire button and then a drag, so direction and
/// power come out of the same gesture the way they do from a mouse, without needing a second
/// finger or a second button.
///
/// This draws the controls and reports what was touched. It never touches the match: the scene
/// routes a hit here into the same handful of verbs a mouse reaches, so the rules cannot tell
/// a thumb from a cursor.
/// </remarks>
public partial class TouchControls : Control
{
    private Vector2 _fire;
    private Vector2 _dynamite;
    private Vector2 _reset;
    private Vector2 _commit;
    private Vector2 _hop;
    private Vector2 _brace;
    private Rect2 _wheel;
    private float _button;
    private float _glyph;

    private TouchTarget _pressed = TouchTarget.None;

    /// <summary>The plan being laid, so the wheel can show what is on it.</summary>
    public SeatPlanner? Planner { get; set; }

    /// <summary>How far the fire button has been dragged, for the aim readout.</summary>
    public Vector2 AimDrag { get; set; }

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
        _glyph = _button * 1.05f;

        float margin = _button * 0.55f;
        float right = screen.X - margin - _button;
        float bottom = screen.Y - margin - _button;

        // Everything that fires or commits goes under the right thumb; everything that shapes
        // the plan goes under the left, which is where the hand not laying ink already is.
        _fire = new Vector2(right, bottom);
        _dynamite = new Vector2(right - (_button * 2.2f), bottom);
        _commit = new Vector2(right, bottom - (_button * 2.2f));

        float left = _button + margin;
        _reset = new Vector2(left, bottom);
        _hop = new Vector2(left + (_button * 2.2f), bottom);
        _brace = new Vector2(left, bottom - (_button * 2.2f));

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

    /// <summary>What is under a touch, so the scene knows whether it is a tap or a stroke.</summary>
    public TouchTarget Hit(Vector2 at)
    {
        if (Within(at, _fire))
        {
            return TouchTarget.Fire;
        }

        if (Within(at, _dynamite))
        {
            return TouchTarget.Dynamite;
        }

        if (Within(at, _commit))
        {
            return TouchTarget.Commit;
        }

        if (Within(at, _reset))
        {
            return TouchTarget.Reset;
        }

        if (Within(at, _hop))
        {
            return TouchTarget.Hop;
        }

        if (Within(at, _brace))
        {
            return TouchTarget.Brace;
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
        QueueRedraw();
    }

    /// <summary>Where the fire button sits, which is where an aim is dragged from.</summary>
    public Vector2 FireAt => _fire;

    /// <summary>How far a drag has to travel up or down the wheel to turn it one notch.</summary>
    public float WheelNotch => _button * 0.8f;

    private bool Within(Vector2 at, Vector2 centre) =>
        at.DistanceTo(centre) <= _button;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        if (Planner is null || !Planner.IsPlanning)
        {
            return;
        }

        DrawWheel();
        DrawButton(_fire, TouchTarget.Fire);
        DrawButton(_dynamite, TouchTarget.Dynamite);
        DrawButton(_commit, TouchTarget.Commit);
        DrawButton(_reset, TouchTarget.Reset);
        DrawButton(_hop, TouchTarget.Hop);
        DrawButton(_brace, TouchTarget.Brace);
        DrawAimStick();
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

    private void DrawButton(Vector2 at, TouchTarget target)
    {
        SeatPlanner planner = Planner!;
        bool armed = target == TouchTarget.Hop && planner.PlacingHop;
        bool active = target == TouchTarget.Brace && planner.BraceAt is not null;
        bool down = _pressed == target || armed || active;

        DrawCircle(at, _button, down ? new Color(Palette.OnPanel, 0.22f) : Palette.Panel);
        DrawArc(
            at, _button, 0, Mathf.Tau, 32,
            armed || active ? Palette.OnPanel : new Color(Palette.OnPanel, 0.3f),
            armed || active ? 3f : 2f);

        switch (target)
        {
            case TouchTarget.Fire:
                Glyphs.Fire(this, at, _glyph, Palette.Damage);
                break;

            case TouchTarget.Dynamite:
                // Dimmed when there are none left rather than hidden, so its absence is
                // legible as "spent" rather than as a layout that moved.
                Glyphs.Dynamite(
                    this, at, _glyph * 0.9f,
                    planner.HasCharges ? Palette.OnPanel : Palette.OnPanelDim);
                DrawCount(at, planner.Stock(WeaponId.BoomBeets));
                break;

            case TouchTarget.Commit:
                Glyphs.Committed(this, at, _glyph, new Color(0.435f, 0.647f, 0.325f));
                break;

            case TouchTarget.Hop:
                Glyphs.Hop(
                    this, at, _glyph,
                    planner.Hops.Count < SeatPlanner.MaxHops
                        ? Palette.OnPanel
                        : Palette.OnPanelDim);
                DrawCount(at, SeatPlanner.MaxHops - planner.Hops.Count);
                break;

            case TouchTarget.Brace:
                Glyphs.Brace(
                    this, at, _glyph * 0.9f,
                    active ? new Color(0.435f, 0.647f, 0.325f) : Palette.OnPanel);
                break;

            case TouchTarget.Reset:
                DrawReset(at);
                break;

            default:
                break;
        }
    }

    /// <summary>How many are left, as pips around a button's rim.</summary>
    private void DrawCount(Vector2 at, int count)
    {
        for (int pip = 0; pip < Mathf.Min(count, 5); pip++)
        {
            float angle = -2.2f + (pip * 0.42f);

            DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _button * 1.08f),
                _button * 0.11f, Palette.OnPanel);
        }
    }

    private void DrawReset(Vector2 at)
    {
        SeatPlanner planner = Planner!;
        bool spent = planner.ResetsLeft <= 0;

        Glyphs.Reset(
            this, at, _glyph, spent ? Palette.OnPanelDim : Palette.Damage);

        if (planner.ResetHeld > 0 && !spent)
        {
            DrawArc(
                at, _button * 0.86f, -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * (float)Mathf.Min(planner.ResetHeld, 1)),
                28, Palette.OnPanel, 4f);
        }

        // How many are left, as pips around the rim rather than a digit.
        for (int token = 0; token < planner.ResetsLeft; token++)
        {
            float angle = -2.2f + (token * 0.42f);
            DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _button * 1.08f),
                _button * 0.11f, Palette.Damage);
        }
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
