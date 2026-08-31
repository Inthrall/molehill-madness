using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>The eight ways a mole leaves a match.</summary>
    /// <remarks>
    /// Nobody dies. Each of these is a piece of slapstick, and which one plays is decided
    /// here in the simulation rather than in the client, so every replay and every shared
    /// clip shows the same pratfall.
    ///
    /// The choice keys off how the mole went out, so the exit reads as a consequence rather
    /// than a shuffle. That is also why this is worth doing properly: these 1.2 seconds are
    /// the most-watched thing in the game, reused in results screens, clips and marketing.
    /// </remarks>
    public enum KnockoutExit : byte
    {
        /// <summary>Spins like a top and vanishes, leaving its boots standing.</summary>
        SpinAndPoof = 0,

        /// <summary>Two worm medics and a tiny stretcher. The signature exit.</summary>
        StretcherSquad = 1,

        /// <summary>Sits down heavily, stars and birds, tips over like a plank.</summary>
        DizzyBirds = 2,

        /// <summary>Inflates and zips off screen on a long raspberry.</summary>
        BalloonExit = 3,

        /// <summary>Punched clean through a dirt wall, leaving a mole-shaped hole.</summary>
        MoleShapedHole = 4,

        /// <summary>Launched out of frame. The helmet stays, spinning.</summary>
        HelmetSpin = 5,

        /// <summary>The floor gives way and it drops through, delighted.</summary>
        UndergroundExpress = 6,

        /// <summary>Hops about going "hot hot hot", then a burst of steam takes it.</summary>
        SteamPop = 7,
    }

    /// <summary>How a mole came to be off duty, which chooses its exit.</summary>
    public enum KnockoutCause : byte
    {
        /// <summary>Worn down without a final shove.</summary>
        Attrition = 0,

        /// <summary>A blast.</summary>
        Explosion = 1,

        /// <summary>Third landing in the lava.</summary>
        Lava = 2,

        /// <summary>A seismic shock through the soil.</summary>
        Seismic = 3,

        /// <summary>The Big Whack.</summary>
        Melee = 4,

        /// <summary>A trap it walked into.</summary>
        Trap = 5,

        /// <summary>The ground, arriving faster than expected.</summary>
        Fall = 6,
    }

    /// <summary>Picks the exit animation.</summary>
    public static class KnockoutReel
    {
        /// <summary>
        /// Chooses how a mole leaves, from what happened to it.
        /// </summary>
        /// <param name="cause">What finished it.</param>
        /// <param name="damage">Size of the final blow.</param>
        /// <param name="shove">How hard it was thrown.</param>
        /// <param name="wasUnderground">Whether it was below ground when it went.</param>
        /// <param name="rng">
        /// Used only to break ties between exits that fit equally well, and drawn from the
        /// match stream so a replay shows the same one.
        /// </param>
        public static KnockoutExit Choose(
            KnockoutCause cause, int damage, Fix64 shove, bool wasUnderground, MatchRng rng)
        {
            switch (cause)
            {
                case KnockoutCause.Lava:
                    return KnockoutExit.SteamPop;

                case KnockoutCause.Melee:
                    return KnockoutExit.HelmetSpin;

                case KnockoutCause.Seismic:
                    // The floor giving way underneath is a different joke from sitting
                    // down among cartoon birds, and which one happened is knowable.
                    return wasUnderground
                        ? KnockoutExit.UndergroundExpress
                        : KnockoutExit.DizzyBirds;

                case KnockoutCause.Trap:
                    return KnockoutExit.SpinAndPoof;

                case KnockoutCause.Fall:
                    // Sits down heavily, stars and birds, tips over like a plank, which is the one
                    // exit in the reel that is already a drawing of somebody who fell over. Named
                    // rather than left to the default, so a fall does not cost a draw from the match
                    // stream to choose between two exits that suit it less well.
                    return KnockoutExit.DizzyBirds;

                case KnockoutCause.Explosion:
                    if (shove >= HardShove)
                    {
                        return KnockoutExit.MoleShapedHole;
                    }

                    return damage >= BigHit
                        ? KnockoutExit.BalloonExit
                        : KnockoutExit.SpinAndPoof;

                default:
                    // Worn down by small change. A stretcher is the kinder reading, and a
                    // puff of dust the funnier one, so the match decides.
                    return rng.NextBool()
                        ? KnockoutExit.StretcherSquad
                        : KnockoutExit.SpinAndPoof;
            }
        }

        /// <summary>Damage above which a blast counts as a proper wallop.</summary>
        private const int BigHit = 25;

        /// <summary>Impulse above which the mole goes through something rather than over it.</summary>
        private static Fix64 HardShove => Fix64.FromInt(20);
    }
}
