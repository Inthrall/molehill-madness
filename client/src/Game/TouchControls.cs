using Godot;
using MoleSim.Match;
using WeaponId = MoleSim.Match.WeaponId;

/// <summary>What a thumb landed on.</summary>
public enum TouchTarget
{
    /// <summary>The map, which pans and pinches and is not part of the plan.</summary>
    None = 0,

    /// <summary>The attack wheel, which is flicked up and down.</summary>
    Wheel = 1,

    /// <summary>The movement wheel, alongside it and flicked the same way.</summary>
    Abilities = 7,

    /// <summary>
    /// Use the movement ability, which is fire for the other allowance.
    /// </summary>
    /// <remarks>
    /// Nine, because eight is the stick. These are numbered explicitly and out of order, having had
    /// two values vacated rather than reused, and picking the next one by looking at the line above
    /// gave this the stick's number: a press on the ability button would have grabbed the joystick.
    /// The compiler caught it only because both ended up in one switch. Read the whole list.
    /// </remarks>
    Ability = 9,

    /// <summary>Hold to aim, release to stamp the shot.</summary>
    Fire = 2,

    // 3 was Dynamite, a button for planting the Boom Beets. The beets are on the wheel now, so the
    // button is gone. The number is left vacant rather than reused, so nothing that persisted a
    // target can come back meaning something else.

    /// <summary>Hold to tear the whole turn up.</summary>
    Reset = 4,

    /// <summary>Done. Lock it in and stop watching the clock.</summary>
    Commit = 5,

    /// <summary>Book a hop for this moment of the walk.</summary>
    Hop = 6,

    /// <summary>The movement stick, which walks the mole.</summary>
    Stick = 8,

    /// <summary>
    /// The settings button, which is what escape does on a keyboard.
    /// </summary>
    /// <remarks>
    /// Ten, following the rule written on <see cref="Ability"/>: the next number, chosen by reading
    /// the whole list rather than the line above.
    /// </remarks>
    Settings = 10,
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
    private Vector2 _reset;
    private Vector2 _commit;
    private Vector2 _hop;
    private Vector2 _ability;
    private Vector2 _settings;
    private Vector2 _stickHome;
    private Rect2 _wheel;
    private Rect2 _abilities;
    private float _button;
    private float _small;
    private float _stickRadius;
    private float _glyph;

    private TouchTarget _pressed = TouchTarget.None;

    /// <summary>The plan being made, so the controls can show what is on it.</summary>
    public SeatPlanner? Planner { get; set; }

    /// <summary>
    /// How much of the bottom right corner something else has claimed, in pixels.
    /// </summary>
    /// <remarks>
    /// The emote wheel, and only in an online match. Told rather than guessed, because this class
    /// cannot see the wheel and the wheel cannot see these buttons, and the alternative to one
    /// number is two layouts drifting apart until somebody plays a match on a phone and finds that
    /// ending a turn opens the emotes.
    /// </remarks>
    public float CornerSpoken { get; set; }

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

        // Fire in the middle of the bottom edge, which is a deliberate move out of the corner it
        // used to sit in. Two reasons, and the second is the one that decided it.
        //
        // It is the one control a new player has to find, and a corner is where a game puts the
        // things you already know are there. More than that, firing here is a press and a drag: the
        // direction comes out of which way the thumb moves off the button, so the button is the
        // origin of the gesture, and an origin in a corner can only be dragged into one quadrant.
        // From the middle it can be dragged anywhere, which is what the gesture actually means.
        //
        // It costs a reach. Firing is a considered press once a turn rather than a rapid one, so a
        // deliberate reach is the right price, and the two presses that do want to be under a
        // resting thumb keep the corner.
        _fire = new Vector2(screen.X / 2f, bottom);

        // The movement allowance gets a button of its own, beside fire, because it is the same kind
        // of press for the other half of the turn. Without one it was reachable only by turning the
        // second wheel, which armed the ability and un-greyed the single fire button: correct, and
        // undiscoverable. A turn gets two uses and the screen should show two ways to spend them.
        //
        // Outboard of fire and a little smaller, on the side the rest of the turn's furniture is on.
        // It sat on fire's other side to begin with, alone in the left half of the bottom edge with
        // the stick, which made the two thumbs cross: the left one steers and the right one spends
        // the turn, and the ability is spent rather than steered. Fire is the one every turn spends
        // and this is the one some turns spend, and the size difference is the only ranking the
        // layout needs.
        _ability = new Vector2((screen.X / 2f) + (_button * 2.3f), bottom);

        // Ending the turn and jumping keep the right-hand side, stacked. Jump is on this side because
        // it is pressed several times a turn and belongs near a resting thumb, and end turn is the
        // last press of a turn so it can be the furthest.
        //
        // Lifted clear of whatever else has claimed the corner. Online that is the emote wheel, which
        // gets first refusal on every press: left where it was, the wheel's button would sit on top
        // of end turn and eat it, which is exactly the fault that moved the wheel out of the bottom
        // left in the first place.
        float floor = bottom - CornerSpoken;

        _commit = new Vector2(right, floor);
        _hop = new Vector2(right, floor - (_button * 2.2f));

        // Settings in the top right, which is the one corner nothing else wants and the corner every
        // phone puts a settings button in. A keyboard has escape for this and a phone had nothing at
        // all: the pause menu existed and there was no way to open it, so a player who wanted to
        // leave a match on a phone had to finish it or kill the process.
        _settings = new Vector2(right, margin + _small);

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

        _reset = new Vector2(column, lowest);

        // The wheel runs up the right edge above the buttons, clear of the topmost of them by
        // a margin, so a flick can never be mistaken for a press. Overlapping them was the
        // first attempt and it made the commit button eat the bottom of the wheel.
        // Two strips side by side. The attack wheel keeps the outer edge, under the thumb, because
        // it is turned every turn; the movement wheel sits inboard of it, reached deliberately, which
        // suits something used once a turn at most.
        // Above the topmost button on this side rather than above end turn, which used to be the
        // topmost and is now the lowest. Anchored to whichever it is, so moving them again cannot
        // drop the wheels onto a button: the wheels are flicked and the buttons are pressed, and a
        // flick that lands on a button is the worst of both.
        float wheelBottom = Mathf.Min(_commit.Y, _hop.Y) - (_button * 1.4f);

        // Four buttons of strip where there is room for it, and whatever is left where there is not.
        //
        // The height used to be fixed and the top derived from it, which was fine until this side of
        // the screen gained a settings button at one end and gave up a corner at the other: the strip
        // then reached its full length by climbing out of the top of the screen and through the
        // settings button, which draws first and so was simply painted over. A wheel that shrinks is
        // legible; a button that is under a wheel is gone. Clamped at its own bottom as well, so a
        // screen too short for any of this gets a strip of no height rather than an inside-out
        // rectangle that hit-tests as empty everywhere.
        float wheelTop = Mathf.Min(
            wheelBottom,
            Mathf.Max(wheelBottom - (_button * 4.2f), _settings.Y + _small + (margin * 0.6f)));

        _wheel = new Rect2(right - _button, wheelTop, _button * 2f, wheelBottom - wheelTop);
        _abilities = new Rect2(
            _wheel.Position.X - (_button * 2.1f), wheelTop, _button * 2f, wheelBottom - wheelTop);
    }

    /// <summary>What is under a touch, so the scene knows whether it is a control or the map.</summary>
    /// <summary>
    /// Where the movement control sits, so the first-run demonstration can point at the real one.
    /// </summary>
    /// <remarks>
    /// Read rather than guessed. A drawn hand demonstrating a gesture two centimetres away from the
    /// control it is demonstrating teaches the wrong thing, and the layout moves with the screen.
    /// </remarks>
    public Vector2 StickHome => _stickHome;

    /// <summary>How far the stick travels, which is how far the demonstration should drag.</summary>
    public float StickReach => _stickRadius;

    public TouchTarget Hit(Vector2 at)
    {
        // First, and the only one tested while nobody is planning. It is the door out of a match,
        // so it has to work during a replay and while waiting on somebody else's phone, which is
        // exactly when every control below it is put away.
        if (Within(at, _settings, _small))
        {
            return TouchTarget.Settings;
        }

        if (Planner is null || !Planner.IsPlanning)
        {
            return TouchTarget.None;
        }

        // The small column is tested before the stick. Its lowest button sits just outside the
        // stick's grab ring, and a thumb reaching for it lands a little short as often as not.
        if (Within(at, _hop, _button))
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

        if (Within(at, _ability, _small))
        {
            return TouchTarget.Ability;
        }

        if (Within(at, _commit, _button))
        {
            return TouchTarget.Commit;
        }

        if (_wheel.HasPoint(at))
        {
            return TouchTarget.Wheel;
        }

        return _abilities.HasPoint(at) ? TouchTarget.Abilities : TouchTarget.None;
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
        WheelSlide = 0f;
        AbilitySlide = 0f;
        QueueRedraw();
    }

    /// <summary>Where the fire button sits, which is where an aim is dragged from.</summary>
    public Vector2 FireAt => _fire;

    /// <summary>Where the stick sits, which is what a push is measured from.</summary>
    public Vector2 StickAt => _stickHome;

    /// <summary>How far the stick has to move for a full-speed push.</summary>
    public float StickTravel => _stickRadius;

    /// <summary>How far a thumb has to travel up or down the wheel to turn it one notch.</summary>
    /// <remarks>
    /// Doubled from eight tenths of a button. At the old distance an ordinary flick crossed four or
    /// five notches, which is most of the arsenal, so choosing a weapon meant overshooting and
    /// hunting back: the wheel was quick to move and slow to use. Two whole buttons of travel per
    /// notch is about a thumb's length, and a deliberate flick now moves one or two places.
    /// </remarks>
    public float WheelNotch => _button * 1.6f;

    /// <summary>
    /// How far the wheel is turned between notches, in notches, so it can be dragged rather than
    /// merely clicked.
    /// </summary>
    /// <remarks>
    /// The wheel used to jump a whole notch at a time with nothing in between, so a drag produced a
    /// run of unexplained substitutions rather than the feeling of a wheel turning. Held as a
    /// fraction rather than in pixels because the drawing wants notches and the input has pixels,
    /// and one of them has to do the division.
    /// </remarks>
    public float WheelSlide { get; set; }

    /// <summary>The same, for the movement wheel, which turns independently.</summary>
    public float AbilitySlide { get; set; }

    /// <summary>
    /// One use button: the weapon its wheel is showing, dimmed when the allowance is spent.
    /// </summary>
    /// <remarks>
    /// The weapon's own picture rather than a generic star, because with two buttons the question a
    /// player has is which of them does what, and the answer is on the wheel above each of them. Fire
    /// keeps the star, since an attack is an attack and the wheel beside it says which one.
    /// </remarks>
    private void Slot(
        SeatPlanner planner, UseSlot slot, Vector2 at, float glyph, float radius, bool spent)
    {
        bool live = planner.CanUse(slot);
        Color ink = live ? Palette.Damage : Palette.OnPanelDim;

        if (slot == UseSlot.Attack)
        {
            Glyphs.Fire(this, at, glyph, ink);
        }
        else
        {
            WeaponId weapon = planner.Selected(slot);

            if (weapon == WeaponId.None)
            {
                Glyphs.Icon(this, "cross", at, glyph * 0.7f, Palette.OnPanelDim);
                return;
            }

            Glyphs.Weapon(
                this, weapon, at, glyph,
                live ? Palette.OnPanel : Palette.OnPanelDim);
        }

        // How many uses are left, for the two weapons that get more than one. A single-use weapon
        // showing a permanent 1 would be noise.
        if (WeaponTable.UsesPerTurn(planner.Selected(slot)) > 1)
        {
            DrawCount(at, radius, planner.UsesLeftIn(slot), Palette.OnPanel);
        }
    }

    /// <summary>
    /// How much of the bottom of the screen the controls occupy.
    /// </summary>
    /// <remarks>
    /// The panes draw their own gauges along the bottom middle, which is where the fire button now
    /// is. Reported rather than guessed at, so moving a button cannot silently bury a gauge under it:
    /// this is the same clearance the keyboard strip reports, arriving by the same route.
    /// </remarks>
    public float Depth => (_button * 2f) + (_button * 0.55f * 2f);

    /// <summary>How far outside the stick's ring still counts as grabbing it.</summary>
    private const float StickGrab = 1.35f;

    private static bool Within(Vector2 at, Vector2 centre, float radius) =>
        at.DistanceTo(centre) <= radius;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        // Above the guard, because the settings button is not part of a turn. Everything else here
        // is a way of laying a plan and goes away when there is no plan to lay; a way out of the
        // match has to be on the screen whenever the match is.
        DrawSettings();

        if (Planner is null || !Planner.IsPlanning)
        {
            return;
        }

        DrawStick();
        DrawWheel(_wheel, UseSlot.Attack, WheelSlide);
        DrawWheel(_abilities, UseSlot.Movement, AbilitySlide);
        DrawButton(_fire, TouchTarget.Fire, _button);
        DrawButton(_ability, TouchTarget.Ability, _small);
        DrawButton(_commit, TouchTarget.Commit, _button);
        DrawButton(_reset, TouchTarget.Reset, _small);
        DrawButton(_hop, TouchTarget.Hop, _button);
        DrawAimStick();
    }

    /// <summary>
    /// The way out: the same three-button pause menu escape opens on a keyboard.
    /// </summary>
    /// <remarks>
    /// Quieter than the controls around it. It is pressed once or twice in a session, by somebody
    /// who has decided to stop rather than by somebody in the middle of a turn, and a bright button
    /// in the corner of every frame would be the loudest thing on a screen that is mostly garden.
    ///
    /// No pressed state, unlike every other button here, because there is no moment to draw one in:
    /// the press puts the pause menu over the whole screen before the next frame.
    /// </remarks>
    private void DrawSettings()
    {
        DrawCircle(_settings, _small, new Color(Palette.Panel, 0.7f));
        DrawArc(_settings, _small, 0, Mathf.Tau, 28, new Color(Palette.OnPanel, 0.25f), 2f);

        Glyphs.Icon(
            this, "settings", _settings, _small * 1.05f, new Color(Palette.OnPanel, 0.6f));
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

        // The drawn face rather than the three-circle glyph. The glyph was made before there was
        // any mole artwork and it shows: it is the one control a new player has to recognise as
        // themselves, and it was the only mole on the screen that did not look like the animal.
        // Weary when the walking is spent, level otherwise, so the knob says what the grey means.
        Strip faces = Art.Faces;
        float face = knobRadius * 1.72f;

        faces.Draw(
            this,
            new Rect2(knob.X - (face / 2f), knob.Y - (face / 2f), face, face),
            spent ? Art.Face.Weary : Art.Face.Level,
            mirrored: false,
            tint: spent ? new Color(1f, 1f, 1f, 0.45f) : Colors.White);
    }

    /// <summary>
    /// The wheel: what is selected, large and in the middle, with its neighbours above and
    /// below to show which way it turns.
    /// </summary>
    private void DrawWheel(Rect2 where, UseSlot slot, float slide)
    {
        SeatPlanner planner = Planner!;

        // What can be chosen, rather than the whole arsenal. A notch moves one place among the
        // weapons this platoon actually holds, so drawing the full wheel put things in the
        // neighbour slots that turning to them would skip straight past: the wheel showed one thing
        // above the selection and delivered another.
        System.Collections.Generic.List<WeaponId> available = planner.Available(slot);

        if (available.Count == 0)
        {
            DrawRect(where, Palette.Panel);
            return;
        }

        int at = available.IndexOf(planner.Selected(slot));

        if (at < 0)
        {
            at = 0;
        }

        Vector2 centre = where.Position + (where.Size / 2f);
        float spacing = where.Size.Y / 3.1f;

        // Sized off the strip rather than off the button, so a wheel squeezed by whatever else is
        // on this side of the screen shows smaller icons rather than overlapping ones.
        float unit = Mathf.Min(_glyph, spacing);

        DrawRect(where, Palette.Panel);

        // Which wheel holds the armed weapon, so two strips cannot both look selected.
        bool armed = WeaponTable.SlotOf(planner.Weapon) == slot;

        // Two either side rather than one, because a wheel part way between notches shows a sliver
        // of the next but one, and a slot that empties as the wheel turns reads as a fault.
        for (int step = -2; step <= 2; step++)
        {
            int index = ((at + step) % available.Count + available.Count) % available.Count;
            float offset = step + slide;

            if (Mathf.Abs(offset) > 1.6f)
            {
                continue;
            }

            Vector2 seat = centre + new Vector2(0, offset * spacing);

            // Whichever is nearest the middle is the selection, which while the wheel is between
            // notches is not necessarily the one the plan is holding yet. Fading with distance is
            // what makes the slide legible: without it the icons merely translate, and the wheel
            // looks like a list that has slipped rather than a dial being turned.
            float away = Mathf.Abs(offset);
            float lit = Mathf.Clamp(1f - away, 0f, 1f);
            bool middle = away < 0.5f;
            float size = unit * (0.78f + (0.27f * lit));

            if (middle)
            {
                DrawCircle(
                    seat, size * 0.68f,
                    new Color(Palette.OnPanel, (armed ? 0.26f : 0.1f) * lit));
            }

            Glyphs.Weapon(
                this, available[index], seat, size,
                new Color(Palette.OnPanel, 0.42f + (0.58f * lit)));
        }

        // Chevrons, so it reads as something to flick rather than something to press.
        Chevron(where.Position + new Vector2(where.Size.X / 2f, 7f), -1);
        Chevron(where.Position + new Vector2(where.Size.X / 2f, where.Size.Y - 7f), 1);
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
                Slot(planner, UseSlot.Attack, at, glyph, radius, spent: false);
                break;

            case TouchTarget.Ability:
                Slot(planner, UseSlot.Movement, at, glyph * 0.86f, radius, spent: false);
                break;

            case TouchTarget.Commit:
                // Ghosted until the turn has fired, so the obvious next press is the one that has
                // not happened yet. Not hidden: a turn spent walking somewhere is a legitimate turn
                // and hiding the only way to end it would trap whoever wanted one.
                Glyphs.Committed(
                    this, at, glyph,
                    planner.HasAttacked
                        ? new Color(0.435f, 0.647f, 0.325f)
                        : new Color(0.435f, 0.647f, 0.325f, 0.3f));
                break;

            case TouchTarget.Hop:
                Glyphs.Jump(
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
    /// The aim, while the fire button is held: a stick out of the button saying which way, and the
    /// ring around it filling as the throw winds up.
    /// </summary>
    /// <remarks>
    /// The stick's length used to be the charge, one thumb spending one gesture on direction and
    /// power at once. That stopped being true when the power became a hold, and leaving the length
    /// alone would have been worse than a dead control rather than merely useless: a thumb an inch
    /// out and a thumb at full stretch would have drawn two different pictures of the same shot.
    ///
    /// So the stick reaches the ring whichever way it is pushed, and the ring fills the way the
    /// reset's does. A hold on this screen announces itself by visibly filling, and there is no
    /// reason for the fire button to say it in a second language. The fill is read off the planner
    /// rather than off the clock here, so the clamp at either end of a throw is in it.
    /// </remarks>
    private void DrawAimStick()
    {
        if (_pressed != TouchTarget.Fire || AimDrag.LengthSquared() < 1f)
        {
            return;
        }

        float reach = _button * 2.4f;
        Vector2 tip = _fire + (AimDrag.Normalized() * reach);

        DrawArc(_fire, reach, 0, Mathf.Tau, 40, new Color(Palette.Damage, 0.3f), 2f);

        float charged = (float)(Planner?.AimCharge ?? 0);

        DrawArc(
            _fire, reach, -Mathf.Pi / 2f, (-Mathf.Pi / 2f) + (Mathf.Tau * charged),
            40, Palette.Damage, 5f);

        DrawLine(_fire, tip, Palette.Damage, 5f);
        DrawCircle(tip, _button * 0.3f, Palette.Damage);
    }
}
