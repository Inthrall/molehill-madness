using Godot;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

/// <summary>
/// Draws a mole, and works out which of its poses to draw.
/// </summary>
/// <remarks>
/// A mole was three circles and a snout until the art arrived, which was honest programmer art and
/// did one thing well: it said which platoon a mole belonged to, at any size, with no assets at
/// all. What it could not say is what a mole was doing, and by the end of Phase 2 a mole could be
/// walking, tunnelling, tumbling through the air, snared in roots or wearing a pair of power claws,
/// and all five looked the same.
///
/// So the pose is chosen from what the simulation already knows rather than from anything new. That
/// matters more than it sounds: the simulation is the game and the client is a lens pointed at it,
/// so a pose that needed its own state would be a second answer to a question already answered, and
/// the two would drift. Everything here is derived, and where the replay has less to go on than the
/// live match does, it derives the same facts from the recording instead.
/// </remarks>
public static class Moles
{
    /// <summary>Ticks a frame of a cycling animation lasts. Ten a second, at thirty ticks.</summary>
    private const int TicksPerFrame = 3;

    /// <summary>How fast a mole has to be going, in metres a second, to count as moving.</summary>
    private const float MovingAt = 0.2f;

    /// <summary>The four tumble poses the artist drew, before the mirrored four.</summary>
    private const int TumblePoses = 4;

    /// <summary>
    /// Which pose, from what the mole is doing.
    /// </summary>
    /// <remarks>
    /// Ordered by what overrides what, and the order is the whole content of this function. Snared
    /// first, because a mole held by roots is not doing anything else whatever else is true of it.
    /// Airborne next, since being in the air is the most visible thing that can be happening.
    /// Then underground, where the claws pose replaces the digging one if the mole is wearing them,
    /// because that is the moment the claws are worth seeing. Standing is what is left.
    ///
    /// Aiming comes before all of it, because a mole lining up a shot is not doing anything else
    /// and because it is the one pose the player is choosing rather than watching. Walking is last
    /// but one: it only reads as walking if the mole is going somewhere, and standing is what is
    /// left over.
    /// </remarks>
    public static string Pose(
        bool aiming, bool snared, bool airborne, bool underground, bool clawed, bool walking)
    {
        if (aiming)
        {
            return "aim";
        }

        if (snared)
        {
            return "rooted";
        }

        if (airborne)
        {
            return "airborne";
        }

        if (underground)
        {
            return clawed ? "claws" : "dig";
        }

        return walking ? "walk" : "stand";
    }

    /// <summary>
    /// Which of the five aims, from the direction the player is pointing.
    /// </summary>
    /// <remarks>
    /// The artist drew sixty degrees down, thirty down, level, thirty up and sixty up, so the
    /// nearest of those five is the answer and the aim is measured rather than stored. Which way
    /// the mole is facing is handled by mirroring, so only the elevation matters here and the sign
    /// of the horizontal is thrown away.
    ///
    /// Up is negative, because this world has its Y axis pointing down.
    /// </remarks>
    public static int AimFrame(Vec2 aim)
    {
        float across = Mathf.Abs((float)aim.X.ToDecimal());
        float up = -(float)aim.Y.ToDecimal();
        float degrees = Mathf.RadToDeg(Mathf.Atan2(up, across));

        return Mathf.Clamp(Mathf.RoundToInt((degrees + 60f) / 30f), 0, 4);
    }

    /// <summary>Whether a mole is moving across the ground fast enough to be walking.</summary>
    public static bool Walking(Vec2 velocity)
    {
        float across = (float)velocity.X.ToDecimal();

        return across > MovingAt || across < -MovingAt;
    }

    /// <summary>
    /// Whether there is ground over this mole's head, which is what digging looks like from outside.
    /// </summary>
    /// <remarks>
    /// A cell above the body rather than the one the body is in. A tunnelling mole stands in the
    /// hole it has just made, so the cell at its centre always reads as air; the same trap the
    /// movement solver fell into twice, and the same answer, which is to sample somewhere the mole
    /// has not already cleared.
    /// </remarks>
    public static bool Underground(Vec2 position, TerrainGrid ground)
    {
        int cellX = WorldScale.ToCell(position.X);
        int cellY = WorldScale.ToCell(position.Y - MatchSettings.Radius) - 2;

        return ground.Contains(cellX, cellY) && MaterialTable.IsSolid(ground[cellX, cellY]);
    }

    /// <summary>Which frame of a cycling pose, at this tick.</summary>
    /// <remarks>
    /// Tumbling is the exception and is not a cycle: the artist drew four poses and then the same
    /// four mirrored, so a mole falling to the left uses the second four rather than a flipped copy
    /// of the first. Flipping them would work and would also flip the shading, which on a mole lit
    /// from one side reads as the light moving with it.
    /// </remarks>
    public static int Frame(string pose, int tick, bool facingLeft)
    {
        if (pose == "airborne")
        {
            return (tick / TicksPerFrame % TumblePoses) + (facingLeft ? TumblePoses : 0);
        }

        return tick / TicksPerFrame;
    }

    /// <summary>Whether a mole is going left, and so should be drawn mirrored.</summary>
    public static bool FacingLeft(Vec2 velocity, bool wasLeft) =>
        (float)velocity.X.ToDecimal() < -MovingAt
            || ((float)velocity.X.ToDecimal() <= MovingAt && wasLeft);

    /// <summary>
    /// Draws one mole, standing on the bottom of its own artwork.
    /// </summary>
    /// <remarks>
    /// Planted by its feet rather than centred on the simulation's position, because the position
    /// is the centre of a collision circle and the artwork is a mole with a head on it, which is
    /// half again as tall as the circle is wide. Centred, every mole would stand shin-deep in the
    /// ground.
    /// </remarks>
    /// <param name="at">The mole's position, in world pixels.</param>
    /// <param name="pixelsPerMetre">How far in the camera is.</param>
    public static void Draw(
        CanvasItem into, Vector2 at, float pixelsPerMetre, int seat,
        string pose, int frame, bool facingLeft)
    {
        Strip strip = Art.Mole(seat, pose);
        Vector2 size = strip.FrameSize * (pixelsPerMetre / Art.MolePixelsPerMetre);
        float radius = (float)MatchSettings.Radius.ToDecimal() * pixelsPerMetre;

        strip.Draw(
            into,
            new Rect2(at.X - (size.X / 2f), at.Y + radius - size.Y, size),
            frame,
            facingLeft);
    }
}
