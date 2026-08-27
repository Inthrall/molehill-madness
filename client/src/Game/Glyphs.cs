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

    public static void Weapon(CanvasItem into, WeaponId weapon, Vector2 at, float size, Color ink)
    {
        switch (weapon)
        {
            case WeaponId.ClodLobber:
                ClodLobber(into, at, size, ink);
                break;

            case WeaponId.BeetleLauncher:
                Beetle(into, at, size, ink);
                break;

            case WeaponId.AcornMortar:
                Acorns(into, at, size, ink);
                break;

            case WeaponId.Fracking:
                Derrick(into, at, size, ink);
                break;

            case WeaponId.BigWhack:
                Mallet(into, at, size, ink);
                break;

            case WeaponId.SnapTrap:
                Jaws(into, at, size, ink);
                break;

            case WeaponId.RootSnare:
                Snare(into, at, size, ink);
                break;

            case WeaponId.TunnelTorpedo:
                Torpedo(into, at, size, ink);
                break;

            case WeaponId.PowerClaws:
                Claws(into, at, size, ink);
                break;

            case WeaponId.Sandbag:
                Sack(into, at, size, ink);
                break;

            case WeaponId.GeyserCap:
                Geyser(into, at, size, ink);
                break;

            case WeaponId.BoomBeets:
                Beet(into, at, size, ink);
                break;

            case WeaponId.SpecialDelivery:
                Delivery(into, at, size, ink);
                break;

            case WeaponId.MolyHandGrenade:
                HolyGrenade(into, at, size, ink);
                break;

            case WeaponId.GnomeMercy:
                Gnome(into, at, size, ink);
                break;

            default:
                Nothing(into, at, size, ink);
                break;
        }
    }

    /// <summary>A clod of earth on its way up, with the arc it travels.</summary>
    private static void ClodLobber(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Arc(into, at + new Vector2(-unit * 0.1f, unit * 0.2f), unit * 0.95f, 200, 340, ink, line);
        into.DrawCircle(at + new Vector2(unit * 0.45f, -unit * 0.5f), unit * 0.34f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.2f, -unit * 0.72f), unit * 0.17f, ink);
        into.DrawCircle(at + new Vector2(unit * 0.72f, -unit * 0.24f), unit * 0.15f, ink);
    }

    /// <summary>A beetle, seen from above. Fast, hard, and goes off on arrival.</summary>
    private static void Beetle(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Oval(into, at + new Vector2(0, unit * 0.15f), unit * 0.55f, unit * 0.72f, ink);
        into.DrawCircle(at + new Vector2(0, -unit * 0.62f), unit * 0.28f, ink);
        into.DrawLine(
            at + new Vector2(0, -unit * 0.4f), at + new Vector2(0, unit * 0.8f),
            new Color(1, 1, 1, 0.85f), line * 0.7f);
        into.DrawLine(
            at + new Vector2(-unit * 0.12f, -unit * 0.8f),
            at + new Vector2(-unit * 0.6f, -unit * 1.0f), ink, line * 0.7f);
        into.DrawLine(
            at + new Vector2(unit * 0.12f, -unit * 0.8f),
            at + new Vector2(unit * 0.6f, -unit * 1.0f), ink, line * 0.7f);
    }

    /// <summary>Three acorns, because this one splits on the way down.</summary>
    private static void Acorns(CanvasItem into, Vector2 at, float size, Color ink)
    {
        Acorn(into, at + new Vector2(0, -size * 0.16f), size * 0.52f, ink);
        Acorn(into, at + new Vector2(-size * 0.29f, size * 0.24f), size * 0.38f, ink);
        Acorn(into, at + new Vector2(size * 0.29f, size * 0.24f), size * 0.38f, ink);
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

    /// <summary>A derrick with the shock going out through the soil.</summary>
    private static void Derrick(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawPolyline(
            new[]
            {
                at + new Vector2(-unit * 0.55f, unit * 0.2f),
                at + new Vector2(0, -unit * 0.95f),
                at + new Vector2(unit * 0.55f, unit * 0.2f),
            },
            ink, line);

        into.DrawLine(
            at + new Vector2(0, -unit * 0.3f), at + new Vector2(0, unit * 0.95f), ink, line);

        for (int side = -1; side <= 1; side += 2)
        {
            Arc(into, at + new Vector2(0, unit * 0.5f), unit * 0.75f,
                side < 0 ? 100 : 20, side < 0 ? 160 : 80, ink, line * 0.8f);
        }
    }

    /// <summary>The Big Whack. A mallet, and nothing else needed.</summary>
    private static void Mallet(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawRect(
            new Rect2(at.X - (unit * 0.85f), at.Y - (unit * 0.9f), unit * 1.7f, unit * 0.7f), ink);
        into.DrawLine(
            at + new Vector2(0, -unit * 0.2f), at + new Vector2(0, unit), ink, line * 1.6f);
    }

    /// <summary>Jaws waiting to shut.</summary>
    private static void Jaws(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        for (int side = -1; side <= 1; side += 2)
        {
            float y = unit * 0.5f * side;
            into.DrawLine(
                at + new Vector2(-unit * 0.9f, y), at + new Vector2(unit * 0.9f, y), ink, line);

            for (int tooth = -2; tooth <= 2; tooth++)
            {
                float x = tooth * unit * 0.36f;
                Polygon(into, ink,
                    at + new Vector2(x - (unit * 0.13f), y),
                    at + new Vector2(x + (unit * 0.13f), y),
                    at + new Vector2(x, y - (unit * 0.42f * side)));
            }
        }
    }

    /// <summary>A noose of root. Costs its victim exactly one turn.</summary>
    private static void Snare(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Arc(into, at + new Vector2(0, unit * 0.25f), unit * 0.62f, 0, 360, ink, line);

        into.DrawPolyline(
            new[]
            {
                at + new Vector2(-unit * 0.2f, -unit * 0.35f),
                at + new Vector2(-unit * 0.5f, -unit * 0.7f),
                at + new Vector2(-unit * 0.2f, -unit * 0.95f),
            },
            ink, line * 0.8f);

        into.DrawLine(
            at + new Vector2(unit * 0.5f, -unit * 0.2f),
            at + new Vector2(unit * 0.95f, -unit * 0.75f), ink, line * 0.8f);
    }

    /// <summary>A shell driving through dirt, with the tunnel behind it.</summary>
    private static void Torpedo(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Polygon(into, ink,
            at + new Vector2(unit * 0.95f, 0),
            at + new Vector2(unit * 0.1f, -unit * 0.42f),
            at + new Vector2(unit * 0.1f, unit * 0.42f));

        into.DrawRect(
            new Rect2(at.X - (unit * 0.45f), at.Y - (unit * 0.34f), unit * 0.55f, unit * 0.68f),
            ink);

        for (int trail = 0; trail < 3; trail++)
        {
            float x = -unit * (0.6f + (trail * 0.24f));
            into.DrawLine(
                at + new Vector2(x, -unit * 0.5f), at + new Vector2(x, unit * 0.5f),
                new Color(ink, 0.45f), line * 0.7f);
        }
    }

    /// <summary>Three claws, for digging cheap.</summary>
    private static void Claws(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        for (int claw = -1; claw <= 1; claw++)
        {
            into.DrawPolyline(
                new[]
                {
                    at + new Vector2(claw * unit * 0.5f, -unit * 0.9f),
                    at + new Vector2(claw * unit * 0.62f, unit * 0.2f),
                    at + new Vector2(claw * unit * 0.28f, unit * 0.9f),
                },
                ink, line * 1.1f);
        }
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

    /// <summary>A capped vent, and what comes out of it.</summary>
    private static void Geyser(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        Arc(into, at + new Vector2(0, unit * 0.85f), unit * 0.55f, 180, 360, ink, line * 1.3f);

        for (int jet = -1; jet <= 1; jet++)
        {
            into.DrawLine(
                at + new Vector2(jet * unit * 0.34f, unit * 0.25f),
                at + new Vector2(jet * unit * 0.62f, -unit * 0.9f),
                ink, line * 0.9f);
        }
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

    /// <summary>Three sacks arriving from above, out of reach of anybody buried.</summary>
    private static void Delivery(CanvasItem into, Vector2 at, float size, Color ink)
    {
        for (int sack = -1; sack <= 1; sack++)
        {
            Sack(into, at + new Vector2(sack * size * 0.32f, size * 0.2f), size * 0.44f, ink);
            into.DrawLine(
                at + new Vector2(sack * size * 0.32f, -size * 0.48f),
                at + new Vector2(sack * size * 0.32f, -size * 0.14f),
                new Color(ink, 0.45f), size * Stroke * 0.7f);
        }
    }

    /// <summary>The crate rarity, and it knows it.</summary>
    private static void HolyGrenade(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;
        float line = size * Stroke;

        into.DrawCircle(at + new Vector2(0, unit * 0.2f), unit * 0.68f, ink);
        into.DrawRect(
            new Rect2(at.X - (unit * 0.22f), at.Y - (unit * 0.72f), unit * 0.44f, unit * 0.35f),
            ink);

        into.DrawLine(
            at + new Vector2(0, -unit * 1.3f), at + new Vector2(0, -unit * 0.7f), ink, line);
        into.DrawLine(
            at + new Vector2(-unit * 0.3f, -unit * 1.05f),
            at + new Vector2(unit * 0.3f, -unit * 1.05f), ink, line);
    }

    /// <summary>Gnome Mercy. Punches through and bounces three times.</summary>
    private static void Gnome(CanvasItem into, Vector2 at, float size, Color ink)
    {
        float unit = size / 2f;

        Polygon(into, ink,
            at + new Vector2(-unit * 0.62f, -unit * 0.15f),
            at + new Vector2(unit * 0.62f, -unit * 0.15f),
            at + new Vector2(0, unit * -1.05f));
        into.DrawCircle(at + new Vector2(0, unit * 0.15f), unit * 0.42f, ink);
        Polygon(into, ink,
            at + new Vector2(-unit * 0.42f, unit * 0.25f),
            at + new Vector2(unit * 0.42f, unit * 0.25f),
            at + new Vector2(0, unit * 1.05f));
    }

    /// <summary>No weapon on the wheel: an empty pair of hands.</summary>
    private static void Nothing(CanvasItem into, Vector2 at, float size, Color ink)
    {
        Arc(into, at, size * 0.42f, 0, 360, new Color(ink, 0.5f), size * Stroke);
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
