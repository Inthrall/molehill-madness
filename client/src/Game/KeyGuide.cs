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
        Color seat = Palette.Seat(_seat);

        DrawRect(new Rect2(0f, top, viewport.X, tall), Palette.Panel);

        float middle = top + (tall / 2f);
        float at = tall * 0.42f;

        // The loaded weapon, and the two either side of it, because a wheel you cannot see the next
        // notch of is a button that changes to something unpredictable. Same argument as the thumb
        // layout's, and the same three-wide answer, turned on its side because a strip is wide.
        at = Weapons(at, middle, glyph, seat);

        DrawLine(
            new Vector2(at, top + (tall * 0.22f)),
            new Vector2(at, top + (tall * 0.78f)),
            new Color(Palette.OnPanel, 0.2f), 2f);

        at += tall * 0.34f;
        at = Steering(at, middle, glyph);

        // Right button rather than left, which is the one binding nobody would guess: the left
        // button drags the map, because a drag on a map means that everywhere else.
        at = Action(at, middle, glyph, "RMB", (into, where, size) =>
            Glyphs.Fire(into, where, size, Palette.OnPanel));

        at = Action(at, middle, glyph, "F", (into, where, size) =>
            Glyphs.Dynamite(into, where, size, Palette.OnPanel));

        at = Action(at, middle, glyph, "H", (into, where, size) =>
            Glyphs.Hop(into, where, size, Palette.OnPanel));

        at = Action(at, middle, glyph, "R", (into, where, size) =>
            Glyphs.Reset(into, where, size, Palette.OnPanel));

        at = Action(at, middle, glyph, "C", (into, where, size) =>
            Glyphs.Icon(into, "aim", where, size * 0.9f, Palette.OnPanel));

        // Commit, in the platoon's colour, because it is the one press that ends the turn and the
        // only one worth picking out of the row.
        at = Action(at, middle, glyph, "SPACE", (into, where, size) =>
            Glyphs.Committed(into, where, size, seat));

        Action(at, middle, glyph, "ESC", (into, where, size) =>
            Glyphs.Icon(into, "pause", where, size * 0.85f, Palette.OnPanel));
    }

    private float Weapons(float at, float middle, float glyph, Color seat)
    {
        WeaponId[] wheel = Arsenal.Wheel;
        int loaded = System.Array.IndexOf(wheel, _planner!.Weapon);
        float step = glyph * 1.45f;

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
