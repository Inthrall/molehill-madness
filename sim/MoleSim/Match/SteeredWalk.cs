using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>One mole's turn being walked out, a tick at a time, before it is committed.</summary>
    /// <remarks>
    /// The planning screen used to work by drawing the route as a line and looping a ghost along
    /// it. That put two moles on the screen, one of them translucent and walking a path the other
    /// was not on, and nobody who had not built it could tell which was which or what the line was
    /// for. Steering is the same information with one mole in it: push, and the mole walks.
    ///
    /// It runs the actual movement solver against a copy of the terrain, one tick per push, so the
    /// gauges are honest about what a route costs and about the digging. Not an estimate and not a
    /// straight-line approximation: the same code that will resolve the round, on a throwaway map.
    ///
    /// The waypoints it leaves behind are positions the mole genuinely stood at, which is a
    /// stronger guarantee than a hand-drawn line ever gave. <see cref="MoleMotion"/> has a
    /// stall-detector whose comment hopes that "real routes come from a ghost that walks the
    /// surface, so they should always be reachable"; steering is what makes that true by
    /// construction rather than by hope.
    /// </remarks>
    public sealed class SteeredWalk
    {
        /// <summary>
        /// How far a push aims ahead of the mole. Beyond arrival range plus a tick's stride, so
        /// the mole is always walking toward the waypoint rather than arriving at it, and the
        /// direction it travels is exactly the direction pushed.
        /// </summary>
        private static Fix64 PushReach => Fix64.FromInt(2);

        /// <summary>
        /// How far the mole walks between waypoints. Twice the arrival radius, so no two
        /// waypoints can be swallowed in the same tick and the route replays as it was walked.
        /// </summary>
        private static Fix64 WaypointSpacing => MatchSettings.Radius * Fix64.FromInt(2);

        private readonly TerrainGrid _scratch;
        private readonly Mole _ghost;
        private readonly Fix64 _startingStamina;
        private readonly Vec2 _start;
        private readonly List<Vec2> _path;
        private readonly List<Vec2> _waypoints = new List<Vec2>();

        /// <summary>
        /// The traps, snares and vents the ghost can walk into, as copies.
        /// </summary>
        /// <remarks>
        /// Copies because a snap trap goes off by marking itself spent, and the preview walks the
        /// ghost over the same hazards to work out what a turn would cost: handing it the real
        /// placements would let merely thinking about stepping on a trap disarm it for everybody.
        /// </remarks>
        private readonly List<Placement> _placements = new List<Placement>();

        private readonly int _round;

        /// <summary>
        /// The ghost's own shells, in the air over its own copy of the world.
        /// </summary>
        /// <remarks>
        /// Asked for from play: firing while planning did nothing at all until the round ran, so
        /// the one thing a player wants to know about a shot, which is where it goes, was the one
        /// thing they could not find out until it was too late to change it. The whole flight is
        /// run here, on the scratch terrain, and it craters that terrain and hurts this mole exactly
        /// as it will hurt it in the round. Blowing yourself up ends your turn, and now it ends it
        /// on the planning screen too, where there is still a reset token to spend on it.
        /// </remarks>
        private readonly List<Projectile> _shots = new List<Projectile>();

        /// <summary>Where those shells went off, for the planning screen to mark.</summary>
        private readonly List<Detonation> _blasts = new List<Detonation>();

        /// <summary>
        /// Everybody a ghost shell can hit, which is the ghost and nobody else.
        /// </summary>
        /// <remarks>
        /// The preview is the round with nobody else in it, and that rule is worth more here than
        /// anywhere. Every other mole on the map moves during the round, so a shell that stopped
        /// against one standing where it happens to be standing now would draw a blast at a place
        /// the round has no reason to put one, which is worse than drawing nothing. What the ghost
        /// does to itself is the half that is honest, and it is also the half that ends the turn.
        /// </remarks>
        private readonly Mole[] _targets;

        /// <summary>Which way the wind is blowing, since one weapon in the arsenal rides it.</summary>
        private readonly Fix64 _wind;

        private SteeredWalk(Mole ghost, TerrainGrid scratch, int round, Fix64 wind)
        {
            _ghost = ghost;
            _scratch = scratch;
            _round = round;
            _wind = wind;
            _targets = new[] { ghost };
            _startingStamina = ghost.Stamina;
            _start = ghost.Position;
            _path = new List<Vec2>(MatchSettings.TicksPerRound + 1) { ghost.Position };
        }

        /// <summary>
        /// Starts a turn for one mole, on a copy of the world so neither the mole nor the map is
        /// touched by anything the player tries out.
        /// </summary>
        /// <param name="placements">
        /// What is already lying about the map. Copied, and checked against the ghost every tick, so
        /// a turn walked over an armed trap or onto a vent shows what it will cost. Omit for a plain
        /// walk with no hazards, which is what most tests want.
        /// </param>
        /// <param name="round">
        /// Which round this turn is, since whether a placement is live depends on it. A trap arms a
        /// round after it is laid, so getting this wrong would either hide a live trap or invent one.
        /// </param>
        /// <param name="wind">
        /// What the wind is doing, so a previewed Beetle Launcher is pushed by it exactly as the
        /// real one will be. Omit for a still day, which is what most tests want.
        /// </param>
        public static SteeredWalk From(
            Mole mole, TerrainGrid terrain,
            IReadOnlyList<Placement>? placements = null, int round = 0, Fix64 wind = default)
        {
            // A stand-in carrying the real mole's state, so the walk accounts for a stamina
            // budget already shortened by the stalemate nudge, and for a snare or cheap digging.
            Mole ghost = new Mole(mole.Seat, mole.Index, mole.Position)
            {
                Stamina = mole.Stamina,
                IsAirborne = mole.IsAirborne,
                Velocity = mole.Velocity,
                Facing = mole.Facing,
                DiggingIsCheap = mole.DiggingIsCheap,
                IsSnared = mole.IsSnared,
            };

            SteeredWalk walk = new SteeredWalk(ghost, terrain.Clone(), round, wind);

            if (placements is not null)
            {
                foreach (Placement placement in placements)
                {
                    walk._placements.Add(placement.Copy());
                }
            }

            return walk;
        }

        /// <summary>
        /// Leaves a trap, snare or vent where the ghost is standing, as resolution will.
        /// </summary>
        /// <remarks>
        /// The vent is the one that matters this turn. It arms in the round it is planted, so a mole
        /// can plant one and be thrown into the air by it before the turn is over, and without this
        /// none of that showed while the turn was being planned. Traps arm a round later and snares
        /// only catch other people usefully, but they go in for nothing and the preview is then
        /// honest about all three rather than about one.
        /// </remarks>
        public void Plant(WeaponId weapon)
        {
            Placement? left = PlacementRules.Make(
                weapon, _ghost.Seat, _ghost.Position, _round, TicksUsed);

            if (left is not null)
            {
                _placements.Add(left);
            }
        }

        /// <summary>Where the mole has got to, which is where the rest of the plan happens from.</summary>
        public Vec2 Position => _ghost.Position;

        /// <summary>Which way it is pointing, so a shot can leave from somewhere sensible.</summary>
        public Vec2 Facing => _ghost.Facing;

        /// <summary>Where it stands at each tick walked. Kept for the markers, never drawn as a trail.</summary>
        public IReadOnlyList<Vec2> Path => _path;

        public Fix64 StaminaSpent => _startingStamina - _ghost.Stamina;

        /// <summary>How much of the eight seconds the walk has consumed.</summary>
        public int TicksUsed => _path.Count - 1;

        /// <summary>Whether the mole has walked further than it can afford.</summary>
        public bool RanOutOfPuff => _ghost.Stamina <= Fix64.Zero;

        /// <summary>Whether there is any of the round left to walk in.</summary>
        public bool HasTimeLeft => TicksUsed < MatchSettings.TicksPerRound;

        /// <summary>
        /// Whether the mole is in the air, and so still moving whether or not anybody is pushing.
        /// </summary>
        public bool IsFalling => _ghost.IsAirborne;

        /// <summary>Whether a torpedo is cutting, which like falling runs without being asked.</summary>
        public bool IsDrilling => _ghost.IsDrilling;

        /// <summary>
        /// What the ghost has left, which a turn walked into a trap will have spent some of.
        /// </summary>
        /// <remarks>
        /// Worth showing. Being hit ends a mole's turn, so a route over an armed trap does not merely
        /// cost pluck, it throws away the rest of the plan, and that is the sort of thing a player
        /// should find out while there is still time to walk round it.
        /// </remarks>
        public int Pluck => _ghost.Pluck;

        /// <summary>Whether the ghost has been caught by a snare.</summary>
        public bool IsSnared => _ghost.IsSnared;

        /// <summary>
        /// Whether the ghost has its claws out, so the planning screen can draw them.
        /// </summary>
        /// <remarks>
        /// The Power Claws are the one weapon whose entire effect is a number on a gauge, and the
        /// mole has a pose for wearing them precisely so that using them is visible. The pose was
        /// being read off the real mole, which does not get its claws until the round resolves, so
        /// on the planning screen the claws still changed nothing anybody could see: the gauge
        /// slowed down and the mole stood there unchanged.
        /// </remarks>
        public bool DiggingIsCheap => _ghost.DiggingIsCheap;

        /// <summary>
        /// Whether something live is acting on the ghost where it stands.
        /// </summary>
        /// <remarks>
        /// Like falling and like drilling, this is something happening to the mole rather than
        /// something it is choosing, so the ticks have to go by whether or not anybody is pushing.
        /// Standing still normally costs nothing and skips the tick entirely, which meant a mole
        /// stood on its own capped vent was never stepped and so was never thrown: the preview showed
        /// it standing calmly on something that would launch it the moment the round ran.
        /// </remarks>
        public bool IsHazarded
        {
            get
            {
                foreach (Placement placement in _placements)
                {
                    if (PlacementRules.IsLive(placement, _round)
                        && PlacementRules.Touches(placement, _ghost))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Whether there is anything under the ghost, so that letting go of the stick leaves it
        /// where it is rather than dropping it.
        /// </summary>
        /// <remarks>
        /// The same three questions <see cref="MoleMotion"/> asks at the top of a grounded step, in
        /// the same order, because the answer has to be the one the step would give. Standing in
        /// dirt counts: a tunnel holds a mole up perfectly well. So does ground within the snap
        /// distance, and that third question is the one that earns its place: without it a mole
        /// loitering a few centimetres above a slope would be stepped on every tick nobody was
        /// pushing, quietly eating the turn clock while the player thought.
        /// </remarks>
        private bool IsStandingOnSomething
        {
            get
            {
                if (TerrainQuery.IsBlocked(_scratch, _ghost.Position, MatchSettings.Radius)
                    || TerrainQuery.IsSupported(
                            _scratch, _ghost.Position, MatchSettings.Radius, _ghost.AcceptsInput))
                {
                    return true;
                }

                return TerrainQuery.TrySnapDown(
                    _scratch, _ghost.Position, MatchSettings.Radius, MatchSettings.GroundSnap,
                    out Vec2 _);
            }
        }

        /// <summary>Whether the mole has gone anywhere worth committing to.</summary>
        public bool HasMoved => Vec2.Distance(_start, _ghost.Position) > MatchSettings.Radius;

        /// <summary>
        /// Advances one tick. A zero direction is no input, which costs nothing on the ground and
        /// cannot stop a fall.
        /// </summary>
        /// <remarks>
        /// Standing still deliberately costs no time. The movement budget is eight seconds of
        /// walking, spent only while the stick is pushed, so a player who spends thirty seconds
        /// thinking has spent none of it. Falling is the exception: it is not something the mole
        /// chose and not something it can decline, so the ticks go by regardless.
        /// </remarks>
        public void Advance(Vec2 direction)
        {
            if (!HasTimeLeft)
            {
                return;
            }

            bool pushing = direction.LengthSquared() != Fix64.Zero;

            // Standing still costs nothing, unless something is going on that a tick has to pass
            // for. What counts as going on is one list, in one place, because two call sites read
            // it: this, and the client deciding whether to spend a frame's worth of ticks at all.
            if (!pushing && !SomethingIsHappening)
            {
                return;
            }

            // A drill cannot be steered. It is not something the mole is choosing tick by tick, so
            // it gets the tick and none of the direction: without this the preview stopped dead
            // between pushes and the tunnel appeared in instalments as the player fidgeted.
            if (_ghost.IsDrilling)
            {
                Step(null);
                return;
            }

            // The push goes through whether the mole is on the ground or off it, so the preview
            // steers a jump and digs into what it hits exactly as the round will. Handed nothing
            // when nobody is pushing, which is still a fall, still a shell in the air, and still a
            // vent going off underfoot.
            Step(pushing ? new[] { _ghost.Position + (direction.Normalised() * PushReach) } : null);
        }

        /// <summary>
        /// Whether something is happening to this turn that a tick has to pass for, whether or not
        /// anybody is pushing.
        /// </summary>
        /// <remarks>
        /// Falling, drilling, standing on something live, a shell in the air, and standing on
        /// nothing at all. One list rather than a condition at each call site, because there are two
        /// of them and they have to agree: the client decides whether to spend a frame's worth of
        /// ticks, and <see cref="Advance"/> decides whether a tick with nothing held does anything.
        /// A case added to one and not the other is a preview that freezes on exactly the case that
        /// was added, which is how a drill that surfaced came to leave the ghost hanging in the sky.
        ///
        /// The last of them is the one the airborne flag cannot answer. That flag is raised inside
        /// the step, and a drill that surfaces ends with it still down: the torpedo deliberately
        /// leaves the mole where it stopped and lets the next grounded step decide whether there is
        /// a floor under it. With nobody pushing there was no next step. The round steps every mole
        /// every tick and dropped it, which is the worst shape a preview bug takes: the plan
        /// disagreed with the round, and only the round was right.
        /// </remarks>
        public bool SomethingIsHappening =>
            _ghost.IsAirborne
            || _ghost.IsDrilling
            || HasAShotInTheAir
            || IsHazarded
            || !IsStandingOnSomething;

        /// <summary>
        /// Hops the ghost, exactly as resolution will hop the mole.
        /// </summary>
        /// <remarks>
        /// Without this, booking a hop did nothing anybody could see. The action went into the plan
        /// and a marker appeared where it was booked, but the walk carried on along the ground as
        /// though nothing had been asked for, so the mole only ever jumped once the round resolved
        /// and the key read as broken. It is the same impulse resolution applies, off the same
        /// setting, and refused in the air for the same reason, because a preview that disagrees
        /// with the round is worse than no preview.
        ///
        /// Returns whether the ghost actually left the ground, so a caller can decline to book an
        /// action the simulation would ignore.
        /// </remarks>
        public bool Hop()
        {
            if (_ghost.IsAirborne || _ghost.IsDrilling)
            {
                return false;
            }

            _ghost.AddImpulse(-Vec2.UnitY * MatchSettings.HopSpeed);
            return true;
        }

        /// <summary>
        /// Sets the ghost drilling, exactly as resolution will set the mole drilling.
        /// </summary>
        /// <remarks>
        /// Without this, ordering a Tunnel Torpedo did nothing whatsoever on the planning screen.
        /// The action went into the plan and the drilling happened at resolution, so the one weapon
        /// whose whole purpose is to move the mole a dozen metres showed no movement at all while the
        /// move was being planned, and the route the plan recorded was the route of a mole that had
        /// stood still. It is the same two fields resolution sets, off the same setting, and refused
        /// while already drilling for the same reason as a hop: a preview that disagrees with the
        /// round is worse than no preview.
        /// </remarks>
        /// <param name="power">
        /// The wind-up, which is how far it cuts. Taken rather than assumed full for the same reason
        /// the heading is taken: the plan carries one, and a preview that always drilled the longest
        /// tunnel would put the mole somewhere the round will not.
        /// </param>
        public bool Drill(Vec2 aim, byte power)
        {
            if (_ghost.IsDrilling || aim.LengthSquared() == Fix64.Zero)
            {
                return false;
            }

            Vec2 heading = aim.Normalised();

            _ghost.Facing = heading;
            _ghost.DrillHeading = heading;
            _ghost.DrillLeft = MatchSettings.TorpedoRangeFor(power);
            _ghost.IsAirborne = false;
            _ghost.Velocity = Vec2.Zero;
            return true;
        }

        /// <summary>
        /// Fires the ghost's weapon into the ghost's own world, exactly as resolution will.
        /// </summary>
        /// <remarks>
        /// The shell is launched here and not resolved here. It flies a tick at a time along with
        /// the walk, because the plan's own clock is what decides where the mole is standing when
        /// its own shell arrives: a mole that fires at its feet and runs is somewhere else by the
        /// time the thing goes off, and a mole that fires and stands still is not. Resolving the
        /// whole flight inside the firing tick would answer that question with wherever the mole
        /// was when the button went down, which is the one answer that is never right.
        ///
        /// Only the kinds that go somewhere. A swing cannot reach the mole swinging it, and the
        /// tools have their own previews a few lines up.
        ///
        /// Returns whether anything was launched, so a caller can tell a shot from a no-op.
        /// </remarks>
        public bool Fire(PlanAction use)
        {
            WeaponSpec spec = WeaponTable.Of(use.Weapon);
            Vec2 aim = use.AimDirection();

            // A mole in the air fires relative to its tumble, which is the rule the round uses and
            // the reason a preview has to ask rather than assume the aim is the heading.
            if (_ghost.IsAirborne)
            {
                aim = aim.RotatedBy(_ghost.Facing);
            }

            switch (spec.Kind)
            {
                case WeaponKind.Thrown:
                    return Launch(use, spec, aim);

                case WeaponKind.FromTheSky:
                    return CallItIn(use, spec, aim);

                case WeaponKind.Planted:
                    // Dropped where it stands, with its fuse already burning.
                    _shots.Add(new Projectile(
                        use.Weapon, _ghost.Seat, _ghost.Index, _ghost.Position, Vec2.Zero));
                    return true;

                case WeaponKind.Seismic:
                    Frack(spec);
                    return true;

                default:
                    return false;
            }
        }

        private bool Launch(PlanAction use, WeaponSpec spec, Vec2 aim)
        {
            if (aim.LengthSquared() == Fix64.Zero)
            {
                return false;
            }

            // Clear of the body, so a shell cannot go off inside the mole that fired it on the way
            // out. The same offset the round uses, because a shot that starts somewhere else lands
            // somewhere else.
            Vec2 muzzle = _ghost.Position
                + (aim * (MatchSettings.Radius + Projectile.Radius + WorldScale.CellSize));

            _shots.Add(new Projectile(
                use.Weapon, _ghost.Seat, _ghost.Index, muzzle,
                aim * (spec.LaunchSpeed * use.PowerFraction())));

            return true;
        }

        private bool CallItIn(PlanAction use, WeaponSpec spec, Vec2 aim)
        {
            if (aim.LengthSquared() == Fix64.Zero)
            {
                return false;
            }

            Vec2 target = _ghost.Position
                + (aim * (MatchSettings.SkyTargetRange * use.PowerFraction()));

            int count = spec.ClusterCount > 0 ? spec.ClusterCount : 1;

            for (int index = 0; index < count; index++)
            {
                Fix64 offset = MatchSettings.SkySpread
                    * Fix64.FromInt(index - ((count - 1) / 2));

                _shots.Add(new Projectile(
                    use.Weapon, _ghost.Seat, _ghost.Index,
                    new Vec2(target.X + offset, target.Y - MatchSettings.SkyDropHeight),
                    Vec2.Zero));
            }

            return true;
        }

        /// <summary>
        /// A shock through the soil at the ghost's own feet, which happens the moment it is asked
        /// for rather than flying anywhere.
        /// </summary>
        /// <remarks>
        /// Worth previewing precisely because it is the one weapon that reaches through dirt. A mole
        /// standing on its own bore takes the whole of it, and finding that out while the round runs
        /// is finding it out too late. The gusher throws whatever is over the bore straight up, and
        /// over this bore there is only ever the mole that drilled it.
        /// </remarks>
        private void Frack(WeaponSpec spec)
        {
            Vec2 bore = _ghost.Position;

            Blast.Detonate(
                _scratch, _targets, bore, spec, crater: false,
                bySeat: _ghost.Seat, byMoleIndex: _ghost.Index);

            TerrainQuery.CollapseCavities(_scratch, bore, spec.BlastRadius);

            _ghost.Velocity = Vec2.Zero;
            _ghost.AddImpulse(-Vec2.UnitY * MatchSettings.GusherSpeed);

            _blasts.Add(new Detonation(bore, spec.BlastRadius));
        }

        /// <summary>Whether any of the ghost's shells is still in the air.</summary>
        /// <remarks>
        /// Like a drill and like a vent, a shell in flight is something happening to the turn rather
        /// than something the player is choosing tick by tick, so the ticks have to go by for it to
        /// land at all. Without this, letting go of the stick froze a clod in mid-air.
        /// </remarks>
        public bool HasAShotInTheAir
        {
            get
            {
                foreach (Projectile shot in _shots)
                {
                    if (!shot.HasDetonated)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The ghost's shells, so the planning screen can draw them on their way.</summary>
        public IReadOnlyList<Projectile> Shots => _shots;

        /// <summary>Where they went off, so it can mark what the turn is about to do.</summary>
        public IReadOnlyList<Detonation> Blasts => _blasts;

        /// <summary>
        /// Whether the turn is over because the mole was hit, by its own shot or by anything else.
        /// </summary>
        /// <remarks>
        /// The design's one rule about damage is that taking any of it ends a mole's turn, and until
        /// the preview fired anything there was nothing in it that could end a turn early except a
        /// trap. Now the commonest way to lose a turn, which is standing too close to your own clod,
        /// is visible while there is still a reset token to spend on it.
        /// </remarks>
        public bool TurnIsOver => !_ghost.AcceptsInput;

        /// <summary>
        /// Advances the ghost's shells by one tick, and applies whatever goes off.
        /// </summary>
        /// <remarks>
        /// Iterated by index because a cluster charge adds to the list as it splits, which is the
        /// same reason the round iterates its own shots by index.
        /// </remarks>
        private void PumpShots()
        {
            for (int index = 0; index < _shots.Count; index++)
            {
                Projectile shot = _shots[index];

                if (shot.HasDetonated)
                {
                    continue;
                }

                if (!ProjectileMotion.Step(shot, _scratch, _targets, _wind))
                {
                    continue;
                }

                WeaponSpec spec = WeaponTable.Of(shot.Weapon);

                Blast.Detonate(
                    _scratch, _targets, shot.Position, spec,
                    bySeat: shot.OwnerSeat, byMoleIndex: shot.OwnerMole);

                _blasts.Add(new Detonation(shot.Position, spec.BlastRadius));
                SplitCluster(shot, spec);
            }
        }

        /// <summary>Splits a cluster charge as it goes off, the way the round splits it.</summary>
        private void SplitCluster(Projectile shot, WeaponSpec spec)
        {
            // Only the ones thrown from a mole split on the way down. Anything called in from the
            // sky arrived as a group already.
            if (spec.ClusterCount <= 0 || spec.Kind != WeaponKind.Thrown)
            {
                return;
            }

            for (int index = 0; index < spec.ClusterCount; index++)
            {
                Fix64 sideways = MatchSettings.ClusterSpread
                    * Fix64.FromInt(index - ((spec.ClusterCount - 1) / 2));

                _shots.Add(new Projectile(
                    WeaponTable.AcornShard, shot.OwnerSeat, shot.OwnerMole, shot.Position,
                    new Vec2(sideways, -MatchSettings.ClusterSpread)));
            }
        }

        /// <summary>
        /// Sharpens the ghost's claws, exactly as resolution sharpens the mole's.
        /// </summary>
        /// <remarks>
        /// The Power Claws only ever do one thing, which is make digging cheap, and the planning
        /// screen is the only place that number is ever shown. Without this the gauge quoted the full
        /// price for a turn the round would charge a quarter of: measured at 62.6 stamina previewed
        /// against 15.0 actually charged, for the same dig. Using the claws appeared to do nothing at
        /// all, so there was no way to learn what they were for.
        ///
        /// From this moment rather than for the whole turn, because that is what resolution does: the
        /// claws come out at the tick they are used, and digging done before that is charged in full.
        /// </remarks>
        public void Claw()
        {
            _ghost.DiggingIsCheap = true;
        }

        /// <summary>
        /// Drops a sandbag into the ghost's own copy of the world.
        /// </summary>
        /// <remarks>
        /// The preview walks a clone of the terrain, so a bag dropped this turn did not exist in it
        /// and a mole could not be planned onto something it was about to build. Standing on your own
        /// bag is most of the reason to carry one.
        /// </remarks>
        public void DropSandbag()
        {
            Tools.DropSandbag(_scratch, _ghost.Position);
        }

        /// <summary>Lays a girder into the ghost's own copy of the world, as resolution will.</summary>
        public void LayGirder(Vec2 aim)
        {
            Tools.LayGirder(_scratch, _ghost.Position, aim);
        }

        /// <summary>
        /// The waypoints to hand the simulation: where the mole stood, every so often, plus
        /// wherever it finished.
        /// </summary>
        /// <remarks>
        /// The tail matters. Without it a plan stops at the last full spacing and the mole comes
        /// up short of the spot its owner steered it to, which is the one thing about a route
        /// that has to be exact, because it is where the shot leaves from.
        /// </remarks>
        public List<Vec2> Waypoints()
        {
            List<Vec2> route = new List<Vec2>(_waypoints.Count + 1);
            route.AddRange(_waypoints);

            Vec2 last = route.Count > 0 ? route[route.Count - 1] : _start;

            if (Vec2.Distance(last, _ghost.Position) > MatchSettings.Radius)
            {
                route.Add(_ghost.Position);
            }

            return route;
        }

        /// <summary>Where the mole was at a given tick, for marking a hop or a planted charge.</summary>
        public Vec2 PositionAt(int tick)
        {
            if (tick <= 0)
            {
                return _start;
            }

            return tick < _path.Count ? _path[tick] : _ghost.Position;
        }

        private void Step(Vec2[]? route)
        {
            // One waypoint, replaced every tick, so the solver is always steering at the stick
            // rather than finishing an instruction the player has since changed their mind about.
            _ghost.WaypointIndex = 0;
            _ghost.StalledTicks = 0;

            MoleMotion.Step(_ghost, _scratch, route);
            TakeTheFall();

            // After the move, which is the order the round uses: everybody moves, and then the
            // shells advance onto wherever they have got to.
            PumpShots();
            CheckPlacements();
            _path.Add(_ghost.Position);

            Vec2 sinceLast = _waypoints.Count > 0 ? _waypoints[_waypoints.Count - 1] : _start;

            if (Vec2.Distance(sinceLast, _ghost.Position) >= WaypointSpacing)
            {
                _waypoints.Add(_ghost.Position);
            }
        }

        /// <summary>
        /// Charges the ghost for a hard landing, by the same rule the round uses.
        /// </summary>
        /// <remarks>
        /// Without this a route that walked off the top of a cave read as free while planning and
        /// cost the mole its turn and a fifth of its pluck when the round ran, which is the same
        /// class of lie the untraversed trap used to be. The preview is meant to be the round with
        /// nobody else in it.
        /// </remarks>
        private void TakeTheFall()
        {
            int damage = Falls.DamageFor(_ghost.LandedAt);

            if (damage > 0)
            {
                _ghost.TakeDamage(damage);
            }
        }

        /// <summary>
        /// Runs the hazards against the ghost, by the same rules the round uses.
        /// </summary>
        /// <remarks>
        /// After the move rather than before it, which is the order the round uses: a mole is caught
        /// by what it walked into during the tick, not by what it was standing on at the start of it.
        /// </remarks>
        private void CheckPlacements()
        {
            foreach (Placement placement in _placements)
            {
                if (!PlacementRules.IsLive(placement, _round)
                    || !PlacementRules.Touches(placement, _ghost))
                {
                    continue;
                }

                PlacementRules.Apply(placement, _ghost);
            }
        }
    }
}
