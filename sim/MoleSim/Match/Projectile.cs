using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>Something in the air that is going to go off.</summary>
    public sealed class Projectile
    {
        public Projectile(WeaponId weapon, int ownerSeat, int ownerMole, Vec2 position, Vec2 velocity)
        {
            Weapon = weapon;
            OwnerSeat = ownerSeat;
            OwnerMole = ownerMole;
            Position = position;
            Velocity = velocity;
            FuseRemaining = WeaponTable.Of(weapon).FuseTicks;
            BlastsRemaining = WeaponTable.Of(weapon).BounceBlasts;
        }

        public WeaponId Weapon { get; }

        /// <summary>
        /// Who fired it. Recorded for the aftermath tally and for naming the Mole of the
        /// Match, never for deciding damage: friendly fire is always on, and that includes
        /// the mole who pulled the trigger.
        /// </summary>
        public int OwnerSeat { get; }

        public int OwnerMole { get; }

        public Vec2 Position { get; set; }

        public Vec2 Velocity { get; set; }

        /// <summary>Ticks left on the fuse. Zero means it has no fuse of its own.</summary>
        public int FuseRemaining { get; set; }

        /// <summary>
        /// Blasts left in something that goes off as it bounces rather than once. Only the
        /// Gnome uses this, and it is what makes three tonnes of concrete rearrange a map.
        /// </summary>
        public int BlastsRemaining { get; set; }

        public bool HasDetonated { get; set; }

        /// <summary>Radius used for collision. Small: a clod, not a boulder.</summary>
        public static Fix64 Radius => Fix64.Ratio(2, WorldScale.CellsPerMetre);
    }

    /// <summary>Flies projectiles, and decides when they go off.</summary>
    public static class ProjectileMotion
    {
        /// <summary>How much of its speed a bouncing projectile keeps.</summary>
        private static Fix64 Restitution => Fix64.Ratio(45, 100);

        /// <summary>
        /// Advances one projectile by a tick. Returns true if it detonated, in which case
        /// the caller applies the blast.
        /// </summary>
        public static bool Step(Projectile shot, TerrainGrid terrain, Mole[] moles, Fix64 wind)
        {
            if (shot.HasDetonated)
            {
                return false;
            }

            WeaponSpec spec = WeaponTable.Of(shot.Weapon);

            Vec2 acceleration = Vec2.UnitY * MatchSettings.Gravity;

            if (spec.RidesTheWind)
            {
                acceleration += new Vec2(wind * MatchSettings.WindDragFactor, Fix64.Zero);
            }

            shot.Velocity = (shot.Velocity + (acceleration * MatchSettings.TickDuration))
                .WithMaxLength(MatchSettings.TerminalSpeed);

            if (!Travel(shot, terrain, moles, spec))
            {
                return true;
            }

            // A fuse burns whether or not the thing is still moving, which is what lets a
            // clod be cooked over somebody's head.
            if (spec.FuseTicks > 0)
            {
                shot.FuseRemaining--;

                if (shot.FuseRemaining <= 0)
                {
                    shot.HasDetonated = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Moves the projectile, dealing with anything it meets. Returns false if it went
        /// off on the way.
        /// </summary>
        private static bool Travel(Projectile shot, TerrainGrid terrain, Mole[] moles, WeaponSpec spec)
        {
            Vec2 travel = shot.Velocity * MatchSettings.TickDuration;
            Fix64 distance = travel.Length();

            if (distance == Fix64.Zero)
            {
                return true;
            }

            int substeps = Fix64.ToInt(distance / MatchSettings.MaxSubstepDistance) + 1;
            if (substeps < MatchSettings.MinimumSubsteps)
            {
                substeps = MatchSettings.MinimumSubsteps;
            }

            Vec2 direction = travel / distance;
            Fix64 stride = distance / Fix64.FromInt(substeps);

            for (int step = 0; step < substeps; step++)
            {
                Vec2 target = shot.Position + (direction * stride);

                if (HitsAMole(target, moles, shot))
                {
                    shot.Position = target;
                    shot.HasDetonated = true;
                    return false;
                }

                if (!TerrainQuery.IsBlocked(terrain, target, Projectile.Radius))
                {
                    shot.Position = target;
                    continue;
                }

                if (spec.DetonatesOnContact)
                {
                    shot.Position = target;
                    shot.HasDetonated = true;
                    return false;
                }

                if (shot.BlastsRemaining > 0)
                {
                    // Goes off where it landed and carries on bouncing, until it has
                    // nothing left to give.
                    shot.BlastsRemaining--;
                    shot.HasDetonated = shot.BlastsRemaining == 0;
                    Bounce(shot, terrain);
                    return false;
                }

                Bounce(shot, terrain);
                return true;
            }

            return true;
        }

        /// <summary>
        /// Whether the projectile has arrived at somebody. The mole that fired it is
        /// excluded only for the tick it is launched, so a shot cannot detonate inside its
        /// owner's own body on the way out, but can absolutely come back and find them.
        /// </summary>
        private static bool HitsAMole(Vec2 position, Mole[] moles, Projectile shot)
        {
            Fix64 reach = MatchSettings.Radius + Projectile.Radius;
            Fix64 reachSquared = reach * reach;

            foreach (Mole mole in moles)
            {
                if (mole.IsOffDuty)
                {
                    continue;
                }

                if (Vec2.DistanceSquared(mole.Position, position) <= reachSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Bounce(Projectile shot, TerrainGrid terrain)
        {
            Vec2 escape = TerrainQuery.EscapeDirection(terrain, shot.Position, Projectile.Radius);
            Fix64 into = Vec2.Dot(shot.Velocity, escape);
            Vec2 reflected = shot.Velocity - (escape * (into * Fix64.FromInt(2)));

            shot.Velocity = reflected * Restitution;
            shot.Position += escape * WorldScale.CellSize;
        }
    }
}
