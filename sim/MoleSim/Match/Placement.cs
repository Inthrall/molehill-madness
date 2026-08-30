using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// Something left on the map that acts on its own: a trap, a snare, a capped vent.
    /// </summary>
    /// <remarks>
    /// All three are the same shape of thing, so they share one list rather than three.
    /// What separates them is when they become dangerous, whether they are used up, and
    /// whether they ever expire.
    /// </remarks>
    public sealed class Placement
    {
        public Placement(
            WeaponId weapon,
            int ownerSeat,
            Vec2 position,
            int placedOnRound,
            int placedOnTick,
            int armsOnRound,
            int expiresAfterRound)
        {
            Weapon = weapon;
            OwnerSeat = ownerSeat;
            Position = position;
            PlacedOnRound = placedOnRound;
            PlacedOnTick = placedOnTick;
            ArmsOnRound = armsOnRound;
            ExpiresAfterRound = expiresAfterRound;
        }

        public WeaponId Weapon { get; }

        /// <summary>Who left it. Recorded for the tally, never for deciding who it catches.</summary>
        public int OwnerSeat { get; }

        public Vec2 Position { get; }

        /// <summary>
        /// The round and the tick within it that this was put down.
        /// </summary>
        /// <remarks>
        /// Recorded so a replay can show it appearing at the moment the mole planted it rather
        /// than having it there from the first frame. A round resolves before anybody watches it,
        /// so everything read from live state during playback gives the round away, and a trap
        /// that exists before the mole reaches the spot is a small but real spoiler of that kind.
        /// </remarks>
        public int PlacedOnRound { get; }

        public int PlacedOnTick { get; }

        /// <summary>
        /// The round from which this can catch somebody. A trap placed now arms next
        /// round, so it sits there as a suspicious mound for a whole round first and
        /// opponents get to decide whether to respect it or test it.
        /// </summary>
        public int ArmsOnRound { get; }

        /// <summary>Last round this is still around. <see cref="int.MaxValue"/> for forever.</summary>
        public int ExpiresAfterRound { get; }

        /// <summary>Whether it has already gone off. Snares and vents never do.</summary>
        public bool Spent { get; set; }

        public bool IsArmed(int round) => !Spent && round >= ArmsOnRound && round <= ExpiresAfterRound;

        /// <summary>
        /// A copy, for the planning preview to spend without spending the real one.
        /// </summary>
        /// <remarks>
        /// Spent is settable, and a trap goes off by setting it. The preview walks a ghost over the
        /// same hazards to show what a turn would cost, so handing it the real placements would let
        /// merely thinking about walking onto a trap disarm it for everybody.
        /// </remarks>
        public Placement Copy() =>
            new Placement(
                Weapon, OwnerSeat, Position, PlacedOnRound, PlacedOnTick,
                ArmsOnRound, ExpiresAfterRound)
            {
                Spent = Spent,
            };
    }
}
