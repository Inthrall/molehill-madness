using System.Collections.Generic;

namespace MoleSim.Match
{
    /// <summary>How a mole came to be off duty, which chooses its exit animation.</summary>
    /// <remarks>
    /// The design has the knockout reel picked from how the mole went out rather than
    /// shuffled, so that the pratfall reads as a consequence. The choice is made in the
    /// simulation, not the client, so every replay and every clip shows the same one.
    /// </remarks>
    public enum KnockoutCause : byte
    {
        /// <summary>Worn down without a final shove: the stretcher squad.</summary>
        Attrition = 0,

        /// <summary>A blast, with knockback to match.</summary>
        Explosion = 1,

        /// <summary>Third landing in the lava: the steam pop.</summary>
        Lava = 2,
    }

    /// <summary>One mole leaving the match.</summary>
    public readonly struct Knockout
    {
        public Knockout(int seat, int moleIndex, KnockoutCause cause)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Cause = cause;
        }

        public int Seat { get; }

        public int MoleIndex { get; }

        public KnockoutCause Cause { get; }
    }

    /// <summary>Everything that happened in one round, for the aftermath and for clips.</summary>
    public sealed class RoundResult
    {
        internal RoundResult(int round)
        {
            Round = round;
            Hits = new List<BlastHit>();
            Knockouts = new List<Knockout>();
            WinningSeat = -1;
        }

        /// <summary>Which round this was, counting from one.</summary>
        public int Round { get; }

        /// <summary>Every hit landed, in the order it happened.</summary>
        public List<BlastHit> Hits { get; }

        /// <summary>Every mole that went off duty.</summary>
        public List<Knockout> Knockouts { get; }

        /// <summary>
        /// How many things went off. Worth having for the aftermath tally, and it is the
        /// direct way to see that a shot cancelled by damage never happened.
        /// </summary>
        public int Detonations { get; internal set; }

        /// <summary>Whether the match is finished.</summary>
        public bool MatchOver { get; internal set; }

        /// <summary>The winning seat, or -1 if the match continues or everybody went out together.</summary>
        public int WinningSeat { get; internal set; }

        /// <summary>Total damage dealt this round, which the stalemate nudge watches.</summary>
        public int TotalDamage
        {
            get
            {
                int total = 0;

                foreach (BlastHit hit in Hits)
                {
                    total += hit.Damage;
                }

                return total;
            }
        }
    }
}
