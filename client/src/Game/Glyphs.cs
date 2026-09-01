using Godot;
using MoleSim.Match;

/// <summary>
/// The wordless icon set: every weapon and every gauge, drawn rather than written.
/// </summary>
/// <remarks>
/// The design wants minimal text and images for everything, which is not decoration. The game
/// is all-ages, ships without text chat, and generates its own names precisely so that nothing
/// on screen has to be read. A localised string is a thing a seven-year-old can be locked out
/// of; a picture of a mallet is not.
///
/// Drawn from primitives rather than imported as art, for three reasons. It scales to any
/// screen without a sprite sheet per density, which matters when the same build runs on a
/// phone and a television. It recolours per platoon for free. And it means the prototype has
/// no art dependency at all, so the icon set can be judged for legibility now and handed to an
/// illustrator later without anything else changing.
///
/// Every glyph draws inside a square of the given size, centred on the given point, so callers
/// can lay them out on a grid without knowing what any of them are.
/// </remarks>
public static class Glyphs
{
    /// <summary>Stroke width as a share of the glyph box, so weight scales with size.</summary>
    private const float Stroke = 0.09f;

    // ---- Weapons --------------------------------------------------------------------

    /// <summary>
    /// A weapon, as the object it is on the map.
    /// </summary>
    /// <remarks>
    /// The wheel used to draw from a glyph sheet made for the purpose, so choosing a weapon meant
    /// picking a small icon and then watching a different-looking thing come out of the mole. The
    /// four weapons that are events rather than objects still use the sheet, since there is nothing
    /// in the world to show instead: see <see cref="Art.ObjectFor"/>.
    ///
    /// This also retired a line drawing of a girder. The girder had no glyph, so it had one drawn
    /// from primitives here among fourteen painted ones, which looked exactly like what it was; the
    /// beam the game lays is a painted object and always was.
    ///
    /// Only the ink's alpha is used, and that is a real loss worth naming. The glyph sheet is white
    /// so that it can be tinted, and callers did tint it: a platoon's armed weapon was drawn in the
    /// platoon's colour. An object sprite cannot be, because it already has colours of its own, and
    /// multiplying one by a seat colour makes a green beetle or a red sandbag rather than a tinted
    /// icon. Since the two sets have to sit on the same wheel, neither is tinted now. Whose weapon
    /// it is was never being carried by the hue anyway: both places that used to tint also draw a
    /// disc in the platoon's colour behind it, and that is what says it.
    ///
    /// The alpha is kept because that is the other half of what callers ask for, and it means
    /// something quite different: it is how the wheel fades its neighbours out and how a spent
    /// allowance greys its button.
    /// </remarks>
    public static void Weapon(
        CanvasItem into, WeaponId weapon, Vector2 at, float size, Color ink, float widest = 0f)
    {
        Color showing = new Color(1f, 1f, 1f, ink.A);

        if (Art.ObjectFor(weapon) is string named)
        {
            Fit(into, Art.Object(named), at, size, showing, widest);
            return;
        }

        Fit(into, Art.Weapon(weapon), at, size, showing, widest);
    }

    /// <summary>An interface glyph, by the name the importer gave it.</summary>
    public static void Icon(
        CanvasItem into, string name, Vector2 at, float size, Color ink, float widest = 0f)
    {
        Fit(into, Art.Glyph(name), at, size, ink, widest);
    }

    /// <summary>
    /// A number, in the game's own digits.
    /// </summary>
    /// <remarks>
    /// Digits are the one numeral the design keeps, on the grounds that a numeral reads the same in
    /// every language a seven-year-old might have, and until now they came out of the engine's
    /// fallback font: a system typeface in a game that has no other type in it anywhere. These are
    /// drawn, in the same hand as everything else.
    ///
    /// Laid out on each digit's own width rather than on a fixed advance, because the artist drew a
    /// one narrower than a zero and monospacing them would leave it swimming.
    /// </remarks>
    public static void Number(CanvasItem into, int value, Vector2 middle, float size, Color ink)
    {
        string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float gap = size * DigitGap;
        float across = 0f;

        foreach (char digit in digits)
        {
            across += Width(digit, size) + gap;
        }

        float left = middle.X - ((across - gap) / 2f);

        foreach (char digit in digits)
        {
            float wide = Width(digit, size);

            Fit(
                into,
                Art.Glyph(Named(digit)),
                new Vector2(left + (wide / 2f), middle.Y),
                size,
                ink);

            left += wide + gap;
        }
    }

    /// <summary>How much of a digit's height goes between it and the next one.</summary>
    private const float DigitGap = 0.12f;

    private static float Width(char digit, float size)
    {
        Texture2D art = Art.Glyph(Named(digit));

        return size * art.GetWidth() / art.GetHeight();
    }

    private static string Named(char digit) => digit switch
    {
        '-' => "minus",
        '+' => "plus",
        '%' => "percent",
        ':' => "colon",
        '.' => "stop",
        _ => "digit-" + digit,
    };

    /// <summary>
    /// Draws a glyph inside a box of the given height, keeping its shape, tinted.
    /// </summary>
    /// <remarks>
    /// The glyphs are white on the way in precisely so they can be tinted on the way out, which is
    /// the property the versions drawn from primitives had and the reason it was safe to replace
    /// them. A platoon's weapon wheel is still in the platoon's colour, and it costs a modulate
    /// rather than a second set of files.
    ///
    /// Fitted to the height rather than to the box, because the sheets were trimmed to their
    /// content and a mallet is wider than an acorn; squeezed into a square, every glyph would be a
    /// different weight from the one next to it.
    /// </remarks>
    private static void Fit(CanvasItem into, Texture2D art, Vector2 middle, float size, Color ink)
    {
        Fit(into, art, middle, size, ink, 0f);
    }

    /// <summary>
    /// The same, but never wider than the room it was given.
    /// </summary>
    /// <remarks>
    /// <paramref name="size"/> is the height, and the width is whatever the art's proportions ask
    /// for. That is right for the fourteen roughly square weapons and wrong for the long ones: the
    /// girder is nearly five times as wide as it is tall, so a row of controls laid out on the
    /// height drew a beam straight across its neighbours and the strip read as one smeared picture
    /// rather than as five things you can press.
    ///
    /// Cropped rather than shrunk, and that is the deliberate half. Scaling a girder down until it
    /// fits leaves a correct picture of a beam about a fifth the size of every other icon, which
    /// reads as a small weapon rather than as a long one. A crop keeps every icon at one size and
    /// spends the shortfall on showing less of the widest few, which is the trade a row of tiles
    /// makes everywhere else.
    ///
    /// The crop is centred, because the middle of an object is the part that identifies it: a
    /// girder cropped to its left end is a grey square, and so is a sandbag.
    /// </remarks>
    private static void Fit(
        CanvasItem into, Texture2D art, Vector2 middle, float size, Color ink, float widest)
    {
        float wide = size * art.GetWidth() / art.GetHeight();

        if (widest <= 0f || wide <= widest)
        {
            into.DrawTextureRect(
                art,
                new Rect2(middle.X - (wide / 2f), middle.Y - (size / 2f), wide, size),
                false,
                ink);

            return;
        }

        float shown = art.GetWidth() * (widest / wide);

        into.DrawTextureRectRegion(
            art,
            new Rect2(middle.X - (widest / 2f), middle.Y - (size / 2f), widest, size),
            new Rect2((art.GetWidth() - shown) / 2f, 0f, shown, art.GetHeight()),
            ink);
    }

    private static void Acorn(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        into.DrawRect(
            new Rect2(at.X - (unit * 0.8f), at.Y - unit, unit * 1.6f, unit * 0.6f), ink);
        Polygon(into, ink,
            at + new Vector2(-unit * 0.7f, -unit * 0.4f),
            at + new Vector2(unit * 0.7f, -unit * 0.4f),
            at + new Vector2(0, unit));
    }

    /// <summary>A sack of loose soil, cheap for anybody to dig back out.</summary>
    private static void Sack(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Polygon(into, ink,
            at + new Vector2(-unit * 0.4f, -unit * 0.5f),
            at + new Vector2(unit * 0.4f, -unit * 0.5f),
            at + new Vector2(unit * 0.85f, unit * 0.9f),
            at + new Vector2(-unit * 0.85f, unit * 0.9f));

        into.DrawLine(
            at + new Vector2(-unit * 0.5f, -unit * 0.55f),
            at + new Vector2(unit * 0.5f, -unit * 0.55f), ink, line * 1.4f);
    }

    /// <summary>Boom Beets: a beetroot with a fuse. Plant, run, regret.</summary>
    private static void Beet(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Polygon(into, ink,
            at + new Vector2(-unit * 0.62f, -unit * 0.2f),
            at + new Vector2(unit * 0.62f, -unit * 0.2f),
            at + new Vector2(0, unit * 0.95f));
        into.DrawCircle(at + new Vector2(0, -unit * 0.2f), unit * 0.62f, ink);

        into.DrawPolyline(
            new[]
            {
                at + new Vector2(0, -unit * 0.78f),
                at + new Vector2(unit * 0.35f, -unit * 0.95f),
                at + new Vector2(unit * 0.2f, -unit * 1.25f),
            },
            ink, line * 0.8f);
    }

    // ---- Gauges and controls --------------------------------------------------------

    /// <summary>An hourglass. How much of the round's eight seconds a route eats.</summary>
    public static void Time(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Polygon(into, ink,
            at + new Vector2(-unit * 0.6f, -unit * 0.85f),
            at + new Vector2(unit * 0.6f, -unit * 0.85f),
            at + new Vector2(0, 0));
        Polygon(into, ink,
            at + new Vector2(-unit * 0.6f, unit * 0.85f),
            at + new Vector2(unit * 0.6f, unit * 0.85f),
            at + new Vector2(0, 0));

        into.DrawLine(
            at + new Vector2(-unit * 0.75f, -unit * 0.85f),
            at + new Vector2(unit * 0.75f, -unit * 0.85f), ink, line);
        into.DrawLine(
            at + new Vector2(-unit * 0.75f, unit * 0.85f),
            at + new Vector2(unit * 0.75f, unit * 0.85f), ink, line);
    }

    /// <summary>
    /// A clock face. How much of the turn's eight seconds of walking is left.
    /// </summary>
    /// <remarks>
    /// A second time glyph, because there are two clocks on screen and they count different things.
    /// The hourglass belongs to the table: it is the planning clock, shared, and it runs down while
    /// everybody thinks. This one belongs to one mole and one turn, and it is spent by walking
    /// rather than by the wall. Reported from play as two identical instruments disagreeing with
    /// each other, which is exactly what they looked like.
    ///
    /// Hands rather than a dial, because the ring around it is already the dial and a second one
    /// inside it would read as a fault. Set at ten past two, which is where every drawn clock in
    /// the world is set, so that it reads as a clock at any size.
    /// </remarks>
    public static void Clock(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawArc(at, unit * 0.82f, 0f, Mathf.Tau, 24, ink, line);

        // The long hand to ten past, the short hand to two, both from the middle.
        into.DrawLine(at, at + new Vector2(unit * 0.52f, -unit * 0.3f), ink, line);
        into.DrawLine(at, at + new Vector2(unit * 0.2f, -unit * 0.55f), ink, line);
        into.DrawCircle(at, line * 0.9f, ink);
    }

    /// <summary>A puff of breath. Stamina, which is the same thing as digging money.</summary>
    public static void Puff(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawCircle(at + new Vector2(-unit * 0.3f, unit * 0.15f), unit * 0.42f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.25f, unit * 0.05f), unit * 0.32f, ink);
        into.DrawCircle(at + new Vector2(0, -unit * 0.3f), unit * 0.34f, ink);

        Arc(into, at + new Vector2(unit * 0.55f, -unit * 0.55f), unit * 0.4f, 250, 20, ink, line * 0.7f);
    }

    /// <summary>
    /// Wind: an arrow for which way, and streaks behind it for how hard.
    /// </summary>
    /// <remarks>
    /// The first version scaled the arrow's length by the strength, which meant a light breeze
    /// drew three two-pixel dashes and read as a rendering fault. Direction is the thing a
    /// player has to see at a glance and it must not shrink, so the arrow is always full
    /// length and the strength is in how many streaks trail it.
    /// </remarks>
    public static void Wind(CanvasItem into, Vector2 at, float size, float strength, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;
        float reach = Mathf.Clamp(strength, -1f, 1f);

        if (Mathf.Abs(reach) < 0.05f)
        {
            // Dead calm, and worth saying so rather than drawing a stub of an arrow.
            Arc(into, at, unit * 0.3f, 0, 360, ink, line * 0.8f);
            return;
        }

        float direction = Mathf.Sign(reach);
        float tip = unit * 0.95f * direction;
        float tail = -unit * 0.9f * direction;

        into.DrawLine(at + new Vector2(tail, 0), at + new Vector2(tip, 0), ink, line);

        float back = tip - (unit * 0.45f * direction);
        Polygon(into, ink,
            at + new Vector2(tip, 0),
            at + new Vector2(back, -unit * 0.34f),
            at + new Vector2(back, unit * 0.34f));

        // One streak for a breeze, three for a gale.
        int streaks = 1 + Mathf.FloorToInt(Mathf.Abs(reach) * 2.99f);

        for (int streak = 0; streak < streaks; streak++)
        {
            float y = unit * (0.42f + (streak * 0.3f)) * (streak % 2 == 0 ? -1f : 1f);
            float length = unit * (0.8f - (streak * 0.16f)) * direction;

            into.DrawLine(
                at + new Vector2(tail, y), at + new Vector2(tail + length, y),
                new Color(ink, 0.7f), line * 0.75f);
        }
    }

    /// <summary>A circular arrow. The reset token, and the most watched glyph on the screen.</summary>
    public static void Reset(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Arc(into, at, unit * 0.7f, 40, 320, ink, line * 1.2f);
        Polygon(into, ink,
            at + new Vector2(unit * 0.72f, -unit * 0.36f),
            at + new Vector2(unit * 0.28f, -unit * 0.5f),
            at + new Vector2(unit * 0.78f, -unit * 0.86f));
    }

    /// <summary>
    /// A mole's head. One per mole still standing, in the tally, and the same shape the moles
    /// themselves are drawn as so the two read as the same animal.
    /// </summary>
    /// <summary>
    /// A mouse, meaning this platoon plans with the pointer and the keys.
    /// </summary>
    /// <remarks>
    /// Drawn rather than imported, like the rest of the interface's own furniture: the icon sheet
    /// that arrived has a speaker and a gear and a heart on it, and nothing that means an input
    /// device. Three shapes, told apart at a glance by silhouette rather than by detail, because
    /// these are read at about twenty pixels on a menu.
    /// </remarks>
    public static void Pointer(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float wide = size * 0.27f;
        float shoulder = at.Y - (size * 0.12f);

        // A rounded shoulder over straight sides, which is the whole of a mouse at this size. The
        // first attempt was a plain rectangle with a nick in it and read as a plug.
        into.DrawCircle(new Vector2(at.X, shoulder), wide, ink);
        into.DrawRect(
            new Rect2(at.X - wide, shoulder, wide * 2f, size * 0.5f), ink);
        into.DrawCircle(new Vector2(at.X, shoulder + (size * 0.5f)), wide * 0.62f, ink);

        // The seam, and the wheel in it. Two marks, and they are what say mouse rather than pebble.
        into.DrawLine(
            new Vector2(at.X, at.Y - (size * 0.38f)),
            new Vector2(at.X, at.Y - (size * 0.08f)),
            Palette.Panel,
            Mathf.Max(size * 0.08f, 1.5f));

        into.DrawCircle(
            new Vector2(at.X, at.Y - (size * 0.19f)), Mathf.Max(size * 0.07f, 1.2f), ink);
    }

    /// <summary>
    /// A fat arrow pointing down at something, for saying "this one" across a whole screen.
    /// </summary>
    /// <remarks>
    /// Drawn rather than imported, and drawn as one polygon rather than a stem and a head, because
    /// two shapes at this size leave a seam wherever they meet and the seam is the first thing the
    /// eye finds. Outlined in the ink the rest of the interface uses, since the platoon colours are
    /// mid-tones and half of them vanish against the sky.
    /// </remarks>
    public static void Attention(CanvasItem into, Vector2 at, float size, Color ink, float shown)
    {
        float half = size * 0.5f;
        float stem = size * 0.19f;
        float head = size * 0.42f;
        float top = at.Y - half;
        float shoulder = at.Y + half - head;

        Vector2[] arrow =
        {
            new Vector2(at.X - stem, top),
            new Vector2(at.X + stem, top),
            new Vector2(at.X + stem, shoulder),
            new Vector2(at.X + head, shoulder),
            new Vector2(at.X, at.Y + half),
            new Vector2(at.X - head, shoulder),
            new Vector2(at.X - stem, shoulder),
        };

        into.DrawColoredPolygon(arrow, new Color(ink, shown));

        // Closed by hand: DrawPolyline leaves the last edge open, and the gap lands on the point of
        // the arrow, which is the one corner that has to look sharp.
        Vector2[] outline = new Vector2[arrow.Length + 1];
        arrow.CopyTo(outline, 0);
        outline[arrow.Length] = arrow[0];

        into.DrawPolyline(
            outline,
            new Color(Palette.Ink, shown * 0.85f),
            Mathf.Max(size * 0.055f, 2f));
    }

    /// <summary>A gamepad, meaning this platoon has a controller of its own.</summary>
    public static void Pad(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float wide = size * 0.46f;

        into.DrawRect(
            new Rect2(at.X - wide, at.Y - (size * 0.18f), wide * 2f, size * 0.34f), ink);

        // Grips at the corners, which is the silhouette that says controller rather than brick.
        into.DrawCircle(new Vector2(at.X - (wide * 0.86f), at.Y + (size * 0.14f)), size * 0.19f, ink);
        into.DrawCircle(new Vector2(at.X + (wide * 0.86f), at.Y + (size * 0.14f)), size * 0.19f, ink);
        into.DrawCircle(new Vector2(at.X - (wide * 0.86f), at.Y - (size * 0.1f)), size * 0.15f, ink);
        into.DrawCircle(new Vector2(at.X + (wide * 0.86f), at.Y - (size * 0.1f)), size * 0.15f, ink);

        // A stick and a pair of buttons, punched out rather than drawn on, so they read on any ink.
        into.DrawCircle(new Vector2(at.X - (wide * 0.4f), at.Y), size * 0.09f, Palette.Panel);
        into.DrawCircle(new Vector2(at.X + (wide * 0.46f), at.Y - (size * 0.06f)), size * 0.06f, Palette.Panel);
        into.DrawCircle(new Vector2(at.X + (wide * 0.22f), at.Y + (size * 0.06f)), size * 0.06f, Palette.Panel);
    }

    /// <summary>
    /// A mouse being passed round, meaning this platoon waits its turn with it.
    /// </summary>
    /// <remarks>
    /// The one that had to be said and could not be before. Hotseat on one pointer means a platoon
    /// plans when the mouse reaches it, and with nothing on screen saying so, four platoons all
    /// looked equally ready and only one of them could act.
    /// </remarks>
    public static void Passing(CanvasItem into, Vector2 at, float size, Color ink)
    {
        // The mouse, lower and smaller, with the loop over the top of it rather than beside it.
        // Side by side the two shapes were the same weight and the pair read as a mug.
        Pointer(into, at + new Vector2(0f, size * 0.14f), size * 0.72f, ink);

        float line = Mathf.Max(size * 0.1f, 1.5f);
        Vector2 middle = at + new Vector2(0f, size * 0.06f);
        float reach = size * 0.4f;

        into.DrawArc(middle, reach, Mathf.Pi * 1.15f, Mathf.Pi * 1.85f, 20, ink, line);

        // An arrowhead on the leading end, because an arc on its own is a bracket.
        Vector2 head = middle + (Vector2.Right.Rotated(Mathf.Pi * 1.85f) * reach);
        Vector2 along = Vector2.Right.Rotated(Mathf.Pi * 1.85f + (Mathf.Pi / 2f));

        into.DrawColoredPolygon(
            new[]
            {
                head + (along * size * 0.17f),
                head + (along.Rotated(Mathf.Pi * 0.62f) * size * 0.17f),
                head + (along.Rotated(-Mathf.Pi * 0.62f) * size * 0.17f),
            },
            ink);
    }

    public static void Mole(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        into.DrawCircle(at, unit * 0.7f, ink);
        into.DrawCircle(at + new Vector2(-unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);

        // The snout, which is what stops it reading as a berry at tally size.
        into.DrawCircle(at + new Vector2(0, unit * 0.24f), unit * 0.26f, Palette.Snout);
    }

    /// <summary>A burst. Fire the thing on the wheel.</summary>
    public static void Fire(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Vector2[] star = new Vector2[12];

        for (int point = 0; point < 12; point++)
        {
            float angle = point * Mathf.Tau / 12f;
            float radius = unit * ((point % 2 == 0) ? 0.95f : 0.42f);
            star[point] = at + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }

        into.DrawColoredPolygon(star, ink);
    }

    /// <summary>A stick of dynamite, for the second button the design asks for.</summary>
    public static void Dynamite(CanvasItem into, Vector2 at, float size, Color ink)
    {
        Beet(into, at, size, ink);
    }

    /// <summary>
    /// Leaving the ground: a body going up, off a line that stays put.
    /// </summary>
    /// <remarks>
    /// The arc-over-a-gap glyph is still below and still used for the plan markers, where an arc is
    /// exactly right because it describes a path already chosen. As a button it was wrong: at button
    /// size an arc collapses into a chevron, and a chevron on its own is the most overloaded mark in
    /// interface design, meaning back, up, expand, scroll and sort depending on where it is.
    ///
    /// A ground line fixes it. An arrow leaving a surface can only mean one thing, and the surface is
    /// what a chevron was missing.
    /// </remarks>
    public static void Jump(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        // The ground it leaves, held still and full width.
        into.DrawLine(
            at + new Vector2(-unit * 0.72f, unit * 0.7f),
            at + new Vector2(unit * 0.72f, unit * 0.7f),
            ink, line * 1.2f);

        // The shaft, stopping short of the head so the join is not a lump.
        into.DrawLine(
            at + new Vector2(0, unit * 0.42f),
            at + new Vector2(0, -unit * 0.24f),
            ink, line * 1.3f);

        // A solid head, which survives being small in a way an outlined one does not.
        Polygon(into, ink,
            at + new Vector2(0, -unit * 0.82f),
            at + new Vector2(-unit * 0.44f, -unit * 0.16f),
            at + new Vector2(unit * 0.44f, -unit * 0.16f));
    }

    /// <summary>An arc over a gap. Hop, scheduled at a moment along the route.</summary>
    public static void Hop(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Arc(into, at + new Vector2(0, unit * 0.55f), unit * 0.8f, 200, 340, ink, line);
        Polygon(into, ink,
            at + new Vector2(0, -unit * 0.85f),
            at + new Vector2(-unit * 0.4f, -unit * 0.25f),
            at + new Vector2(unit * 0.4f, -unit * 0.25f));
    }

    /// <summary>A tick. This plan is locked in.</summary>
    public static void Committed(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        into.DrawPolyline(
            new[]
            {
                at + new Vector2(-unit * 0.75f, 0),
                at + new Vector2(-unit * 0.2f, unit * 0.6f),
                at + new Vector2(unit * 0.8f, -unit * 0.65f),
            },
            ink, size * Stroke * 1.6f);
    }

    /// <summary>
    /// A flower, for whoever took the flowerbed.
    /// </summary>
    /// <remarks>
    /// The moles are four platoons who each believe the flowerbed is rightfully theirs, so the
    /// prize is the flower and nothing has to be written down to say who got it. Petals take the
    /// platoon's colour and the eye stays cream, which reads on any of the four.
    /// </remarks>
    public static void Flower(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        for (int petal = 0; petal < 6; petal++)
        {
            float angle = petal * Mathf.Tau / 6f;

            into.DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * unit * 0.58f),
                unit * 0.42f, ink);
        }

        into.DrawCircle(at, unit * 0.34f, Palette.Paper);
    }

    /// <summary>An arrow pointing back the way you came.</summary>
    public static void Back(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        into.DrawLine(
            at + new Vector2(-unit * 0.25f, 0),
            at + new Vector2(unit * 0.72f, 0), ink, size * Stroke * 1.4f);

        Polygon(into, ink,
            at + new Vector2(-unit * 0.78f, 0),
            at + new Vector2(-unit * 0.12f, -unit * 0.52f),
            at + new Vector2(-unit * 0.12f, unit * 0.52f));
    }

    /// <summary>A play triangle. Start the match.</summary>
    public static void Play(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Polygon(into, ink,
            at + new Vector2(unit * 0.82f, 0),
            at + new Vector2(-unit * 0.55f, -unit * 0.72f),
            at + new Vector2(-unit * 0.55f, unit * 0.72f));
    }

    // ---- Playing together, and playing apart ----------------------------------------

    /// <summary>
    /// One screen with everybody round it. Playing on the couch.
    /// </summary>
    /// <remarks>
    /// The distinction the menu has to draw is not "offline versus online", which is a word about
    /// plumbing, but "all of us here" versus "us in different places". So this is a device with
    /// several moles inside it and the online ones are a mole with something travelling away.
    /// </remarks>
    public static void Couch(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawRect(
            new Rect2(at - new Vector2(unit * 0.9f, unit * 0.68f), new Vector2(unit * 1.8f, unit * 1.24f)),
            ink, false, line);

        for (int mole = 0; mole < 3; mole++)
        {
            Mole(into, at + new Vector2((mole - 1) * unit * 0.58f, -unit * 0.04f), unit * 0.52f, ink);
        }
    }

    /// <summary>
    /// Two moles shoulder to shoulder. Playing with people you already know.
    /// </summary>
    /// <remarks>
    /// It has to be told apart from the two glyphs it shares a screen with, and both of those are
    /// also pictures of more than one mole. The couch is a box with a row of moles evenly spaced in
    /// it, which reads as seats; the strangers are two hills a long way apart with an arc between
    /// them, which reads as distance. This is two moles and nothing else, close enough to touch and
    /// with no furniture and no ground: the point of it is who they are rather than where.
    ///
    /// Bigger than the moles on the couch, because there are two rather than three and the box is
    /// gone, so there is room. A glyph is only as good as the weakest one in its row.
    /// </remarks>
    public static void Friends(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Mole(into, at + new Vector2(-unit * 0.6f, 0f), unit * 1.45f, ink);
        Mole(into, at + new Vector2(unit * 0.6f, 0f), unit * 1.45f, ink);
    }

    /// <summary>
    /// A molehill sending something out. Opening a lobby for other people to find.
    /// </summary>
    public static void Broadcast(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        // The hill, which is the game's own silhouette and keeps this from being a generic aerial.
        Polygon(into, ink,
            at + new Vector2(-unit * 0.85f, unit * 0.82f),
            at + new Vector2(0, unit * 0.06f),
            at + new Vector2(unit * 0.85f, unit * 0.82f));

        Mole(into, at + new Vector2(0, unit * 0.16f), unit * 0.5f, Palette.Paper);

        // Going out, rather than coming in, which is the difference between hosting and joining.
        for (int ring = 1; ring <= 3; ring++)
        {
            Arc(into, at + new Vector2(0, unit * 0.2f), unit * (0.42f + (ring * 0.28f)),
                205, 335, new Color(ink, 1f - (ring * 0.22f)), line * 0.85f);
        }
    }

    /// <summary>
    /// Two molehills a long way apart, joined. A match with somebody nobody here knows.
    /// </summary>
    /// <remarks>
    /// Two hills rather than one, because what separates this from hosting is not what you do, it is
    /// who is at the other end: hosting sends something out and waits for the people you told, and
    /// this reaches somebody who was doing the same thing somewhere else.
    ///
    /// Deliberately not dice and deliberately not a shuffle arrow, which are the two obvious ways to
    /// draw "random" and are both wrong here. The design rules out anything that looks like gambling,
    /// and it rules it out on the store page as firmly as in the shop: a die on the front menu of an
    /// all-ages game is a conversation with a ratings board nobody needs to have.
    /// </remarks>
    public static void Strangers(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;
        float apart = unit * 0.56f;
        float floor = unit * 0.78f;

        // Sized against the couch and the tiles beside it rather than against the box it is given.
        // The first attempt used the box, came out as two faint bumps, and read as a smudge next to
        // three glyphs that read as objects. A glyph is only as good as the weakest one in the row.
        for (int side = -1; side <= 1; side += 2)
        {
            Vector2 hill = at + new Vector2(side * apart, 0f);

            Polygon(into, ink,
                hill + new Vector2(-unit * 0.62f, floor),
                hill + new Vector2(0, -unit * 0.30f),
                hill + new Vector2(unit * 0.62f, floor));

            Mole(into, hill + new Vector2(0, unit * 0.26f), unit * 0.44f, Palette.Paper);
        }

        // The distance between them, as the arc something has to cross rather than as a line. A
        // straight one drawn between two hills reads as the ground they are both standing on, and
        // it has to clear both peaks or it reads as a rope somebody has slung through them.
        Arc(
            into, at + new Vector2(0, -unit * 0.18f), apart * 1.02f,
            200, 340, ink, line * 0.9f);
    }

    /// <summary>
    /// Five empty tiles. A code to type in, which is the only way into somebody else's game.
    /// </summary>
    public static void Tiles(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke * 0.8f;
        float wide = unit * 0.3f;
        float tall = unit * 0.46f;

        for (int tile = 0; tile < 5; tile++)
        {
            into.DrawRect(
                new Rect2(
                    at + new Vector2(((tile - 2) * wide * 1.22f) - (wide / 2f), -tall / 2f),
                    new Vector2(wide, tall)),
                ink, false, line);
        }
    }

    /// <summary>
    /// A moon. The Anytime pace, where a round window is measured in hours and the answer to
    /// "when do I play" is "whenever", including after everybody has gone to bed.
    /// </summary>
    public static void Moon(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        into.DrawCircle(at, unit * 0.78f, ink);

        // Bitten out rather than drawn as a crescent, because a crescent from primitives at this
        // size reads as a banana.
        into.DrawCircle(at + new Vector2(unit * 0.42f, -unit * 0.26f), unit * 0.62f, Palette.Panel);
    }

    /// <summary>
    /// Ears listening. Waiting for the other platoons to commit, which is where an online match
    /// spends nearly all of its life.
    /// </summary>
    public static void Waiting(CanvasItem into, Vector2 at, float size, int howMany, Color ink)
    {
        float unit = size / 2f;

        for (int dot = 0; dot < Mathf.Max(1, howMany); dot++)
        {
            into.DrawCircle(
                at + new Vector2((dot - ((Mathf.Max(1, howMany) - 1) / 2f)) * unit * 0.62f, 0),
                unit * 0.2f,
                ink);
        }
    }

    // ---- Things to say --------------------------------------------------------------

    /// <summary>
    /// The emote wheel, which is the only communication channel in the game.
    /// </summary>
    /// <remarks>
    /// Pictures, because there are no words. The design lists the emote wheel among the load-bearing
    /// glyph work next to the weapon objects and the four helmets, and it wants edge rather than
    /// blandness: a pointed "nice shot" and a very sarcastic "after you" are both named outright.
    /// </remarks>
    public static void Say(CanvasItem into, Molehill.Online.Emote emote, Vector2 at, float size, Color ink)
    {
        switch (emote)
        {
            case Molehill.Online.Emote.WatchOut:
                Alarm(into, at, size, ink);
                return;

            case Molehill.Online.Emote.NiceShot:
                Applause(into, at, size, ink);
                return;

            case Molehill.Online.Emote.WellPlayed:
                Paw(into, at, size, ink);
                return;

            case Molehill.Online.Emote.Laughing:
                Laugh(into, at, size, ink);
                return;

            case Molehill.Online.Emote.AfterYou:
                Bow(into, at, size, ink);
                return;

            case Molehill.Online.Emote.Oops:
                Wince(into, at, size, ink);
                return;

            case Molehill.Online.Emote.Thinking:
                Thought(into, at, size, ink);
                return;

            default:
                Flag(into, at, size, ink);
                return;
        }
    }

    /// <summary>An exclamation in a triangle. The only emote that is ever urgent.</summary>
    private static void Alarm(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawPolyline(
            new[]
            {
                at + new Vector2(0, -unit * 0.9f),
                at + new Vector2(unit * 0.85f, unit * 0.7f),
                at + new Vector2(-unit * 0.85f, unit * 0.7f),
                at + new Vector2(0, -unit * 0.9f),
            },
            ink,
            line);

        into.DrawLine(
            at + new Vector2(0, -unit * 0.35f), at + new Vector2(0, unit * 0.15f), ink, line * 1.3f);
        into.DrawCircle(at + new Vector2(0, unit * 0.42f), line * 0.9f, ink);
    }

    /// <summary>Two paws clapping, with the motion that makes it applause rather than a pair of blobs.</summary>
    private static void Applause(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Oval(into, at + new Vector2(-unit * 0.34f, unit * 0.06f), unit * 0.4f, unit * 0.52f, ink);
        Oval(into, at + new Vector2(unit * 0.34f, unit * 0.06f), unit * 0.4f, unit * 0.52f, ink);

        for (int ray = -1; ray <= 1; ray++)
        {
            float angle = Mathf.DegToRad(-90f + (ray * 34f));

            into.DrawLine(
                at + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * unit * 0.68f,
                at + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * unit * 1.0f,
                ink,
                line * 0.8f);
        }
    }

    /// <summary>A raised paw. Well played, without the edge.</summary>
    private static void Paw(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Oval(into, at + new Vector2(0, unit * 0.28f), unit * 0.46f, unit * 0.4f, ink);

        for (int toe = -1; toe <= 1; toe++)
        {
            into.DrawCircle(
                at + new Vector2(toe * unit * 0.36f, -unit * 0.22f), unit * 0.19f, ink);
        }

        into.DrawCircle(at + new Vector2(unit * 0.55f, unit * 0.12f), unit * 0.15f, ink);
    }

    /// <summary>A mole creased up. The one a comedy game cannot do without.</summary>
    private static void Laugh(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawCircle(at, unit * 0.7f, ink);
        into.DrawCircle(at + new Vector2(-unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);

        // Eyes screwed shut and a wide open mouth, which is what separates laughing from a mole head.
        for (int eye = -1; eye <= 1; eye += 2)
        {
            Arc(into, at + new Vector2(eye * unit * 0.26f, -unit * 0.16f), unit * 0.16f,
                200, 340, Palette.Paper, line * 0.9f);
        }

        Oval(into, at + new Vector2(0, unit * 0.3f), unit * 0.26f, unit * 0.2f, Palette.Paper);
    }

    /// <summary>
    /// A sweeping bow. The design's "very sarcastic 'after you'".
    /// </summary>
    /// <remarks>
    /// A mole bent double with an arm thrown out. The sarcasm lives in the flourish: a small bow reads
    /// as politeness, and an enormous one reads as what it is.
    /// </remarks>
    private static void Bow(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        // The bent mole, head down and low.
        into.DrawCircle(at + new Vector2(-unit * 0.45f, unit * 0.34f), unit * 0.34f, ink);
        Oval(into, at + new Vector2(-unit * 0.02f, unit * 0.1f), unit * 0.38f, unit * 0.28f, ink);

        // The arm, sweeping right and up, far further than anybody means it.
        Arc(into, at + new Vector2(unit * 0.2f, unit * 0.1f), unit * 0.78f, 250, 355, ink, line * 1.1f);
        Polygon(into, ink,
            at + new Vector2(unit * 0.95f, -unit * 0.42f),
            at + new Vector2(unit * 0.5f, -unit * 0.52f),
            at + new Vector2(unit * 0.86f, -unit * 0.88f));
    }

    /// <summary>A wince. Owning a mistake, which is most of this game.</summary>
    private static void Wince(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawCircle(at, unit * 0.7f, ink);
        into.DrawCircle(at + new Vector2(-unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.55f, -unit * 0.62f), unit * 0.26f, ink);

        // One eye shut, one open, and a crooked mouth. A symmetrical wince reads as a grimace.
        into.DrawLine(
            at + new Vector2(-unit * 0.42f, -unit * 0.16f),
            at + new Vector2(-unit * 0.1f, -unit * 0.16f), Palette.Paper, line * 0.9f);
        into.DrawCircle(at + new Vector2(unit * 0.26f, -unit * 0.16f), unit * 0.1f, Palette.Paper);
        into.DrawLine(
            at + new Vector2(-unit * 0.24f, unit * 0.34f),
            at + new Vector2(unit * 0.3f, unit * 0.2f), Palette.Paper, line * 0.9f);
    }

    /// <summary>A thought bubble. Give me a moment.</summary>
    private static void Thought(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Oval(into, at + new Vector2(0, -unit * 0.16f), unit * 0.82f, unit * 0.56f, ink);

        into.DrawCircle(at + new Vector2(-unit * 0.4f, unit * 0.6f), unit * 0.16f, ink);
        into.DrawCircle(at + new Vector2(-unit * 0.66f, unit * 0.86f), unit * 0.1f, ink);

        for (int dot = -1; dot <= 1; dot++)
        {
            into.DrawCircle(
                at + new Vector2(dot * unit * 0.34f, -unit * 0.16f), unit * 0.1f, Palette.Paper);
        }
    }

    /// <summary>
    /// A flag. Truce?
    /// </summary>
    /// <remarks>
    /// Here to buy back something the design says cutting text chat costs: "no coordinating a
    /// temporary alliance in a four-way match". A truce cannot be agreed without words, but it can be
    /// proposed with a picture and accepted by not shooting somebody.
    /// </remarks>
    private static void Flag(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawLine(
            at + new Vector2(-unit * 0.5f, unit * 0.9f),
            at + new Vector2(-unit * 0.5f, -unit * 0.9f), ink, line * 1.2f);

        Polygon(into, ink,
            at + new Vector2(-unit * 0.5f, -unit * 0.85f),
            at + new Vector2(unit * 0.85f, -unit * 0.5f),
            at + new Vector2(-unit * 0.5f, -unit * 0.1f));
    }

    /// <summary>
    /// A paw with one digit out, pressing. The hand in the first-run demonstration.
    /// </summary>
    /// <remarks>
    /// Deliberately not the raised paw the emote wheel uses. That one means "well played" and reads as
    /// a greeting; this one has to read as a finger on a control, which needs the extended digit and
    /// the tilt. A game whose only tutorial is one drawn hand should draw a hand that is obviously
    /// doing something.
    /// </remarks>
    public static void Pointing(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        // The pad, tilted, so it reads as a hand at an angle rather than a blob.
        Oval(into, at + new Vector2(unit * 0.16f, unit * 0.42f), unit * 0.44f, unit * 0.52f, ink);

        // Three folded digits down the near side.
        for (int digit = 0; digit < 3; digit++)
        {
            into.DrawCircle(
                at + new Vector2(unit * (0.5f - (digit * 0.02f)), unit * (0.16f + (digit * 0.34f))),
                unit * 0.17f,
                ink);
        }

        // And the one that is out, pointing up and slightly back, with its tip where the press is.
        Polygon(into, ink,
            at + new Vector2(-unit * 0.34f, unit * 0.44f),
            at + new Vector2(-unit * 0.06f, unit * 0.6f),
            at + new Vector2(unit * 0.12f, -unit * 0.7f),
            at + new Vector2(-unit * 0.2f, -unit * 0.72f));

        into.DrawCircle(at + new Vector2(-unit * 0.04f, -unit * 0.72f), unit * 0.16f, ink);
    }

    // ---- Primitives -----------------------------------------------------------------

    private static void Arc(
        CanvasItem into, Vector2 at, float radius, float fromDegrees, float toDegrees,
        Color ink, float width)
    {
        float from = Mathf.DegToRad(fromDegrees);
        float to = Mathf.DegToRad(toDegrees);

        if (to <= from)
        {
            to += Mathf.Tau;
        }

        into.DrawArc(at, radius, from, to, 28, ink, width);
    }

    private static void Oval(CanvasItem into, Vector2 at, float halfWidth, float halfHeight, Color ink)
    {
        Vector2[] points = new Vector2[24];

        for (int point = 0; point < points.Length; point++)
        {
            float angle = point * Mathf.Tau / points.Length;
            points[point] = at + new Vector2(
                Mathf.Cos(angle) * halfWidth, Mathf.Sin(angle) * halfHeight);
        }

        into.DrawColoredPolygon(points, ink);
    }

    private static void Polygon(CanvasItem into, Color ink, params Vector2[] points)
    {
        into.DrawColoredPolygon(points, ink);
    }
}
