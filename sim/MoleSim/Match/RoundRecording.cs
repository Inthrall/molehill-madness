using System;
using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// Every tick of a resolved round, kept so it can be watched afterwards.
    /// </summary>
    /// <remarks>
    /// A round resolves instantly, in a few milliseconds, and then somebody has to watch
    /// it happen over eight seconds. Rather than interleaving the client with the
    /// simulation and hoping they stay in step, the simulation runs to completion and hands
    /// back a recording for the client to play at whatever rate the display wants.
    ///
    /// This is the same shape the clip renderer needs: re-simulate, then render frames
    /// from the result with no interface in shot. Building it here means instant replay and
    /// clip export are already most of the way done, which is a good reason to prefer it
    /// over a stepped API even before that work starts.
    ///
    /// Events are not duplicated. A frame records how far into the round's hit and knockout
    /// lists things had got by that tick, so the client can pop a damage number at the
    /// moment it actually happened without a second copy of anything.
    /// </remarks>
    public sealed class RoundRecording
    {
        private readonly Vec2[] _positions;
        private readonly Vec2[] _velocities;
        private readonly int[] _pluck;
        private readonly bool[] _offDuty;
        private readonly List<Vec2>[] _shots;
        private readonly int[] _hitsUpTo;
        private readonly int[] _knockoutsUpTo;
        private readonly int[] _changesUpTo;
        private readonly int[] _detonationsUpTo;

        internal RoundRecording(int round, int moleCount, int ticks)
        {
            Journal = new List<TerrainChange>();

            Round = round;
            MoleCount = moleCount;
            Ticks = ticks;

            _positions = new Vec2[moleCount * ticks];
            _velocities = new Vec2[moleCount * ticks];
            _pluck = new int[moleCount * ticks];
            _offDuty = new bool[moleCount * ticks];
            _shots = new List<Vec2>[ticks];
            _hitsUpTo = new int[ticks];
            _knockoutsUpTo = new int[ticks];
            _changesUpTo = new int[ticks];
            _detonationsUpTo = new int[ticks];
        }

        public int Round { get; }

        public int MoleCount { get; }

        public int Ticks { get; }

        /// <summary>How long the whole thing takes to watch, in seconds.</summary>
        public Fix64 Duration => Fix64.FromInt(Ticks) * MatchSettings.TickDuration;

        /// <summary>Where a mole was at a given tick. Slots match <see cref="MoleMatch.Moles"/>.</summary>
        public Vec2 PositionOf(int tick, int moleSlot) => _positions[Index(tick, moleSlot)];

        /// <summary>
        /// How a mole was moving. The client needs this to lean a walking mole and to spin
        /// a tumbling one, which is the same information the simulation uses to decide
        /// which way a shot fired mid-air leaves.
        /// </summary>
        public Vec2 VelocityOf(int tick, int moleSlot) => _velocities[Index(tick, moleSlot)];

        public int PluckOf(int tick, int moleSlot) => _pluck[Index(tick, moleSlot)];

        public bool IsOffDutyAt(int tick, int moleSlot) => _offDuty[Index(tick, moleSlot)];

        /// <summary>Everything in the air at a given tick.</summary>
        public IReadOnlyList<Vec2> ShotsAt(int tick) =>
            _shots[Clamp(tick)] ?? (IReadOnlyList<Vec2>)Array.Empty<Vec2>();

        /// <summary>
        /// How far into the round's hit list things had got by this tick, so a client can
        /// show a damage number exactly when it landed.
        /// </summary>
        public int HitsUpTo(int tick) => _hitsUpTo[Clamp(tick)];

        public int KnockoutsUpTo(int tick) => _knockoutsUpTo[Clamp(tick)];

        /// <summary>
        /// Every cell the round changed, in the order it changed, so a watcher can be shown
        /// a crater at the moment the shell that made it lands rather than eight seconds
        /// early. Replayed against a copy of the map taken before the round.
        /// </summary>
        public IReadOnlyList<TerrainChange> TerrainChanges => Journal;

        /// <summary>How far into <see cref="TerrainChanges"/> things had got by this tick.</summary>
        public int ChangesUpTo(int tick) => _changesUpTo[Clamp(tick)];

        /// <summary>
        /// How many things had gone off by this tick, so a client can make a noise at the
        /// moment one did.
        /// </summary>
        /// <remarks>
        /// The same shape as the hit and knockout counts, and for the same reason: the round is
        /// over before anybody watches it, so the only way to put a sound in the right place is
        /// to know which tick it belonged to. Comparing this tick's count with the last one is
        /// also how the client tells a crater apart from a mole digging, since both change the
        /// terrain and only one of them is an explosion.
        /// </remarks>
        public int DetonationsUpTo(int tick) => _detonationsUpTo[Clamp(tick)];

        /// <summary>The list the grid appends to while the round resolves.</summary>
        internal List<TerrainChange> Journal { get; }

        /// <summary>
        /// Interpolated position, for drawing at a higher rate than the simulation runs.
        /// </summary>
        /// <remarks>
        /// The simulation is 30 Hz and displays are not, so the client draws between ticks.
        /// This is presentation only: nothing here ever feeds back into the simulation, and
        /// a mole that has just gone off duty stops being interpolated so it does not slide
        /// away from its own pratfall.
        /// </remarks>
        public Vec2 PositionAt(Fix64 seconds, int moleSlot)
        {
            Fix64 exact = seconds / MatchSettings.TickDuration;
            int tick = Fix64.FloorToInt(exact);

            if (tick < 0)
            {
                return PositionOf(0, moleSlot);
            }

            if (tick >= Ticks - 1)
            {
                return PositionOf(Ticks - 1, moleSlot);
            }

            if (IsOffDutyAt(tick + 1, moleSlot) && !IsOffDutyAt(tick, moleSlot))
            {
                return PositionOf(tick, moleSlot);
            }

            Fix64 blend = exact - Fix64.FromInt(tick);

            return Vec2.Lerp(PositionOf(tick, moleSlot), PositionOf(tick + 1, moleSlot), blend);
        }

        internal void Capture(
            int tick, IReadOnlyList<Mole> moles, IReadOnlyList<Projectile> shots,
            int hits, int knockouts, int detonations)
        {
            for (int slot = 0; slot < moles.Count; slot++)
            {
                int at = Index(tick, slot);
                _positions[at] = moles[slot].Position;
                _velocities[at] = moles[slot].Velocity;
                _pluck[at] = moles[slot].Pluck;
                _offDuty[at] = moles[slot].IsOffDuty;
            }

            if (shots.Count > 0)
            {
                List<Vec2> flying = new List<Vec2>(shots.Count);

                foreach (Projectile shot in shots)
                {
                    if (!shot.HasDetonated)
                    {
                        flying.Add(shot.Position);
                    }
                }

                _shots[tick] = flying;
            }

            _hitsUpTo[tick] = hits;
            _knockoutsUpTo[tick] = knockouts;
            _changesUpTo[tick] = Journal.Count;
            _detonationsUpTo[tick] = detonations;
        }

        private int Index(int tick, int moleSlot) => (Clamp(tick) * MoleCount) + moleSlot;

        private int Clamp(int tick) => tick < 0 ? 0 : tick >= Ticks ? Ticks - 1 : tick;
    }
}
