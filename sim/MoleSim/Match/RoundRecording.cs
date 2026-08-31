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
    /// <summary>
    /// Something in the air, and what fired it.
    /// </summary>
    /// <remarks>
    /// The weapon as well as the position, because a clod, an indignant beetle, three acorns, a
    /// bunch of beetroots wired to a sparkler and a drill going through a hillside are five
    /// different pictures and used to be one circle. Taken off the projectile, so nothing here
    /// decides anything.
    /// </remarks>
    public readonly struct Shot
    {
        public Shot(Vec2 position, WeaponId weapon)
        {
            Position = position;
            Weapon = weapon;
        }

        public Vec2 Position { get; }

        public WeaponId Weapon { get; }
    }

    public sealed class RoundRecording
    {
        private readonly Vec2[] _positions;
        private readonly Vec2[] _velocities;
        private readonly int[] _pluck;
        private readonly Fix64[] _landed;
        private readonly bool[] _offDuty;
        private readonly List<Shot>[] _shots;
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
            _landed = new Fix64[moleCount * ticks];
            _offDuty = new bool[moleCount * ticks];
            _shots = new List<Shot>[ticks];
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

        /// <summary>
        /// How hard a mole hit the ground on this tick, or zero if it did not.
        /// </summary>
        /// <remarks>
        /// Recorded so the client can throw dust out from under a mole that landed badly, and know
        /// how much to throw. Falling damage was previously the only damage in the game with nothing
        /// on the screen to explain it: the number rose over the mole and there was no picture of it
        /// happening, so it read as the game taking pluck away for no reason.
        ///
        /// The speed rather than the damage, because a landing is a thing that happens whether or
        /// not it costs anything, and how much it cost is <see cref="Falls.DamageFor"/>'s answer to
        /// give. The client asks it the same question the round did.
        /// </remarks>
        public Fix64 LandedAt(int tick, int moleSlot) => _landed[Index(tick, moleSlot)];

        public bool IsOffDutyAt(int tick, int moleSlot) => _offDuty[Index(tick, moleSlot)];

        /// <summary>Everything in the air at a given tick.</summary>
        public IReadOnlyList<Shot> ShotsAt(int tick) =>
            _shots[Clamp(tick)] ?? (IReadOnlyList<Shot>)Array.Empty<Shot>();

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
        /// The last tick anything at all happened, so a watcher need not sit through the rest.
        /// </summary>
        /// <remarks>
        /// A round is always two hundred and forty ticks whatever is in it, because everybody plans
        /// against the same eight seconds and a shorter round for a quiet turn would give the
        /// simultaneous clock away. Watching one is a different question: most rounds have every
        /// mole stood still and every shell landed within four or five seconds, and the rest is a
        /// still picture with a countdown behind it.
        ///
        /// So this says where the round stopped being worth watching, and the client decides how
        /// long a tail to leave on it. Presentation stays in the client and the fact stays here,
        /// which is the same split every other field of this class is on.
        ///
        /// Everything is read from what was recorded rather than from a flag anybody had to
        /// remember to set: a mole that moved, a shot in the air, a hit, a knockout, a bang, a cell
        /// that changed. The one exception is <see cref="Stirred"/>, for the things that happen to a
        /// crate, which leave no mark anywhere in here at all.
        ///
        /// Worked out once and kept, because the client asks every frame of the replay and the
        /// answer is a scan of every tick of every mole. Safe to cache because a recording is
        /// finished before anybody can read it: <see cref="Stirred"/> is called by the round while
        /// it resolves and by nothing afterwards.
        /// </remarks>
        public int SettledTick => _settled >= 0 ? _settled : (_settled = FindSettled());

        private int _settled = -1;

        private int FindSettled()
        {
            for (int tick = Ticks - 1; tick > _stirred; tick--)
            {
                if (Happened(tick))
                {
                    return tick;
                }
            }

            return _stirred;
        }

        /// <summary>
        /// Notes a tick that mattered but leaves no other trace in the recording.
        /// </summary>
        /// <remarks>
        /// Crates only. One landing changes no cell, moves no mole and hurts nobody, so nothing in
        /// the arrays below can see it, and a round whose only late event was a crate coming down
        /// would be cut off before it arrived.
        /// </remarks>
        internal void Stirred(int tick)
        {
            if (tick > _stirred)
            {
                _stirred = tick < Ticks ? tick : Ticks - 1;
            }
        }

        private int _stirred;

        /// <summary>Whether anything about this tick differs from the one before it.</summary>
        private bool Happened(int tick)
        {
            int before = tick - 1;

            if (_hitsUpTo[tick] != _hitsUpTo[before]
                || _knockoutsUpTo[tick] != _knockoutsUpTo[before]
                || _detonationsUpTo[tick] != _detonationsUpTo[before]
                || _changesUpTo[tick] != _changesUpTo[before]
                || ShotsAt(tick).Count > 0)
            {
                return true;
            }

            for (int slot = 0; slot < MoleCount; slot++)
            {
                // Position rather than velocity, because a mole walking on the ground is moved by
                // the solver without a velocity ever being written, so a velocity test would call
                // the whole of a walking turn quiet.
                if (Vec2.DistanceSquared(_positions[Index(tick, slot)], _positions[Index(before, slot)])
                    > StirringSquared)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How far a mole has to shift in one tick to count as still moving, squared.
        /// </summary>
        /// <remarks>
        /// A quarter of a cell, which is about half a metre a second: an order of magnitude below
        /// walking pace and far below anything a player would call movement. Not zero, because a
        /// body resting on a slope can creep by a raw unit a tick for ever, and an exact test would
        /// let that one mole declare a round busy to its last tick and quietly turn the trim off.
        /// </remarks>
        private static Fix64 StirringSquared =>
            (WorldScale.CellSize / Fix64.FromInt(4)) * (WorldScale.CellSize / Fix64.FromInt(4));

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
                _landed[at] = moles[slot].LandedAt;
                _offDuty[at] = moles[slot].IsOffDuty;
            }

            if (shots.Count > 0)
            {
                List<Shot> flying = new List<Shot>(shots.Count);

                foreach (Projectile shot in shots)
                {
                    if (!shot.HasDetonated)
                    {
                        flying.Add(new Shot(shot.Position, shot.Weapon));
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
