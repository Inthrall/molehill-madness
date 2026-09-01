using Godot;
using MoleSim.Match;

/// <summary>
/// What the controls do, and which weapon is loaded, for somebody who is not holding a phone.
/// </summary>
/// <remarks>
/// A phone has had a full set of controls on the screen since the touch layout arrived: a stick, a
/// weapon wheel, a fire button, plant, reset and commit, all visible and all labelled by shape. A
/// desktop has had none of it. Every one of those verbs was on a key, no key was written down
/// anywhere, and the weapon a platoon was holding was not on the screen at all, so a player at a
/// keyboard could not find out what they could do or see what they were about to fire. The phone
/// was the discoverable platform and the desktop was the one you had to be told about.
///
/// So this is the thumb layout's opposite number, and it is deliberately not the same shape. The
/// touch layout is a set of things to press; this is a set of things to read once and then ignore,
/// so it sits in one strip along the bottom, out of the map, and never asks for a press.
///
/// The letters are the one place the wordless rule bends, and it has to bend. Everything else on
/// this screen avoids text because text has a language and this game ships in all of them, but the
/// label on a key is not language: a key with Q printed on it says Q in every locale, and there is
/// no drawing that means "the Q key". Each cap is paired with a glyph that says what it does, so
/// the reading is icon-first and the letter is only the address.
///
/// Two things about it are not one strip's business, and both come from the couch.
///
/// A controller is a second set of addresses for the same verbs, and a hotseat match can have both
/// kinds of hardware on it at once: seat zero on the keyboard, seats one and up on pads. The strip
/// carries both labels per control when any pad is connected, because a legend that names only the
/// keyboard is a legend three of the four players cannot use. A pad's cap is drawn round where a
/// key's is square, which is the shape difference the hardware already has.
///
/// And the wheels belong to a player rather than to the screen. There is one strip and up to four
/// people planning at once, so a single wheel on it can only ever show one of them what they are
/// holding. When the screen is split, each pane carries its own pair of wheels, attack and
/// movement, for the platoon that pane belongs to. The strip gives its own wheel up in that case
/// rather than showing a fourth copy of one player's.
/// </remarks>
public partial class KeyGuide : Control
{
    /// <summary>One platoon's pane, and what it is planning with.</summary>
    public readonly struct Side
    {
        public Side(Rect2 pane, SeatPlanner planner, bool pad, bool holding)
        {
            Pane = pane;
            Planner = planner;
            Pad = pad;
            Holding = holding;
        }

        /// <summary>Where that platoon is watching its own mole.</summary>
        public Rect2 Pane { get; }

        public SeatPlanner Planner { get; }

        /// <summary>Whether it holds a controller rather than queueing for the pointer.</summary>
        public bool Pad { get; }

        /// <summary>Whether the pointer is with this platoon right now.</summary>
        public bool Holding { get; }
    }

    /// <summary>
    /// What a controller calls the things the keyboard has letters for.
    /// </summary>
    /// <remarks>
    /// Bound in <c>MatchScene.DriveWithGamepad</c>, and the two have to be read together: nothing
    /// in the type system ties a label here to the button that is actually polled there. Kept as
    /// named constants rather than as literals in the layout so that at least the drift is visible
    /// in one place.
    /// </remarks>
    private static class Pad
    {
        public const string Steer = "LS";

        public const string Fire = "RS";

        public const string Ability = "LT";

        public const string Hop = "Y";

        public const string Recentre = "X";

        public const string Reset = "B";

        public const string Commit = "START";

        public const string WheelBack = "LB";

        public const string WheelOn = "RB";
    }

    private SeatPlanner? _planner;
    private int _seat;
    private Side[] _sides = System.Array.Empty<Side>();
    private bool _pads;

    public KeyGuide()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Fills the layer, so Size is the rectangle it is actually drawn into. Laid out against
        // the viewport instead, an expanding stretch gives a canvas a different shape from the
        // window and everything anchored near an edge ends up off it.
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    /// <summary>
    /// Whose turn it is, whose panes want a wheel of their own, and whether any pad is in play.
    /// </summary>
    /// <param name="planner">The platoon holding the pointer, or null when nobody is.</param>
    /// <param name="seat">Which platoon that is, for the commit key's colour.</param>
    /// <param name="sides">A pane each, when the screen is split. Empty when it is not.</param>
    /// <param name="pads">Whether any platoon is planning on a controller.</param>
    public void Watch(SeatPlanner? planner, int seat, Side[] sides, bool pads)
    {
        _planner = planner;
        _seat = seat;
        _sides = sides;
        _pads = pads;

        // Still shown while the pointer's own platoon has finished, because the panes have wheels
        // on them and because the band above is measured from this. Hiding it the moment one seat
        // commits made the map jump a strip's height while three people were still planning.
        Visible = planner is not null || sides.Length > 0;
        QueueRedraw();
    }

    /// <summary>How tall the strip is, so the layout above it can keep clear.</summary>
    public static float Height(Vector2 viewport) =>
        Mathf.Clamp(viewport.Y * 0.125f, 68f, 104f);

    public override void _Draw()
    {
        Vector2 viewport = Size;

        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        foreach (Side side in _sides)
        {
            Wheels(side);
        }

        if (_planner is not null)
        {
            Strip(viewport);
        }
    }

    /// <summary>The legend along the bottom, which belongs to whoever holds the pointer.</summary>
    private void Strip(Vector2 viewport)
    {
        float tall = Height(viewport);
        float top = viewport.Y - tall;
        float glyph = tall * 0.42f;
        float middle = top + (tall / 2f);
        float margin = tall * 0.42f;
        Color seat = Palette.Seat(_seat);

        // No strip. There was a panel the full width of the window behind all of this, and most of
        // its area was the gaps between controls: a broad dark band across the bottom of the map,
        // paid for by the two or three places where a pale cap actually needed something behind it.
        // Each control carries its own plate instead, so the ground shows through between them.

        // Weapons stay hard left, which is where a wheel belongs and where it already was, unless
        // the panes have taken the wheels over.
        float weapons = _sides.Length > 0 ? 0f : Weapons(margin, middle, glyph, seat) - margin;

        // Everything you press mid-turn, centred as a group. Measured before it is drawn rather
        // than laid out from the left edge and hoped for: the row has to be centred on the window,
        // so its width has to be known before the first icon lands.
        System.Action<CanvasItem, Vector2, float, float>[] icons =
        {
            (into, where, size, widest) => Glyphs.Fire(into, where, size, Palette.OnPanel),
            Ability,
            (into, where, size, widest) => Glyphs.Hop(into, where, size, Palette.OnPanel),
            (into, where, size, widest) =>
                Glyphs.Mole(into, where, size * 0.95f, Palette.OnPanel),
        };

        // Right button rather than left, which is the one binding nobody would guess: the left
        // button drags the map, because a drag on a map means that everywhere else. And C is a mole
        // rather than the aiming reticle it used to borrow, which read as a second way to aim.
        //
        // F is gone with the button it described. It planted the Boom Beets, which are now a weapon
        // on the wheel like the rest, so the one press that fires covers them too.

        // Shift is the movement ability, which the desktop had no binding for at all until now: the
        // thumb layout has carried a second fire button since the allowance was split in two, and
        // a mouse had one button for one of the two wheels.
        // Shift sits next to the right button because the two of them are the same verb aimed at
        // different halves of the turn: one fires the attack, one spends the movement allowance.
        string[] keys = { "RMB", "SHIFT", "SPACE", "C" };
        string?[] pads = Pads(Pad.Fire, Pad.Ability, Pad.Hop, Pad.Recentre);

        // Shrunk to fit rather than allowed to run over its neighbours. Two labels per control is
        // most of half as wide again, and a narrow window did not have the room for one label, so
        // the row is measured against the gap actually left between the wheel and the commit and
        // scaled to it. Overflowing here is not a cosmetic fault: the plates are what separate one
        // control from the next, and once they overlap the strip stops being a list of things.
        float commit = Reach(glyph, "ENTER", Pads(Pad.Commit)[0]);
        float room = viewport.X - (margin * 2f) - weapons - commit - (glyph * 0.4f);
        float row = Row(glyph, keys, pads);

        if (row > room && row > 0f)
        {
            glyph *= room / row;
            commit = Reach(glyph, "ENTER", Pads(Pad.Commit)[0]);
            row = Row(glyph, keys, pads);
        }

        // Laid out from the row's left edge, each control advanced by its own width.
        //
        // It used to advance by the width of the control it had just drawn and use that as the next
        // one's centre, which is only the same thing while every control is exactly as wide as its
        // neighbour. A wide control following a narrow one lands half the difference on top of it,
        // and that is most of what "the icons all overlap" was: the plates are what separate one
        // control from the next, and they were being asked to start before the last one had ended.
        float at = (viewport.X - row) / 2f;
        float steering = Steer(glyph);

        Steering(at + (steering / 2f), middle, glyph);
        at += steering;

        for (int index = 0; index < keys.Length; index++)
        {
            float width = Reach(glyph, keys[index], pads[index]);

            Action(at + (width / 2f), middle, glyph, keys[index], pads[index], icons[index]);
            at += width;
        }

        // The reset, with its tokens and its own hold ring, because the panel that used to carry
        // both of those sat at the top of the pane and is now a stamina bar and a clock.
        float reset = Reach(glyph, "R", Pads(Pad.Reset)[0]);

        Held(
            at + (reset / 2f), middle, glyph, "R", Pads(Pad.Reset)[0], Palette.Damage,
            (float)Mathf.Min(_planner!.ResetHeld, 1),
            (into, where, size, widest) => Glyphs.Reset(into, where, size, Palette.OnPanel),
            _planner.ResetsLeft);

        // Ending the turn, bottom right, in the platoon's colour: the one press that finishes the
        // round, and the only one worth putting in a corner of its own. It is a hold, so it draws a
        // filling ring, because a cap with no ring on it reads as a tap and a tap here would end
        // somebody's turn the first time a hand landed on the wrong key.
        Held(
            viewport.X - margin - (commit / 2f), middle, glyph, "ENTER", Pads(Pad.Commit)[0], seat,
            (float)Mathf.Min(_planner.CommitHeld, 1),
            (into, where, size, widest) => Glyphs.Committed(into, where, size, seat),
            0);

        // The pause, in the opposite corner from everything you press while playing, because it is
        // the one control that is not part of a turn.
        //
        // The only control on the strip with no cap under it. Every other one is a verb inside a
        // turn, and a player reads the strip to find out how to do them; the pause is the one thing
        // that needs no looking up, because Escape is where a pause has been in every game either
        // of us has played, and the cap was spending a control's width saying so. Escape still
        // opens it: what went is the label, not the key.
        Action(
            viewport.X - margin - (Reach(glyph, string.Empty, null) / 2f),
            margin + (glyph * 0.42f), glyph, null, null,
            (into, where, size, widest) =>
                Glyphs.Icon(into, "pause", where, size * 0.85f, Palette.OnPanel, widest));
    }

    /// <summary>The controller's labels, or a row of nothing when no controller is connected.</summary>
    private string?[] Pads(params string[] labels)
    {
        string?[] shown = new string?[labels.Length];

        for (int index = 0; index < labels.Length; index++)
        {
            shown[index] = _pads ? labels[index] : null;
        }

        return shown;
    }

    /// <summary>How wide the centred row of controls comes out.</summary>
    private float Row(float glyph, string[] keys, string?[] pads)
    {
        float width = Steer(glyph) + Reach(glyph, "R", Pads(Pad.Reset)[0]);

        for (int index = 0; index < keys.Length; index++)
        {
            width += Reach(glyph, keys[index], pads[index]);
        }

        return width;
    }

    /// <summary>
    /// Whichever movement ability is loaded, or a cross when that wheel is on nothing.
    /// </summary>
    /// <remarks>
    /// The weapon rather than one fixed picture, which is what the thumb layout's second button
    /// does and for the same reason: the key is always Shift and what it does is entirely whichever
    /// of the movement weapons is armed, so a fixed glyph would be a drawing of the key rather than
    /// of what pressing it would do.
    /// </remarks>
    private void Ability(CanvasItem into, Vector2 where, float size, float widest)
    {
        WeaponId weapon = _planner!.Selected(UseSlot.Movement);

        if (weapon == WeaponId.None)
        {
            Glyphs.Icon(into, "cross", where, size * 0.7f, Palette.OnPanelDim, widest);
            return;
        }

        Glyphs.Weapon(into, weapon, where, size, Palette.OnPanel, widest);
    }

    /// <summary>
    /// How wide one control is, both caps included.
    /// </summary>
    /// <remarks>
    /// Measured rather than guessed. This used to be the glyph size times one of two constants
    /// picked on whether the label was longer than two characters, which is a rule that happens to
    /// hold for the eight labels it was written against and holds for nothing else: START is five
    /// characters and got the same allowance as ESC. Since the plates are the only thing separating
    /// one control from the next, a width that is short by a few pixels is two controls touching.
    /// </remarks>
    private static float Reach(float glyph, string key, string? pad)
    {
        float caps = string.IsNullOrEmpty(key) ? 0f : CapWide(glyph, key);

        if (pad is not null)
        {
            caps += CapWide(glyph, pad) + (glyph * 0.14f);
        }

        return Mathf.Max(glyph * 1.7f, caps + (glyph * 0.3f));
    }

    /// <summary>
    /// How wide the steering cluster is.
    /// </summary>
    /// <remarks>
    /// Measured off the cross it actually draws, for the same reason <see cref="Reach"/> is. This
    /// was a constant of its own, and the constant was short: the cross is a cap either side of a
    /// gap, so it is two gaps plus a cap wide, and the number here claimed two gaps plus about a
    /// third of one. The D ran into whatever control came next, which on a keyboard-only strip was
    /// hidden by the plates being generous and showed up the moment a second label made the row
    /// tight.
    /// </remarks>
    private static float Steer(float glyph) =>
        (glyph * 0.62f * 1.12f * 2f) + CapWide(glyph, "W") + (glyph * 0.3f);

    /// <summary>How tall the steering cluster's plate is, which the pad's cap adds a row to.</summary>
    private float SteerHigh(float glyph) => glyph * (_pads ? 2.75f : 2.2f);

    /// <summary>
    /// The plate one control sits on.
    /// </summary>
    /// <remarks>
    /// Per control rather than one band across the window. The caps are pale text on a faint pale
    /// box, which needs something dark behind it and needs it only where a cap actually is.
    /// </remarks>
    private void Plate(float at, float middle, float glyph, float width, float high = 0f)
    {
        float tall = high > 0f ? high : glyph * 2.2f;

        DrawRect(
            new Rect2(at - (width / 2f), middle - (tall / 2f), width, tall),
            Palette.Panel);
    }

    /// <summary>
    /// An action that has to be held, with a ring that fills as it is.
    /// </summary>
    /// <remarks>
    /// The ring is the whole of the instruction. Nothing on this strip says the word "hold", because
    /// nothing on this strip says words, so the only way a hold can announce itself is by visibly
    /// getting somewhere while the key is down. Drawn even at zero, faintly, so the shape is there to
    /// recognise before the first press rather than appearing during it.
    /// </remarks>
    private void Held(
        float at, float middle, float glyph, string key, string? pad, Color ring, float held,
        System.Action<CanvasItem, Vector2, float, float> icon, int tokens)
    {
        float width = Reach(glyph, key, pad);
        Vector2 where = new Vector2(at, middle - (glyph * 0.42f));

        Plate(at, middle, glyph, width);
        icon(this, where, glyph, width - (glyph * 0.24f));

        DrawArc(where, glyph * 0.62f, 0f, Mathf.Tau, 28, new Color(ring, 0.22f), 2f);

        if (held > 0f)
        {
            DrawArc(
                where, glyph * 0.62f, -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * held), 28, ring, 3f);
        }

        // How many are left, as pips over the ring. The design spends its one numeral on damage, so
        // a count anywhere else is dots.
        for (int token = 0; token < Mathf.Min(tokens, 4); token++)
        {
            DrawCircle(
                where + new Vector2((token - ((Mathf.Min(tokens, 4) - 1) / 2f)) * glyph * 0.26f,
                    -glyph * 0.78f),
                glyph * 0.09f, ring);
        }

        Caps(at, middle + (glyph * 0.66f), glyph * 0.92f, key, pad);
    }

    /// <summary>The strip's own attack wheel, for the one arrangement with nowhere else to put it.</summary>
    private float Weapons(float at, float middle, float glyph, Color seat)
    {
        WeaponId[] wheel = Arsenal.Wheel;
        int loaded = System.Array.IndexOf(wheel, _planner!.Weapon);
        float step = glyph * 1.45f;

        // One plate for the whole wheel, because it is one control with three notches showing
        // rather than three controls.
        Plate(at + (step * 2.06f), middle, glyph, step * 4.4f);

        Cap(at, middle, "Q", glyph);
        at += step * 0.82f;

        for (int notch = -1; notch <= 1; notch++)
        {
            int index = (((loaded + notch) % wheel.Length) + wheel.Length) % wheel.Length;
            bool held = notch == 0;

            if (held)
            {
                DrawCircle(new Vector2(at, middle), glyph * 0.82f, new Color(seat, 0.22f));
            }

            Glyphs.Weapon(
                this,
                wheel[index],
                new Vector2(at, middle),
                glyph * (held ? 1.05f : 0.68f),
                held ? seat : new Color(Palette.OnPanel, 0.38f),
                step * 0.94f);

            at += step;
        }

        Cap(at - (step * 0.18f), middle, "E", glyph);

        return at + (step * 0.3f);
    }

    /// <summary>
    /// One platoon's own pair of wheels, in the corner of its own pane.
    /// </summary>
    /// <remarks>
    /// The reason the strip cannot do this. Four people plan at once and the strip is one row along
    /// the bottom of a shared window, so the wheel on it can only belong to one of them: whoever
    /// holds the pointer. Everybody else got a legend describing a control they are holding and a
    /// picture of somebody else's ammunition, which is worse than nothing, because it is confidently
    /// wrong rather than absent.
    ///
    /// Both wheels, because the turn has two allowances and the strip has only ever shown one of
    /// them as a wheel: the movement ability was a single icon under the Shift cap, which says what
    /// is loaded and not what else there is. Three movement weapons fit on one wheel whole, so
    /// there is nothing hidden on that side at all.
    ///
    /// One pair of cycle caps for the pair of wheels, and that is honest rather than lazy: a
    /// keyboard and a pad both have exactly one cycle binding, which walks the whole arsenal and
    /// arms whichever wheel owns what it lands on. Two pairs of caps would be drawing a control
    /// that does not exist on either device.
    /// </remarks>
    private void Wheels(in Side side)
    {
        Rect2 pane = side.Pane;

        if (pane.Size.X <= 0f || pane.Size.Y <= 0f)
        {
            return;
        }

        SeatPlanner planner = side.Planner;
        Color seat = Palette.Seat(planner.Seat);

        // Smaller than the strip's, and deliberately so. The strip has a band of its own below the
        // map and can afford to be read; this sits on the ground a mole is standing on, and the
        // rule everywhere else on this screen is that a legend over the world is worse than a
        // shorter world. At a quarter of a 720p screen the first attempt took a fifth of the pane's
        // height and most of its width, which is a control panel with a game behind it.
        float glyph = Mathf.Clamp(pane.Size.Y * 0.08f, 12f, 23f);
        float step = glyph * 1.4f;
        float mark = glyph * 1.05f;
        float cap = CapWide(glyph, side.Pad ? Pad.WheelBack : "Q");
        float gap = glyph * 0.5f;

        // Attack shows three of thirteen and movement shows all three of three, so the movement
        // group is the narrower one and sits second, nearer the middle of the screen.
        float wide = mark + cap + (step * 3f) + gap + (step * 3f) + cap + (glyph * 0.5f);
        float middle = pane.End.Y - (glyph * 1.35f);
        float at = pane.Position.X + (glyph * 0.55f);

        Plate(at + (wide / 2f), middle, glyph, wide);

        // Which device this pane answers to, in three states rather than two. With a keyboard and
        // three pads round one screen the caps below are two different alphabets and nothing else
        // on the pane says which one this player is reading; and where the seats are sharing the
        // one pointer, the difference between holding it and waiting for it is the difference
        // between a legend you can act on and a legend you are reading in advance. The mouse being
        // passed round is a glyph the game already has, for exactly this.
        Vector2 badge = new Vector2(at + (mark / 2f), middle);

        if (side.Pad)
        {
            Glyphs.Pad(this, badge, glyph * 0.8f, Palette.OnPanelDim);
        }
        else if (side.Holding)
        {
            Glyphs.Pointer(this, badge, glyph * 0.8f, Palette.OnPanelDim);
        }
        else
        {
            Glyphs.Passing(this, badge, glyph * 0.8f, Palette.OnPanelDim);
        }

        at += mark;

        Cap(at + (cap / 2f), middle, side.Pad ? Pad.WheelBack : "Q", glyph);
        at += cap;

        at = Wheel(at, middle, glyph, step, seat, Arsenal.Attacks, planner.Weapon);
        at += gap;
        at = Wheel(
            at, middle, glyph, step, seat, Arsenal.Movements, planner.Selected(UseSlot.Movement));

        Cap(at + (cap / 2f), middle, side.Pad ? Pad.WheelOn : "E", glyph);
    }

    /// <summary>Three notches of one wheel, with the armed one in the middle.</summary>
    private float Wheel(
        float at, float middle, float glyph, float step, Color seat, WeaponId[] wheel,
        WeaponId loaded)
    {
        if (wheel.Length == 0)
        {
            return at;
        }

        int armed = System.Array.IndexOf(wheel, loaded);

        for (int notch = -1; notch <= 1; notch++)
        {
            float centre = at + (step * (notch + 1)) + (step / 2f);
            bool held = notch == 0;

            // A wheel shorter than the window shows the same weapon twice rather than a blank, and
            // a repeat reads as the end of a short list, which is what it is.
            int index = armed < 0
                ? Mathf.Clamp(notch + 1, 0, wheel.Length - 1)
                : (((armed + notch) % wheel.Length) + wheel.Length) % wheel.Length;

            if (held)
            {
                DrawCircle(new Vector2(centre, middle), glyph * 0.78f, new Color(seat, 0.22f));
            }

            Glyphs.Weapon(
                this,
                wheel[index],
                new Vector2(centre, middle),
                glyph * (held ? 1f : 0.62f),
                held ? seat : new Color(Palette.OnPanel, 0.38f),
                step * 0.92f);
        }

        return at + (step * 3f);
    }

    /// <summary>
    /// The four steering keys as a cluster, which needs no glyph beside it.
    /// </summary>
    /// <remarks>
    /// A cross of W, A, S and D in the shape they sit in on the keyboard is the one thing in this
    /// strip that explains itself: the arrangement is the explanation. The arrow keys do the same
    /// job and are not drawn, because showing both would double the width of the most obvious part
    /// of the row.
    ///
    /// A pad's answer is the left stick, and it gets a cap of its own beneath the cross rather than
    /// beside it, because a cross with a fifth label hanging off one side stops reading as the shape
    /// of a keyboard.
    /// </remarks>
    private void Steering(float at, float middle, float glyph)
    {
        float cap = glyph * 0.62f;
        float gap = cap * 1.12f;
        float width = Steer(glyph);

        // One plate under the whole cross. It is four caps and one control. Taller when the pad's
        // own cap is under it, so the stick's label sits on the same dark ground every other label
        // does rather than on whatever the map happens to have below the strip.
        float high = SteerHigh(glyph);
        float centre = _pads ? middle - (glyph * 0.24f) : middle;

        Plate(at, centre, glyph, width, high);

        Cap(at, centre - gap, "W", glyph);
        Cap(at - gap, centre + (gap * 0.15f), "A", glyph);
        Cap(at, centre + (gap * 0.15f), "S", glyph);
        Cap(at + gap, centre + (gap * 0.15f), "D", glyph);

        if (_pads)
        {
            Cap(at, centre + (glyph * 0.94f), Pad.Steer, glyph * 0.82f, round: true);
        }
    }

    private void Action(
        float at, float middle, float glyph, string? key, string? pad,
        System.Action<CanvasItem, Vector2, float, float> icon)
    {
        float width = Reach(glyph, key ?? string.Empty, pad);

        Plate(at, middle, glyph, width);

        // The icon is given the plate's width to stay inside. Wide art used to be drawn at whatever
        // its proportions asked for and a girder is nearly five times as wide as it is tall, so the
        // movement ability drew a beam straight across the two controls either side of it.
        icon(this, new Vector2(at, middle - (glyph * 0.42f)), glyph, width - (glyph * 0.24f));

        if (key is not null || pad is not null)
        {
            Caps(at, middle + (glyph * 0.66f), glyph * 0.92f, key ?? string.Empty, pad);
        }
    }

    /// <summary>A control's addresses: the key, and the controller button beside it.</summary>
    private void Caps(float at, float y, float glyph, string key, string? pad)
    {
        if (pad is null)
        {
            Cap(at, y, key, glyph);
            return;
        }

        float keyWide = CapWide(glyph, key);
        float padWide = CapWide(glyph, pad);
        float gap = glyph * 0.14f;
        float left = at - ((keyWide + gap + padWide) / 2f);

        Cap(left + (keyWide / 2f), y, key, glyph);
        Cap(left + keyWide + gap + (padWide / 2f), y, pad, glyph, round: true);
    }

    /// <summary>How wide one cap comes out, which is what the layout is measured from.</summary>
    private static float CapWide(float glyph, string key) =>
        ThemeDB.FallbackFont.GetStringSize(key, fontSize: CapSize(glyph)).X + (glyph * 0.34f);

    private static int CapSize(float glyph) => Mathf.Max((int)(glyph * 0.46f), 9);

    /// <summary>
    /// One cap, drawn as a key rather than as a word.
    /// </summary>
    /// <remarks>
    /// Round for a controller and square for a keyboard, which is the difference the hardware
    /// already has: face buttons are circles and keys are not. It is doing real work in a hotseat
    /// match, where B is a key on the keyboard and a button on a pad and the two would otherwise be
    /// the same picture with a different letter in it.
    /// </remarks>
    private void Cap(float x, float y, string key, float glyph, bool round = false)
    {
        Font font = ThemeDB.FallbackFont;
        int size = CapSize(glyph);
        Vector2 measured = font.GetStringSize(key, fontSize: size);
        float padding = glyph * 0.17f;
        Vector2 box = new Vector2(measured.X + (padding * 2f), (glyph * 0.46f) + padding);
        Color plate = new Color(Palette.OnPanel, 0.13f);

        if (round)
        {
            // A capsule, so a one-letter button is a circle and LB or START is a lozenge. Drawn as
            // a rectangle between two end caps rather than with a corner radius, which Godot's
            // immediate mode has no primitive for.
            float radius = box.Y / 2f;

            DrawCircle(new Vector2(x - (box.X / 2f) + radius, y), radius, plate);
            DrawCircle(new Vector2(x + (box.X / 2f) - radius, y), radius, plate);
            DrawRect(
                new Rect2(x - (box.X / 2f) + radius, y - radius, box.X - (radius * 2f), box.Y),
                plate);
        }
        else
        {
            DrawRect(new Rect2(x - (box.X / 2f), y - (box.Y / 2f), box), plate);
        }

        DrawString(
            font,
            new Vector2(x - (measured.X / 2f), y + (measured.Y * 0.32f)),
            key,
            HorizontalAlignment.Left,
            -1,
            size,
            new Color(Palette.OnPanel, 0.85f));
    }
}
