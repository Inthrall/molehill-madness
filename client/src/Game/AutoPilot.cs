using System.Collections.Generic;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// Plays the game badly, on purpose, so a round can be watched without a human at the
/// keyboard.
/// </summary>
/// <remarks>
/// This is a test driver, not a bot. The design rules out opponent AI at launch and that
/// still stands: nothing here is ever offered to a player, and it exists only so the render
/// layer can be inspected without four people in the room. It is how the planning ghost, the
/// resolution playback and the knockout exits get checked frame by frame during development,
/// and it is what a recorded smoke test drives.
///
/// It plays through the same door a player does. It lays a route, stamps a shot and commits,
/// and has no access the interface does not, so a round it drives is a round somebody could
/// have played.
/// </remarks>
public sealed class AutoPilot
{
    private readonly MoleMatch _match;

    public AutoPilot(MoleMatch match)
    {
        _match = match;
    }

    /// <summary>
    /// A route toward the nearest mole from another platoon, and where the aim was dragged
    /// to.
    /// </summary>
    /// <remarks>
    /// The drag endpoint rather than a direction and a power, because that is the only thing
    /// a player's hand ever produces. Letting the driver hand over a power directly would
    /// give it a way into the game that no interface offers, and then a round it drove would
    /// no longer be a round somebody could have played.
    /// </remarks>
    public sealed class Intent
    {
        public Intent(List<Vec2> route, Vec2 aimAt)
        {
            Route = route;
            AimAt = aimAt;
        }

        public List<Vec2> Route { get; }

        public Vec2 AimAt { get; }
    }

    /// <summary>
    /// Decides what one mole does: walk a few metres toward the nearest enemy, then lob
    /// something at it.
    /// </summary>
    /// <remarks>
    /// Aiming flat at the target was the first attempt and it never hit anything, because a
    /// shell fired flat at twenty metres is in the dirt by ten. So this does the schoolbook
    /// forty-five degree solve instead: at that angle a projectile's range is its speed
    /// squared over gravity, so the speed wanted for a given range is the square root of
    /// range times gravity, and the power is that as a fraction of the weapon's full charge.
    ///
    /// It ignores terrain, height difference, wind and the fact that the target is walking
    /// away, so it still misses plenty. That is fine and even useful: what it needs to do is
    /// connect often enough that knockouts happen and the exits can be watched.
    ///
    /// Floating point is safe here in a way it never is inside the simulation. This produces
    /// a drag endpoint, which is quantised into a plan's wire encoding before anything
    /// deterministic sees it, exactly like a real hand on a real mouse.
    /// </remarks>
    public Intent Decide(Mole actor, WeaponId weapon)
    {
        Mole? quarry = Nearest(actor);
        List<Vec2> route = new List<Vec2>();

        if (quarry is null)
        {
            return new Intent(route, actor.Position + new Vec2(Fix64.One, -Fix64.One));
        }

        bool rightward = quarry.Position.X > actor.Position.X;
        Fix64 sign = rightward ? Fix64.One : -Fix64.One;

        // Four waypoints a couple of metres apart, which is roughly what a hand lays.
        for (int step = 1; step <= 4; step++)
        {
            route.Add(new Vec2(
                actor.Position.X + (sign * Fix64.FromInt(step * 2)),
                actor.Position.Y));
        }

        // Aim from where the ghost will finish, not from where the mole is standing now.
        Vec2 from = route[route.Count - 1];
        double range = (double)Fix64.Abs(quarry.Position.X - from.X).ToDecimal();
        double gravity = (double)MatchSettings.Gravity.ToDecimal();
        double full = (double)WeaponTable.Of(weapon).LaunchSpeed.ToDecimal();
        double wanted = System.Math.Sqrt(System.Math.Max(range, 1) * gravity);
        double power = full <= 0 ? 1 : System.Math.Min(1, System.Math.Max(0.15, wanted / full));

        // A forty-five degree drag, its length carrying the power.
        double drag = power * FullPowerDrag;
        Fix64 reach = Fix64.Ratio((int)(drag * Diagonal * 256), 256);

        return new Intent(route, new Vec2(from.X + (sign * reach), from.Y - reach));
    }

    /// <summary>Must match the drag distance the planning screen treats as full power.</summary>
    private const double FullPowerDrag = 20;

    /// <summary>Root two over two, so a diagonal drag has the length it looks like.</summary>
    private const double Diagonal = 0.7071067811865476;

    private Mole? Nearest(Mole actor)
    {
        Mole? best = null;
        Fix64 bestDistance = Fix64.MaxValue;

        foreach (Mole candidate in _match.Moles)
        {
            if (candidate.IsOffDuty || candidate.Seat == actor.Seat)
            {
                continue;
            }

            Fix64 distance = Vec2.Distance(candidate.Position, actor.Position);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }
}
