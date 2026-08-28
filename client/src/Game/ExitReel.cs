using Godot;
using MoleSim.Match;

/// <summary>
/// The eight ways a mole leaves the field.
/// </summary>
/// <remarks>
/// The simulation has chosen between all eight since Phase 1 and recorded which, so nothing is
/// decided here: this only works out where the chosen one goes and how far through it is.
///
/// That matters more than most presentation work, because the gate this phase exists to pass is a
/// question about whether the game is funny and the reel is the game's main answer to it. Nobody is
/// knocked out in this game: they are carried off by two worm medics, sat down among cartoon birds,
/// inflated and zipped off on a long raspberry, punched clean through a dirt wall, launched out of
/// frame leaving a helmet spinning, dropped through the floor delighted, or taken by a burst of
/// steam. The design puts the stretcher squad on the store page.
///
/// All eight were drawn from primitives until the art arrived, which was the right way round while
/// there was no art: it left the prototype with no dependency and it said, in the file that drew
/// them, that each one could be handed to an illustrator later without anything around it changing.
/// That is what has happened, and the primitives are in the history if the sheets disappoint.
///
/// Seven of the eight. Three sheets have their scenery drawn into the frames, and that took two
/// looks at a frame to sort out. All three are planted on the surface below the mole rather than on
/// the mole, because a knockout usually punts a mole into the air first, and drawn where the mole
/// actually was, a band of ground hung in the sky. They were always ground-level gags anyway: you
/// sink into the ground, you get punched through a wall standing on it, and a helmet lands on it.
///
/// That fixed two of them. The mole-shaped hole it did not fix, because every cell of that sheet is
/// a solid rectangle of dirt wall from edge to edge: the wall is the joke and it is drawn rather
/// than implied, so over a procedural hillside it is a slab dropped on the landscape wherever it is
/// put. Until there is a version of that sheet with nothing behind the mole, MoleShapedHole borrows
/// the spin-and-poof frames. The simulation still chose it, still recorded it and still knocked the
/// mole out for the reason it did; only the picture is shared, and that is the right way round for
/// something the art cannot do yet.
/// </remarks>
public static class ExitReel
{
    /// <summary>
    /// Whether this exit brings its own scenery, and so belongs on the ground rather than on the mole.
    /// </summary>
    public static bool Grounded(KnockoutExit exit) =>
        exit is KnockoutExit.HelmetSpin or KnockoutExit.UndergroundExpress;

    /// <summary>
    /// Plays one exit at whatever point it has reached, from nothing to finished.
    /// </summary>
    /// <param name="into">The pane being drawn.</param>
    /// <param name="exit">Which piece of slapstick the simulation chose.</param>
    /// <param name="at">Where the mole went out, in pane pixels.</param>
    /// <param name="pixelsPerMetre">How far in the camera is, so every exit scales with the zoom.</param>
    /// <param name="seat">Whose mole it was, which picks the platoon's copy of the artwork.</param>
    /// <param name="life">How far through, from zero to one.</param>
    public static void Play(
        CanvasItem into, KnockoutExit exit, Vector2 at, float pixelsPerMetre, int seat, float life)
    {
        Strip reel = Art.Exit(seat, Named(exit));
        Vector2 size = reel.FrameSize * (pixelsPerMetre / Art.MolePixelsPerMetre);
        float radius = (float)MatchSettings.Radius.ToDecimal() * pixelsPerMetre;

        // Clamped rather than wrapped, because an exit is a thing that happens once. Wrapped, a
        // mole carried off on a stretcher would be carried off again, and again.
        int frame = Mathf.Clamp(Mathf.FloorToInt(life * reel.Frames), 0, reel.Frames - 1);

        reel.Draw(
            into,
            new Rect2(at.X - (size.X / 2f), at.Y + radius - size.Y, size),
            frame,
            mirrored: false);
    }

    /// <summary>
    /// Which set of frames an exit is, by the name the importer gave it.
    /// </summary>
    /// <remarks>
    /// The nine death sheets turned out to be these eight plus a plain launch that matches none of
    /// them, so there was no mapping work to do beyond writing it down. The launch is imported and
    /// unused, waiting for something that wants a mole thrown into the air without a punchline.
    /// </remarks>
    private static string Named(KnockoutExit exit) => exit switch
    {
        KnockoutExit.StretcherSquad => "stretcher",
        KnockoutExit.DizzyBirds => "birds",
        KnockoutExit.BalloonExit => "balloon",
        // Not "hole". See the remarks: that sheet is a wall with a mole in front of it.
        KnockoutExit.MoleShapedHole => "poof",
        KnockoutExit.HelmetSpin => "helmet",
        KnockoutExit.UndergroundExpress => "sink",
        KnockoutExit.SteamPop => "steam",
        _ => "poof",
    };
}
