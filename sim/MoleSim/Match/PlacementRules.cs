using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// When a placement catches somebody and what it does to them, in one place so the round and
    /// the planning preview cannot disagree.
    /// </summary>
    /// <remarks>
    /// The traps, snares and vents used to be understood only by the match, which runs when a round
    /// resolves. The planning screen moves a ghost and nothing else, so a mole could be walked over
    /// an armed trap, or plant a vent and be thrown into the air by it, with nothing whatsoever
    /// showing while the turn was being planned. That is the same fault the Tunnel Torpedo had, and
    /// the Power Claws, and the sandbag, and it kept coming back because the rule had one home and
    /// needed two callers.
    ///
    /// So there is one copy of each rule here and both callers use it. Whoever adds the fourth kind
    /// of placement gets the preview for free rather than discovering a year later that it was never
    /// wired up.
    /// </remarks>
    internal static class PlacementRules
    {
        /// <summary>
        /// Builds the placement a tool leaves behind, or null if that weapon leaves nothing.
        /// </summary>
        /// <remarks>
        /// The arming and expiry rules are the balance and they live here: a trap sits as a
        /// suspicious mound for a round before it can catch anybody, so opponents get to decide
        /// whether to respect it or test it; a snare is live at once and gone after this round, so it
        /// costs its victim exactly one turn; a vent is live at once and stays for good.
        /// </remarks>
        public static Placement? Make(WeaponId weapon, int ownerSeat, Vec2 at, int round, int tick)
        {
            switch (weapon)
            {
                case WeaponId.SnapTrap:
                    return new Placement(
                        weapon, ownerSeat, at, round, tick,
                        round + MatchSettings.TrapArmDelay, int.MaxValue);

                case WeaponId.RootSnare:
                    return new Placement(weapon, ownerSeat, at, round, tick, round, round);

                case WeaponId.GeyserCap:
                    return new Placement(weapon, ownerSeat, at, round, tick, round, int.MaxValue);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether a placement is live this round at all.
        /// </summary>
        /// <remarks>
        /// Asked once per placement, before looking at any mole, and that is deliberate rather than
        /// incidental. A snap trap sets itself spent when it goes off, so asking again for each mole
        /// would let the first one caught shield everybody else standing on it. Two moles on one trap
        /// both take it, which is how this has always behaved.
        /// </remarks>
        public static bool IsLive(Placement placement, int round) => placement.IsArmed(round);

        /// <summary>Whether a live placement is close enough to act on a particular mole.</summary>
        public static bool Touches(Placement placement, Mole mole) =>
            !mole.IsOffDuty
            && Vec2.Distance(mole.Position, placement.Position)
                <= WeaponTable.Of(placement.Weapon).BlastRadius;

        /// <summary>What happened to a mole, so a caller can record it without knowing the rules.</summary>
        public readonly struct Bite
        {
            public Bite(int damage, bool wentOffDuty)
            {
                Damage = damage;
                WentOffDuty = wentOffDuty;
            }

            public int Damage { get; }

            public bool WentOffDuty { get; }

            /// <summary>Whether anything worth recording happened. A snare and a vent do not hurt.</summary>
            public bool Hurt => Damage > 0;
        }

        /// <summary>
        /// Applies one placement to one mole that <see cref="Reaches"/> has already accepted.
        /// </summary>
        public static Bite Apply(Placement placement, Mole mole)
        {
            WeaponSpec spec = WeaponTable.Of(placement.Weapon);

            switch (placement.Weapon)
            {
                case WeaponId.SnapTrap:
                    placement.Spent = true;
                    bool wentOffDuty = mole.TakeDamage(spec.Damage);
                    mole.AddImpulse(-Vec2.UnitY * spec.Knockback);
                    return new Bite(spec.Damage, wentOffDuty);

                case WeaponId.RootSnare:
                    mole.IsSnared = true;
                    return default;

                case WeaponId.GeyserCap:
                    // Only off the ground, so a mole already in the air is not thrown twice by the
                    // same vent on its way past.
                    if (!mole.IsAirborne)
                    {
                        mole.AddImpulse(-Vec2.UnitY * spec.Knockback);
                    }

                    return default;

                default:
                    return default;
            }
        }
    }
}
