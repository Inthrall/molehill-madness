using Godot;
using MoleSim.Match;

/// <summary>
/// The eight ways a mole leaves the field.
/// </summary>
/// <remarks>
/// The simulation has chosen between all eight since Phase 1 and recorded which, and the client
/// drew two of them: the stretcher squad, and spin-and-poof for everything else. Six deterministic,
/// already-recorded outcomes were rendering as the wrong joke.
///
/// That matters more than most presentation gaps, because the gate this phase exists to pass is a
/// question about whether the game is funny, and the reel is the game's main answer to it. Nobody
/// is knocked out in this game: they are carried off, launched, deflated or dropped through the
/// floor, and the design puts the stretcher squad on the store page.
///
/// Drawn from primitives like everything else, so there is no art dependency and each of these can
/// be handed to an illustrator later without anything around it changing. Rotation is done by hand
/// rather than with a canvas transform, because the caller has already set one to put the world on
/// the pane and clobbering it would move everything else too.
/// </remarks>
public static class ExitReel
{
    /// <summary>
    /// Plays one exit at whatever point it has reached, from nothing to finished.
    /// </summary>
    /// <param name="into">The pane being drawn.</param>
    /// <param name="exit">Which piece of slapstick the simulation chose.</param>
    /// <param name="at">Where the mole went out, in pane pixels.</param>
    /// <param name="radius">The mole's drawn radius, so every exit scales with the zoom.</param>
    /// <param name="colour">The platoon's colour.</param>
    /// <param name="life">How far through, from zero to one.</param>
    public static void Play(
        CanvasItem into, KnockoutExit exit, Vector2 at, float radius, Color colour, float life)
    {
        switch (exit)
        {
            case KnockoutExit.StretcherSquad:
                Stretcher(into, at, radius, colour, life);
                break;

            case KnockoutExit.DizzyBirds:
                Dizzy(into, at, radius, colour, life);
                break;

            case KnockoutExit.BalloonExit:
                Balloon(into, at, radius, colour, life);
                break;

            case KnockoutExit.MoleShapedHole:
                ThroughTheWall(into, at, radius, colour, life);
                break;

            case KnockoutExit.HelmetSpin:
                Helmet(into, at, radius, colour, life);
                break;

            case KnockoutExit.UndergroundExpress:
                Express(into, at, radius, colour, life);
                break;

            case KnockoutExit.SteamPop:
                Steam(into, at, radius, colour, life);
                break;

            default:
                SpinAndPoof(into, at, radius, colour, life);
                break;
        }
    }

    // ---- The eight ------------------------------------------------------------------

    /// <summary>Spins like a top and vanishes, leaving its boots standing.</summary>
    /// <remarks>
    /// The dust ring is load-bearing. Without it the mole simply gets smaller, which reads as a
    /// rendering fault rather than as a joke, and the boots left behind are the punchline.
    /// </remarks>
    private static void SpinAndPoof(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        if (life < 1f)
        {
            into.DrawArc(
                at, radius * (0.6f + (life * 2.2f)), 0, Mathf.Tau, 28,
                new Color(Palette.Dust, 1f - life), radius * 0.3f);
        }

        Boots(into, at, radius);

        Body(
            into,
            at + new Vector2(Mathf.Cos(life * 24f) * radius * 0.4f, -life * radius * 1.6f),
            Mathf.Lerp(radius, 0f, life), colour);
    }

    /// <summary>
    /// Two worm medics jog in with a tiny stretcher and carry the dizzy mole off, waving weakly.
    /// </summary>
    private static void Stretcher(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        Vector2 carried = at + new Vector2(life * radius * 3.5f, -life * radius * 0.5f);

        into.DrawLine(
            carried + new Vector2(-radius, radius * 0.6f),
            carried + new Vector2(radius, radius * 0.6f), Palette.Ink, radius * 0.22f);

        Body(into, carried, radius * 0.75f, colour);

        // Waving weakly at the camera, which is the whole character of the signature exit.
        float wave = Mathf.Sin(life * 26f) * radius * 0.35f;

        into.DrawLine(
            carried + new Vector2(radius * 0.5f, -radius * 0.1f),
            carried + new Vector2((radius * 0.9f) + wave, -radius * 0.9f),
            colour, radius * 0.16f);

        into.DrawCircle(carried + new Vector2(-radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
        into.DrawCircle(carried + new Vector2(radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
    }

    /// <summary>Sits down heavily, stars and birds, tips over like a plank.</summary>
    private static void Dizzy(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        const float SitsUntil = 0.45f;
        float tipped = life <= SitsUntil ? 0f : (life - SitsUntil) / (1f - SitsUntil);
        Vector2 seat = at + new Vector2(0, radius * 0.35f);

        // Over like a plank, so it pivots about its feet rather than sliding sideways.
        float lean = tipped * Mathf.Pi / 2f;
        Vector2 middle = Rotate(seat + new Vector2(0, -radius * 0.9f), seat, lean);

        Body(into, middle, radius, colour);
        Boots(into, seat + new Vector2(0, radius * 0.2f), radius * 0.8f);

        if (tipped >= 1f)
        {
            return;
        }

        // Stars and birds going round, which is how a cartoon says concussed without a word.
        for (int star = 0; star < 3; star++)
        {
            float angle = (life * 9f) + (star * Mathf.Tau / 3f);
            Vector2 spot = middle
                + new Vector2(Mathf.Cos(angle) * radius * 1.3f, (Mathf.Sin(angle) * radius * 0.45f) - (radius * 1.5f));
            float size = radius * 0.3f * (1f - tipped);

            into.DrawLine(spot - new Vector2(size, 0), spot + new Vector2(size, 0), Palette.Ink, radius * 0.1f);
            into.DrawLine(spot - new Vector2(0, size), spot + new Vector2(0, size), Palette.Ink, radius * 0.1f);
        }
    }

    /// <summary>
    /// Inflates like a balloon and zips off on a long raspberry, ricocheting once on the way.
    /// </summary>
    private static void Balloon(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        const float InflatesUntil = 0.38f;

        if (life <= InflatesUntil)
        {
            float swell = life / InflatesUntil;

            Body(into, at, radius * (1f + (swell * 1.3f)), colour);
            return;
        }

        float flown = (life - InflatesUntil) / (1f - InflatesUntil);

        // One bounce, which the design asks for by name: off a molehill on the way out.
        float bounce = Mathf.Abs(Mathf.Sin(flown * Mathf.Pi * 1.5f));
        Vector2 gone = at + new Vector2(
            flown * radius * 14f, -(bounce * radius * 3.5f) - (flown * radius * 2f));

        // The raspberry, as a trail of escaping puffs behind it.
        for (int puff = 1; puff <= 3; puff++)
        {
            float behind = flown - (puff * 0.09f);

            if (behind <= 0f)
            {
                continue;
            }

            float wasBouncing = Mathf.Abs(Mathf.Sin(behind * Mathf.Pi * 1.5f));

            into.DrawCircle(
                at + new Vector2(
                    behind * radius * 14f,
                    -(wasBouncing * radius * 3.5f) - (behind * radius * 2f)),
                radius * 0.3f * puff,
                new Color(Palette.Dust, 0.5f / puff));
        }

        Body(into, gone, radius * 2.3f * (1f - (flown * 0.7f)), colour);
    }

    /// <summary>
    /// Punched clean through a dirt wall, leaving a perfect mole-shaped hole, then salutes.
    /// </summary>
    private static void ThroughTheWall(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        // The hole stays for the whole reel, because the hole is the joke. Dark rather than sky
        // coloured: a hole in dirt reads as a shadow, and painting it cream made it read as the
        // mole still standing there. It is a silhouette with no snout for the same reason, since a
        // pink nose in the middle of a hole is a ghost rather than an absence.
        Silhouette(into, at, radius * 1.15f, new Color(Palette.Ink, 0.5f));

        if (life < 0.2f)
        {
            return;
        }

        float away = (life - 0.2f) / 0.8f;
        Vector2 gone = at + new Vector2(radius * (2f + (away * 9f)), 0);

        Body(into, gone, radius, colour);

        // A salute on the way out, from a mole who takes the war extremely seriously.
        if (away > 0.55f)
        {
            into.DrawLine(
                gone + new Vector2(radius * 0.3f, -radius * 0.2f),
                gone + new Vector2(radius * 0.75f, -radius * 0.95f), colour, radius * 0.18f);
        }
    }

    /// <summary>
    /// Launched straight up out of frame. The helmet stays, spinning for an unreasonably long
    /// time, then clangs flat.
    /// </summary>
    private static void Helmet(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        const float Launched = 0.28f;
        const float Clangs = 0.86f;

        if (life < Launched)
        {
            float up = life / Launched;

            Body(into, at + new Vector2(0, -up * radius * 9f), radius, new Color(colour, 1f - up));
        }

        Vector2 rest = at + new Vector2(0, radius * 0.55f);

        if (life >= Clangs)
        {
            // Flat, and the brim is all that is left of it.
            into.DrawLine(
                rest + new Vector2(-radius * 0.85f, 0), rest + new Vector2(radius * 0.85f, 0),
                Palette.Ink, radius * 0.3f);
            return;
        }

        // Spinning on the spot: the dome squashes and stretches rather than being rotated, which
        // is the same read at a fraction of the arithmetic.
        float spin = Mathf.Cos(life * 34f);
        float half = Mathf.Max(Mathf.Abs(spin) * radius * 0.85f, radius * 0.12f);

        into.DrawLine(
            rest + new Vector2(-half, 0), rest + new Vector2(half, 0), Palette.Ink, radius * 0.24f);
        into.DrawArc(
            rest, radius * 0.6f, Mathf.Pi, Mathf.Tau, 16, Palette.Ink, radius * 0.26f);
    }

    /// <summary>The floor gives way and it drops through, delighted.</summary>
    private static void Express(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        // The hole it went down, which stays open behind it.
        into.DrawColoredPolygon(
            new[]
            {
                at + new Vector2(-radius * 1.2f, radius * 0.4f),
                at + new Vector2(radius * 1.2f, radius * 0.4f),
                at + new Vector2(radius * 0.9f, radius * 1.5f),
                at + new Vector2(-radius * 0.9f, radius * 1.5f),
            },
            new Color(Palette.Ink, 0.55f));

        if (life >= 0.75f)
        {
            return;
        }

        float down = life / 0.75f;
        Vector2 sinking = at + new Vector2(0, down * radius * 2.6f);

        Body(into, sinking, radius * (1f - (down * 0.35f)), colour);

        // Both arms up on the way down, because the design says delighted rather than alarmed.
        for (int side = -1; side <= 1; side += 2)
        {
            into.DrawLine(
                sinking + new Vector2(side * radius * 0.4f, -radius * 0.2f),
                sinking + new Vector2(side * radius * 0.95f, -radius * 1.0f),
                colour, radius * 0.16f);
        }
    }

    /// <summary>Hops about going hot hot hot, then a burst of steam takes it.</summary>
    private static void Steam(
        CanvasItem into, Vector2 at, float radius, Color colour, float life)
    {
        const float HopsUntil = 0.55f;

        if (life <= HopsUntil)
        {
            float hopping = life / HopsUntil;

            // Quick and small, and side to side as well as up, which is what reads as panic
            // rather than as a mole calmly jumping.
            Vector2 hop = new Vector2(
                Mathf.Sin(life * 40f) * radius * 0.5f,
                -Mathf.Abs(Mathf.Sin(life * 30f)) * radius * 0.9f);

            Body(into, at + hop, radius, colour);

            for (int wisp = 0; wisp < 2; wisp++)
            {
                into.DrawCircle(
                    at + hop + new Vector2((wisp == 0 ? -1f : 1f) * radius * 0.8f, -radius * 1.2f),
                    radius * 0.22f * hopping, new Color(Palette.Dust, 0.6f));
            }

            return;
        }

        float burst = (life - HopsUntil) / (1f - HopsUntil);

        for (int puff = 0; puff < 5; puff++)
        {
            float angle = puff * Mathf.Tau / 5f;

            into.DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * burst * 2.6f),
                radius * (0.75f - (burst * 0.35f)),
                new Color(Palette.Dust, 1f - burst));
        }

        Boots(into, at, radius);
    }

    // ---- Parts ----------------------------------------------------------------------

    /// <summary>
    /// A mole, the same shape the live one is drawn in, so an exit does not look like a different
    /// animal leaving.
    /// </summary>
    private static void Body(CanvasItem into, Vector2 at, float radius, Color colour)
    {
        if (radius <= 0.5f)
        {
            return;
        }

        Silhouette(into, at, radius, colour);
        into.DrawCircle(at + new Vector2(0, radius * 0.34f), radius * 0.34f, Palette.Snout);
    }

    /// <summary>The mole's outline with nothing in it, for a hole shaped like one.</summary>
    private static void Silhouette(CanvasItem into, Vector2 at, float radius, Color colour)
    {
        if (radius <= 0.5f)
        {
            return;
        }

        into.DrawCircle(at, radius, colour);
        into.DrawCircle(at + new Vector2(-radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        into.DrawCircle(at + new Vector2(radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
    }

    /// <summary>The boots that stay behind, which is the punchline of more than one of these.</summary>
    private static void Boots(CanvasItem into, Vector2 at, float radius)
    {
        Vector2 boot = new Vector2(radius * 0.5f, radius * 0.55f);

        into.DrawRect(new Rect2(at.X - boot.X - 1f, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);
        into.DrawRect(new Rect2(at.X + 1f, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);
    }

    private static Vector2 Rotate(Vector2 point, Vector2 about, float radians)
    {
        Vector2 offset = point - about;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);

        return about + new Vector2(
            (offset.X * cos) - (offset.Y * sin),
            (offset.X * sin) + (offset.Y * cos));
    }
}
