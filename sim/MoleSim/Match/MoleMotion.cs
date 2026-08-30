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

            // A drill in progress owns the mole: it is not walking, not falling, and not steerable
            // while it is cutting. Ahead of the airborne branch on purpose, because a torpedo fired
            // in mid air keeps drilling rather than reverting to a ballistic arc.
            if (mole.IsDrilling)
            {
                StepDrill(mole, terrain);
                return;
            }

            if (mole.IsAirborne)
            {
                // The route goes in now. A mole off the ground used to be handed nothing, so a jump
                // was a ballistic arc with no steering in it and no way to do anything with what it
                // hit.
                StepBallistic(mole, terrain, route);
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
                    StepBallistic(mole, terrain, route);
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

            if (mole.IsSnared)
            {
                remaining = remaining / Fix64.FromInt(2);
            }

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

            // Power Claws turn the mole into earthmoving equipment for a turn: dirt at
            // open-ground prices.
            Material charged = mole.DiggingIsCheap ? Material.Air : ahead;
            Fix64 cost = MaterialTable.CostPerMetre(charged) * stride;

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

                if (mole.IsSnared)
                {
                    // A Root Snare stops digging outright, so a snared mole is stuck with
                    // whatever open ground it can reach.
                    return false;
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
            mole.Facing = direction;

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

        /// <summary>
        /// Advances a Tunnel Torpedo by one tick, cutting as it goes.
        /// </summary>
        /// <remarks>
        /// The whole twelve metres used to be cut inside the tick the torpedo was ordered on, so the
        /// tunnel appeared complete and the mole was instantly at the far end of it. Nothing about
        /// that was watchable, and in the planning preview it did not happen at all, because the
        /// preview steps moles through this function and the drilling lived in the match.
        ///
        /// Substepped for the same reason the ballistic path is: at fourteen metres a second a tick
        /// covers most of a body length, and cutting in body-length jumps would let a torpedo pass
        /// straight through a thin slab of bedrock without noticing it.
        /// </remarks>
        private static void StepDrill(Mole mole, TerrainGrid terrain)
        {
            Fix64 travel = MatchSettings.TorpedoSpeed * MatchSettings.TickDuration;

            if (travel > mole.DrillLeft)
            {
                travel = mole.DrillLeft;
            }

            // Carried on the mole rather than left at whatever it was before, because a mole
            // crossing twelve metres with a velocity of zero is a lie that other things read. The
            // camera decides whether to follow from it, the replay records it, and the pose picker
            // asks it whether the mole is moving: all three were being told the mole stood still.
            // Nothing integrates it while drilling, so it is a report rather than a force.
            mole.Velocity = mole.DrillHeading * MatchSettings.TorpedoSpeed;

            Fix64 stride = WorldScale.CellSize * Fix64.FromInt(2);
            Fix64 cut = Fix64.Zero;

            while (cut < travel)
            {
                Fix64 step = travel - cut;

                if (step > stride)
                {
                    step = stride;
                }

                Vec2 next = mole.Position + (mole.DrillHeading * step);

                // Carve first, then ask whether anything is still in the way. Asking what material
                // is at the body's centre would stop the drill before it started, because a mole on
                // the surface has open air at its centre: the same mistake the walking solver made.
                TerrainQuery.CarveBody(terrain, next, MatchSettings.Radius);

                if (TerrainQuery.IsBlocked(terrain, next, MatchSettings.Radius))
                {
                    // Bedrock, which is the one thing that stops a torpedo. The drill ends here
                    // rather than at its full range, and the mole keeps the ground it won.
                    mole.DrillLeft = Fix64.Zero;
                    return;
                }

                mole.Position = next;
                cut += step;
            }

            mole.DrillLeft -= cut;

            // Left where it stopped rather than dropped. Whether the far end of the tunnel has a
            // floor is the grounded solver's question, and it gets asked on the next tick once the
            // drill is spent.
            if (mole.DrillLeft <= Fix64.Zero)
            {
                mole.DrillLeft = Fix64.Zero;
                mole.Velocity = Vec2.Zero;
            }
        }

        private static void StepBallistic(Mole mole, TerrainGrid terrain, Vec2[]? route)
        {
            Vec2 velocity = mole.Velocity
                + (Vec2.UnitY * (MatchSettings.Gravity * MatchSettings.TickDuration));

            velocity = Steered(mole, velocity, route);
            velocity = velocity.WithMaxLength(MatchSettings.TerminalSpeed);

            // A mole in the air points where it is going. This is what makes a shot fired
            // mid-flight leave at an angle its owner never chose.
            if (velocity.LengthSquared() > Fix64.Zero)
            {
                mole.Facing = velocity.Normalised();
            }

            // Stopped going up, and something underneath or walls either side. This is what catches
            // a mole at the top of a shaft it has just punched through a ceiling: while airborne
            // nothing consults the terrain about being held up, so without this it sailed to the
            // apex and then fell back down its own hole, which measured as a jump-dig that achieved
            // nothing. Asked only once the rise is spent, so it cannot cut a jump short.
            if (velocity.Y >= Fix64.Zero
                && TerrainQuery.IsSupported(terrain, mole.Position, MatchSettings.Radius))
            {
                mole.IsAirborne = false;
                mole.Velocity = Vec2.Zero;

                // Sat neatly on it, the same way Collide settles a landing. Support reaches two
                // cells further than a body does, so without this a mole came to rest a fifth of
                // its own height above the ground and hung there: the grounded solver agrees it is
                // supported and so never snaps it down either. A brace in a shaft has nothing to
                // snap to and keeps the position it stopped at.
                if (TerrainQuery.TrySnapDown(
                        terrain, mole.Position, MatchSettings.Radius, MatchSettings.GroundSnap,
                        out Vec2 settled))
                {
                    mole.Position = settled;
                }

                return;
            }

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

                // Something in the way, in mid-air. A ceiling or a wall the mole is being pushed
                // into is dug rather than bounced off, which is the whole of "surface and
                // underground are one seamless move" applied to the one case that never obeyed it.
                if (TryDigIntoIt(mole, terrain, target, route, stride, velocity))
                {
                    if (!mole.IsAirborne)
                    {
                        return;
                    }

                    // Still going up, and now inside the dirt it just opened. Carry on through:
                    // the rest of the jump is spent tunnelling rather than thrown away on impact.
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
        /// Steers a body that is off the ground, sideways only.
        /// </summary>
        /// <remarks>
        /// Sideways only on purpose: gravity owns the vertical while a mole is in the air, and a
        /// push that could fight it would be flight rather than a jump.
        ///
        /// The cap is walking pace, and it is a cap on what this adds rather than a cap on the
        /// mole's speed. A mole thrown sideways by a blast is already going faster than it can walk,
        /// and air control must not quietly become an air brake that bleeds off somebody else's
        /// knockback.
        /// </remarks>
        private static Vec2 Steered(Mole mole, Vec2 velocity, Vec2[]? route)
        {
            if (route is null || !mole.AcceptsInput || mole.WaypointIndex >= route.Length)
            {
                return velocity;
            }

            Fix64 wanted = route[mole.WaypointIndex].X - mole.Position.X;

            if (wanted == Fix64.Zero)
            {
                return velocity;
            }

            Fix64 step = MatchSettings.AirControl * MatchSettings.TickDuration;
            Fix64 across = wanted > Fix64.Zero ? velocity.X + step : velocity.X - step;

            // Whichever is the larger allowance: walking pace, or whatever it was already doing.
            Fix64 already = velocity.X > Fix64.Zero ? velocity.X : -velocity.X;
            Fix64 ceiling = already > MatchSettings.WalkSpeed ? already : MatchSettings.WalkSpeed;

            if (across > ceiling)
            {
                across = ceiling;
            }
            else if (across < -ceiling)
            {
                across = -ceiling;
            }

            return new Vec2(across, velocity.Y);
        }

        /// <summary>
        /// Turns a mid-air collision into a dig, when the thing hit is not a floor and somebody is
        /// pushing into it. Returns whether it did.
        /// </summary>
        /// <remarks>
        /// Jump at a ceiling and start tunnelling up it; jump at a wall and go through. The two
        /// conditions are what stop it being a nuisance. Only when the player is actually pushing,
        /// because a mole that merely falls against a wall should slide down it rather than
        /// excavate; and never on a floor, or every landing would punch a hole in the ground the
        /// mole was trying to land on.
        ///
        /// Which of the two a contact is comes off the escape direction, since solid below pushes a
        /// body upward. That is measured rather than assumed from the velocity, because a mole
        /// tumbling sideways into a slope is moving sideways while standing on something.
        ///
        /// The mole stops being airborne when it works, which is the point: it hands over to the
        /// walking solver, in the dirt, and everything about tunnelling from there is the code that
        /// already does it.
        /// </remarks>
        private static bool TryDigIntoIt(
            Mole mole, TerrainGrid terrain, Vec2 target, Vec2[]? route, Fix64 stride, Vec2 velocity)
        {
            if (!mole.AcceptsInput || mole.IsSnared || mole.Stamina <= Fix64.Zero)
            {
                return false;
            }

            // The jump is the input, on the way up. This used to require a direction to be held as
            // well, which read as broken on a touchscreen for the plainest reason: the stick springs
            // back to the middle the moment the thumb leaves it, so tapping hop and watching the
            // mole go had no direction held at all, and a jump into a ceiling stopped dead. Measured
            // at minus one cell for the whole rise, against thirty-eight cells with a direction held.
            //
            // Falling still needs one. Digging into whatever a mole happens to be dropping onto is
            // the case this rule has to stay away from, and the floor test below is not enough on
            // its own: a mole falling against a wall would otherwise burrow sideways into it without
            // anybody asking.
            bool rising = velocity.Y < Fix64.Zero;

            if (route is null && !rising)
            {
                return false;
            }

            Vec2 escape = TerrainQuery.EscapeDirection(terrain, target, MatchSettings.Radius);

            if (escape.Y <= -MatchSettings.FloorContact)
            {
                return false;
            }

            // Against the surface rather than under the middle. A body is blocked as soon as
            // anything solid comes within its radius, so at the moment of contact its centre cell
            // is still open air, and asking what is there answers "air", which is not diggable.
            // Measured before this was fixed: jumping into a ceiling while holding up rose nothing
            // at all, because the dig was declined on every contact.
            //
            // The escape direction points out of the solid, so a radius back along it is the thing
            // actually being hit. Same reasoning as the leading-edge sample in TryAdvance, arrived
            // at from the normal instead of from the heading, because a tumbling mole's heading and
            // the surface it lands against are not related.
            Material ahead = TerrainQuery.MaterialAt(
                terrain, target - (escape * MatchSettings.Radius));

            if (!MaterialTable.IsDiggable(ahead))
            {
                return false;
            }

            TerrainQuery.CarveBody(terrain, target, MatchSettings.Radius);

            if (TerrainQuery.IsBlocked(terrain, target, MatchSettings.Radius))
            {
                // Bedrock behind whatever was diggable. Nothing happened, so nothing is charged.
                return false;
            }

            // Still on the way up, so the jump keeps its momentum and the dig continues on it.
            // Hitting a ceiling used to stop a mole dead and hand it to the walking solver, which
            // meant a hop into a roof bought one body length of tunnel however hard it was going.
            // Now the rise is spent going through, which is what a mole with a run-up should get,
            // and it is charged by the substep exactly as walking through dirt is.
            Material charged = mole.DiggingIsCheap ? Material.Air : ahead;
            Fix64 paidFor = rising ? stride : MatchSettings.Radius;

            mole.Stamina -= MaterialTable.CostPerMetre(charged) * paidFor;

            if (mole.Stamina < Fix64.Zero)
            {
                mole.Stamina = Fix64.Zero;
            }

            if (rising && mole.Stamina > Fix64.Zero)
            {
                // Left airborne, and the caller moves it and carries on. Out of puff drops through
                // to the settle below, because a mole that cannot pay has stopped digging.
                return true;
            }

            mole.Position = target;
            mole.IsAirborne = false;
            mole.Velocity = Vec2.Zero;
            return true;
        }

        /// <summary>
        /// Handles arriving at something solid: settle onto it if slow, bounce off it if
        /// not. Returns the velocity to carry on with.
        /// </summary>
        private static Vec2 Collide(Mole mole, TerrainGrid terrain, Vec2 velocity)
        {
            Vec2 escape = TerrainQuery.EscapeDirection(terrain, mole.Position, MatchSettings.Radius);

            // How fast it is closing on the surface, rather than how fast it is going. These used
            // to be the same test and the difference did not matter until a mole could steer in the
            // air: air control adds up to walking pace sideways, which is more than the settle
            // speed on its own, so a mole falling onto flat ground while a direction was held never
            // slowed enough to land. Measured, it skittered along the surface permanently airborne.
            //
            // The component into the surface is the honest question anyway. A mole sliding along a
            // slope is on the ground however fast it is sliding.
            Fix64 closing = -Vec2.Dot(velocity, escape);

            if (closing <= MatchSettings.SettleSpeed)
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
