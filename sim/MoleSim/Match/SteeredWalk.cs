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

        private SteeredWalk(Mole ghost, TerrainGrid scratch)
        {
            _ghost = ghost;
            _scratch = scratch;
            _startingStamina = ghost.Stamina;
            _start = ghost.Position;
            _path = new List<Vec2>(MatchSettings.TicksPerRound + 1) { ghost.Position };
        }

        /// <summary>
        /// Starts a turn for one mole, on a copy of the world so neither the mole nor the map is
        /// touched by anything the player tries out.
        /// </summary>
        public static SteeredWalk From(Mole mole, TerrainGrid terrain)
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

            return new SteeredWalk(ghost, terrain.Clone());
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

            // A drill in progress runs whether or not anybody is pushing, and cannot be steered. It
            // is not something the mole is choosing tick by tick, so like falling the ticks go by
            // regardless: without this the preview stopped dead between pushes and the tunnel
            // appeared in instalments as the player fidgeted with the stick.
            if (_ghost.IsDrilling)
            {
                Step(null);
                return;
            }

            if (_ghost.IsAirborne)
            {
                // The push goes through while off the ground, so the preview steers a jump and digs
                // into what it hits exactly as the round will. Handed nothing when nobody is
                // pushing, which is still a fall.
                Step(pushing ? new[] { _ghost.Position + (direction.Normalised() * PushReach) } : null);
                return;
            }

            if (!pushing)
            {
                return;
            }

            Step(new[] { _ghost.Position + (direction.Normalised() * PushReach) });
        }

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
        /// whose whole purpose is to move the mole twelve metres showed no movement at all while the
        /// move was being planned, and the route the plan recorded was the route of a mole that had
        /// stood still. It is the same two fields resolution sets, off the same setting, and refused
        /// while already drilling for the same reason as a hop: a preview that disagrees with the
        /// round is worse than no preview.
        /// </remarks>
        public bool Drill(Vec2 aim)
        {
            if (_ghost.IsDrilling || aim.LengthSquared() == Fix64.Zero)
            {
                return false;
            }

            Vec2 heading = aim.Normalised();

            _ghost.Facing = heading;
            _ghost.DrillHeading = heading;
            _ghost.DrillLeft = MatchSettings.TorpedoRange;
            _ghost.IsAirborne = false;
            _ghost.Velocity = Vec2.Zero;
            return true;
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
            _path.Add(_ghost.Position);

            Vec2 sinceLast = _waypoints.Count > 0 ? _waypoints[_waypoints.Count - 1] : _start;

            if (Vec2.Distance(sinceLast, _ghost.Position) >= WaypointSpacing)
            {
                _waypoints.Add(_ghost.Position);
            }
        }
    }
}
