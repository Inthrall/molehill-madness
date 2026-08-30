using Godot;
using MoleSim.Match;

/// <summary>
/// What the keys do, and which weapon is loaded, for somebody at a keyboard.
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
/// </remarks>
public partial class KeyGuide : Control
{
    private SeatPlanner? _planner;
    private int _seat;

    public KeyGuide()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Fills the layer, so Size is the rectangle it is actually drawn into. Laid out against
        // the viewport instead, an expanding stretch gives a canvas a different shape from the
        // window and everything anchored near an edge ends up off it.
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    /// <summary>
    /// Whose turn it is, or null when nobody is planning.
    /// </summary>
    public void Watch(SeatPlanner? planner, int seat)
    {
        _planner = planner;
        _seat = seat;
        Visible = planner is not null;
        QueueRedraw();
    }

    /// <summary>How tall the strip is, so the layout above it can keep clear.</summary>
    public static float Height(Vector2 viewport) =>
        Mathf.Clamp(viewport.Y * 0.125f, 68f, 104f);

    public override void _Draw()
    {
        Vector2 viewport = Size;

        if (viewport.X <= 0f || viewport.Y <= 0f || _planner is null)
        {
            return;
        }

        float tall = Height(viewport);
        float top = viewport.Y - tall;
        float glyph = tall * 0.42f;
        float middle = top + (tall / 2f);
        Color seat = Palette.Seat(_seat);

        // No strip. There was a panel the full width of the window behind all of this, and most of
        // its area was the gaps between controls: a broad dark band across the bottom of the map,
        // paid for by the two or three places where a pale cap actually needed something behind it.
        // Each control carries its own plate instead, so the ground shows through between them.

        // Weapons stay hard left, which is where a wheel belongs and where it already was.
        Weapons(tall * 0.42f, middle, glyph, seat);

        // Everything you press mid-turn, centred as a group. Measured before it is drawn rather
        // than laid out from the left edge and hoped for: the row has to be centred on the window,
        // so its width has to be known before the first icon lands.
        System.Action<CanvasItem, Vector2, float>[] icons =
        {
            (into, where, size) => Glyphs.Fire(into, where, size, Palette.OnPanel),
            Ability,
            (into, where, size) => Glyphs.Hop(into, where, size, Palette.OnPanel),
            (into, where, size) => Glyphs.Mole(into, where, size * 0.95f, Palette.OnPanel),
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

        float width = Steer(glyph) + Reach(glyph, "R");

        for (int index = 0; index < keys.Length; index++)
        {
            width += Reach(glyph, keys[index]);
        }

        float at = ((viewport.X - width) / 2f) + (Steer(glyph) / 2f);

        at = Steering(at, middle, glyph);

        for (int index = 0; index < keys.Length; index++)
        {
            at = Action(at, middle, glyph, keys[index], icons[index]);
        }

        // The reset, with its tokens and its own hold ring, because the panel that used to carry
        // both of those sat at the top of the pane and is now a stamina bar and a clock.
        Held(
            at, middle, glyph, "R", Palette.Damage,
            (float)Mathf.Min(_planner.ResetHeld, 1),
            (into, where, size) => Glyphs.Reset(into, where, size, Palette.OnPanel),
            _planner.ResetsLeft);

        // Ending the turn, bottom right, in the platoon's colour: the one press that finishes the
        // round, and the only one worth putting in a corner of its own. It is a hold, so it draws a
        // filling ring, because a cap with no ring on it reads as a tap and a tap here would end
        // somebody's turn the first time a hand landed on the wrong key.
        Held(
            viewport.X - (tall * 0.42f) - (Reach(glyph, "ENTER") / 2f), middle, glyph, "ENTER", seat,
            (float)Mathf.Min(_planner.CommitHeld, 1),
            (into, where, size) => Glyphs.Committed(into, where, size, seat),
            0);

        // The pause, in the opposite corner from everything you press while playing, because it is
        // the one control that is not part of a turn.
        float escape = Reach(glyph, "ESC");

        Action(
            viewport.X - (tall * 0.42f) - (escape / 2f), (tall * 0.42f) + (glyph * 0.42f), glyph,
            "ESC", (into, where, size) =>
                Glyphs.Icon(into, "pause", where, size * 0.85f, Palette.OnPanel));
    }

    /// <summary>How wide one control is, cap included.</summary>
    /// <summary>
    /// Whichever movement ability is loaded, or a cross when that wheel is on nothing.
    /// </summary>
    /// <remarks>
    /// The weapon rather than one fixed picture, which is what the thumb layout's second button
    /// does and for the same reason: the key is always Shift and what it does is entirely whichever
    /// of the movement weapons is armed, so a fixed glyph would be a drawing of the key rather than
    /// of what pressing it would do.
    /// </remarks>
    private void Ability(CanvasItem into, Vector2 where, float size)
    {
        WeaponId weapon = _planner!.Selected(UseSlot.Movement);

        if (weapon == WeaponId.None)
        {
            Glyphs.Icon(into, "cross", where, size * 0.7f, Palette.OnPanelDim);
            return;
        }

        Glyphs.Weapon(into, weapon, where, size, Palette.OnPanel);
    }

    private static float Reach(float glyph, string key) =>
        glyph * (key.Length > 2 ? 2.15f : 1.7f);

    /// <summary>How wide the steering cluster is.</summary>
    private static float Steer(float glyph) => glyph * 0.62f * 1.12f * 2.4f;

    /// <summary>
    /// The plate one control sits on.
    /// </summary>
    /// <remarks>
    /// Per control rather than one band across the window. The caps are pale text on a faint pale
    /// box, which needs something dark behind it and needs it only where a cap actually is.
    /// </remarks>
    private void Plate(float at, float middle, float glyph, float width)
    {
        float high = glyph * 2.2f;

        DrawRect(
            new Rect2(at - (width / 2f), middle - (high / 2f), width, high),
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
    private float Held(
        float at, float middle, float glyph, string key, Color ring, float held,
        System.Action<CanvasItem, Vector2, float> icon, int tokens)
    {
        float width = Reach(glyph, key);
        Vector2 where = new Vector2(at, middle - (glyph * 0.42f));

        Plate(at, middle, glyph, width);
        icon(this, where, glyph);

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

        Cap(at, middle + (glyph * 0.66f), key, glyph * 0.92f);

        return at + width;
    }

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
                held ? seat : new Color(Palette.OnPanel, 0.38f));

            at += step;
        }

        Cap(at - (step * 0.18f), middle, "E", glyph);

        return at + (step * 0.3f);
    }

    /// <summary>
    /// The four steering keys as a cluster, which needs no glyph beside it.
    /// </summary>
    /// <remarks>
    /// A cross of W, A, S and D in the shape they sit in on the keyboard is the one thing in this
    /// strip that explains itself: the arrangement is the explanation. The arrow keys do the same
    /// job and are not drawn, because showing both would double the width of the most obvious part
    /// of the row.
    /// </remarks>
    private float Steering(float at, float middle, float glyph)
    {
        float cap = glyph * 0.62f;
        float gap = cap * 1.12f;

        // One plate under the whole cross. It is four caps and one control.
        Plate(at, middle, glyph, (gap * 2f) + (cap * 1.4f));

        Cap(at, middle - gap, "W", glyph);
        Cap(at - gap, middle + (gap * 0.15f), "A", glyph);
        Cap(at, middle + (gap * 0.15f), "S", glyph);
        Cap(at + gap, middle + (gap * 0.15f), "D", glyph);

        return at + (gap * 2.4f);
    }

    private float Action(
        float at, float middle, float glyph, string key,
        System.Action<CanvasItem, Vector2, float> icon)
    {
        Plate(at, middle, glyph, Reach(glyph, key));
        icon(this, new Vector2(at, middle - (glyph * 0.42f)), glyph);
        Cap(at, middle + (glyph * 0.66f), key, glyph * 0.92f);

        return at + (glyph * (key.Length > 2 ? 2.15f : 1.7f));
    }

    /// <summary>One key cap, drawn as a key rather than as a word.</summary>
    private void Cap(float x, float y, string key, float glyph)
    {
        Font font = ThemeDB.FallbackFont;
        int size = Mathf.Max((int)(glyph * 0.46f), 9);
        Vector2 measured = font.GetStringSize(key, fontSize: size);
        float padding = glyph * 0.17f;
        Vector2 box = new Vector2(measured.X + (padding * 2f), (glyph * 0.46f) + padding);

        DrawRect(
            new Rect2(x - (box.X / 2f), y - (box.Y / 2f), box), new Color(Palette.OnPanel, 0.13f));

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
