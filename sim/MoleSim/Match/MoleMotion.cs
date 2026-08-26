using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// Moves one mole for one tick: either steering along its route, or falling.
    /// </summary>
    /// <remarks>
    /// The design's central movement rule is that surface and underground are one seamless
    /// move and only the price changes. That is why there is no dig branch here. The mole
    /// steers toward its next waypoint; if solid ground is in the way it first tries to
    /// step over it, and failing that it digs through, paying whatever that material costs
    /// per metre. Walking a slope and tunnelling a hillside are the same code path with
    /// different bills.
    ///
    /// Route following is suspended while airborne and resumes on landing, so a plan that
    /// walks into a crater somebody else just made falls in, climbs out and carries on,
    /// which is exactly what "intents replay through chaos" means.
    /// </remarks>
    public static class MoleMotion
    {
        /// <summary>Advances one mole by a single tick.</summary>
        public static void Step(Mole mole, TerrainGrid terrain, Vec2[]? route)
        {
            if (mole.IsOffDuty)
            {
                return;
            }

            if (mole.IsAirborne)
            {
                StepBallistic(mole, terrain);
                return;
            }

            // Standing on nothing is falling, however it came about.
            if (!TerrainQuery.IsBlocked(terrain, mole.Position, MatchSettings.Radius)
                && !TerrainQuery.IsSupported(terrain, mole.Position, MatchSettings.Radius))
            {
                if (!TerrainQuery.TrySnapDown(
                        terrain, mole.Position, MatchSettings.Radius, MatchSettings.GroundSnap, out Vec2 snapped))
                {
                    mole.IsAirborne = true;
                    StepBallistic(mole, terrain);
                    return;
                }

                mole.Position = snapped;
            }

            if (route is not null && mole.AcceptsInput)
            {
                StepAlongRoute(mole, terrain, route);
            }
        }

        // ---- Route following ----------------------------------------------------------

        private static void StepAlongRoute(Mole mole, TerrainGrid terrain, Vec2[] route)
        {
            Fix64 remaining = MatchSettings.WalkSpeed * MatchSettings.TickDuration;
            int startedOnWaypoint = mole.WaypointIndex;
            Fix64 distanceAtStart = DistanceToWaypoint(mole, route);

            while (remaining > Fix64.Zero
                   && mole.Stamina > Fix64.Zero
                   && mole.WaypointIndex < route.Length)
            {
                Vec2 target = route[mole.WaypointIndex];
                Vec2 toTarget = target - mole.Position;
                Fix64 distance = toTarget.Length();

                if (distance <= ArrivalRadius)
                {
                    mole.WaypointIndex++;
                    continue;
                }

                Fix64 stride = Fix64.Min(
                    Fix64.Min(remaining, MatchSettings.MaxSubstepDistance),
                    distance);

                if (!TryAdvance(mole, terrain, toTarget / distance, stride))
                {
                    // Something immovable is in the way. Give up on this waypoint rather
                    // than spending the rest of the round grinding against bedrock.
                    mole.WaypointIndex++;
                    continue;
                }

                remaining -= stride;
            }

            NoteProgress(mole, route, startedOnWaypoint, distanceAtStart);
        }

        /// <summary>
        /// Gives up on a waypoint the mole is not actually getting any closer to.
        /// </summary>
        /// <remarks>
        /// Arrival alone is not enough. A waypoint a metre above the ground can never be
        /// reached by something that walks, and without this the mole spends its entire
        /// round shuffling underneath it burning stamina. Real routes come from a ghost
        /// that walks the surface, so they should always be reachable; this is here so
        /// that a route which is not cannot cost somebody their whole turn.
        /// </remarks>
        private static void NoteProgress(Mole mole, Vec2[] route, int startedOnWaypoint, Fix64 distanceAtStart)
        {
            if (mole.WaypointIndex != startedOnWaypoint)
            {
                mole.StalledTicks = 0;
                return;
            }

            if (mole.WaypointIndex >= route.Length)
            {
                return;
            }

            Fix64 gained = distanceAtStart - DistanceToWaypoint(mole, route);

            if (gained > MinimumProgressPerTick)
            {
                mole.StalledTicks = 0;
                return;
            }

            mole.StalledTicks++;

            if (mole.StalledTicks >= StalledTicksBeforeGivingUp)
            {
                mole.WaypointIndex++;
                mole.StalledTicks = 0;
            }
        }

        private static Fix64 DistanceToWaypoint(Mole mole, Vec2[] route) =>
            mole.WaypointIndex < route.Length
                ? Vec2.Distance(mole.Position, route[mole.WaypointIndex])
                : Fix64.Zero;

        /// <summary>
        /// How close counts as having reached a waypoint. A body length, because a mole
        /// touching the point it was sent to has plainly got there.
        /// </summary>
        private static Fix64 ArrivalRadius => MatchSettings.Radius;

        /// <summary>Ground gained in a tick below which the mole counts as stalled.</summary>
        private static Fix64 MinimumProgressPerTick => Fix64.Ratio(1, WorldScale.CellsPerMetre);

        /// <summary>
        /// How steeply downward a route must point before the mole reads it as an
        /// instruction to dig rather than an obstacle to climb. About twenty degrees below
        /// horizontal, which leaves walking down slopes alone.
        /// </summary>
        private static Fix64 DiggingDownThreshold => Fix64.Ratio(35, 100);

        /// <summary>
        /// Stalled ticks tolerated before moving on. A few, so that briefly grinding up
        /// against a step or squeezing through a gap is not mistaken for being stuck.
        /// </summary>
        private const int StalledTicksBeforeGivingUp = 5;

        /// <summary>
        /// Moves one substep along a direction, dealing with whatever is in the way and
        /// charging for it. Returns false when the mole could not move at all.
        /// </summary>
        private static bool TryAdvance(Mole mole, TerrainGrid terrain, Vec2 direction, Fix64 stride)
        {
            Vec2 target = mole.Position + (direction * stride);

            // Charge for what is at the leading edge, not what is under the body. A
            // tunnelling mole stands in the hole it just made, so its centre always reads
            // as open air: sampling there would let anybody dig the length of the map at
            // walking prices, and would also make the sim decide dirt was not diggable
            // because the cell it asked about was already gone.
            Material ahead = TerrainQuery.MaterialAt(
                terrain, target + (direction * MatchSettings.Radius));
            Fix64 cost = MaterialTable.CostPerMetre(ahead) * stride;

            if (cost > mole.Stamina)
            {
                // Out of puff. Everything else in the plan still happens, from wherever
                // the mole has ended up.
                mole.Stamina = Fix64.Zero;
                return false;
            }

            if (TerrainQuery.IsBlocked(terrain, target, MatchSettings.Radius))
            {
                // Step-up exists so that walking a slope does not trench along it. It must
                // not apply when the route points downward, or a mole told to dig finds
                // clear air above the ground it was aiming at, steps into it, and burrows
                // precisely nowhere. Every attempt to tunnel down became a step up until
                // this test was here.
                bool digging = direction.Y > DiggingDownThreshold;

                if (!digging && TerrainQuery.TryStepUp(
                        terrain, target, MatchSettings.Radius, MatchSettings.StepHeight, out Vec2 stepped))
                {
                    // Walking up a slope. Costs the going rate for open air, and leaves
                    // the hillside intact.
                    mole.Position = stepped;
                    mole.Stamina -= MaterialTable.CostPerMetre(Material.Air) * stride;
                    return true;
                }

                TerrainQuery.CarveBody(terrain, target, MatchSettings.Radius);

                if (TerrainQuery.IsBlocked(terrain, target, MatchSettings.Radius))
                {
                    // Carving did not clear it, so whatever is in the way is bedrock.
                    // Nothing is charged, because nothing happened.
                    return false;
                }
            }

            mole.Position = target;
            mole.Stamina -= cost;

            FollowGroundOrFall(mole, terrain);
            return true;
        }

        /// <summary>
        /// Keeps a walking mole in contact with the ground over gentle terrain, and lets
        /// go when there is nothing left to walk on.
        /// </summary>
        private static void FollowGroundOrFall(Mole mole, TerrainGrid terrain)
        {
            if (TerrainQuery.IsBlocked(terrain, mole.Position, MatchSettings.Radius))
            {
                // Inside dirt of its own making: a tunnel holds a mole up perfectly well.
                return;
            }

            if (TerrainQuery.IsSupported(terrain, mole.Position, MatchSettings.Radius))
            {
                return;
            }

            if (TerrainQuery.TrySnapDown(
                    terrain, mole.Position, MatchSettings.Radius, MatchSettings.GroundSnap, out Vec2 snapped))
            {
                mole.Position = snapped;
                return;
            }

            mole.IsAirborne = true;
        }

        // ---- Falling ------------------------------------------------------------------

        private static void StepBallistic(Mole mole, TerrainGrid terrain)
        {
            Vec2 velocity = mole.Velocity
                + (Vec2.UnitY * (MatchSettings.Gravity * MatchSettings.TickDuration));
            velocity = velocity.WithMaxLength(MatchSettings.TerminalSpeed);

            Vec2 travel = velocity * MatchSettings.TickDuration;
            Fix64 distance = travel.Length();

            if (distance == Fix64.Zero)
            {
                mole.Velocity = velocity;
                return;
            }

            // Enough substeps that a fast mole cannot tunnel through a thin floor.
            int substeps = Fix64.ToInt(distance / MatchSettings.MaxSubstepDistance) + 1;
            if (substeps < MatchSettings.MinimumSubsteps)
            {
                substeps = MatchSettings.MinimumSubsteps;
            }

            Vec2 direction = travel / distance;
            Fix64 stride = distance / Fix64.FromInt(substeps);

            for (int step = 0; step < substeps; step++)
            {
                Vec2 target = mole.Position + (direction * stride);

                if (!TerrainQuery.IsBlocked(terrain, target, MatchSettings.Radius))
                {
                    mole.Position = target;
                    continue;
                }

                velocity = Collide(mole, terrain, velocity);

                if (!mole.IsAirborne)
                {
                    mole.Velocity = Vec2.Zero;
                    return;
                }

                break;
            }

            mole.Velocity = velocity;
        }

        /// <summary>
        /// Handles arriving at something solid: settle onto it if slow, bounce off it if
        /// not. Returns the velocity to carry on with.
        /// </summary>
        private static Vec2 Collide(Mole mole, TerrainGrid terrain, Vec2 velocity)
        {
            Vec2 escape = TerrainQuery.EscapeDirection(terrain, mole.Position, MatchSettings.Radius);

            if (velocity.Length() <= MatchSettings.SettleSpeed)
            {
                mole.IsAirborne = false;

                // Sit the mole neatly on the surface rather than a hair inside it.
                if (TerrainQuery.TrySnapDown(
                        terrain, mole.Position, MatchSettings.Radius, MatchSettings.GroundSnap, out Vec2 snapped))
                {
                    mole.Position = snapped;
                }

                return Vec2.Zero;
            }

            // Reflect about the escape direction and lose most of the energy, so moles
            // tumble to a stop rather than pinballing forever.
            Fix64 into = Vec2.Dot(velocity, escape);
            Vec2 reflected = velocity - (escape * (into * Fix64.FromInt(2)));

            mole.Position += escape * WorldScale.CellSize;

            return reflected * MatchSettings.Restitution;
        }
    }
}
