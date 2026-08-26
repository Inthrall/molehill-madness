using System.Collections.Generic;
using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>One mole leaving the match, and how it went.</summary>
    public readonly struct Knockout
    {
        public Knockout(int seat, int moleIndex, KnockoutCause cause, KnockoutExit exit)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Cause = cause;
            Exit = exit;
        }

        public int Seat { get; }

        public int MoleIndex { get; }

        public KnockoutCause Cause { get; }

        /// <summary>Which piece of slapstick the client should play.</summary>
        public KnockoutExit Exit { get; }
    }

    /// <summary>A crate coming down next round, so it can be drawn during planning.</summary>
    public readonly struct CrateTelegraph
    {
        public CrateTelegraph(Vec2 position)
        {
            Position = position;
        }

        /// <summary>Exactly where it will land. Contents stay hidden until somebody has it.</summary>
        public Vec2 Position { get; }
    }

    /// <summary>Somebody picking a crate up, or two of them tearing it apart.</summary>
    public readonly struct CrateClaim
    {
        public CrateClaim(int seat, int moleIndex, CrateContents contents, bool shattered)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Contents = contents;
            Shattered = shattered;
        }

        public int Seat { get; }

        public int MoleIndex { get; }

        public CrateContents Contents { get; }

        /// <summary>
        /// True when three or more arrived at once and nobody got anything, which is both
        /// deterministic and correct.
        /// </summary>
        public bool Shattered { get; }
    }

    /// <summary>Everything that happened in one round, for the aftermath and for clips.</summary>
    public sealed class RoundResult
    {
        internal RoundResult(int round)
        {
            Round = round;
            Hits = new List<BlastHit>();
            Knockouts = new List<Knockout>();
            CrateClaims = new List<CrateClaim>();
            NextCrates = new List<CrateTelegraph>();
            WinningSeat = -1;
        }

        /// <summary>Which round this was, counting from one.</summary>
        public int Round { get; }

        /// <summary>Every hit landed, in the order it happened.</summary>
        public List<BlastHit> Hits { get; }

        /// <summary>Every mole that went off duty, and how.</summary>
        public List<Knockout> Knockouts { get; }

        /// <summary>Crates claimed, split or torn apart this round.</summary>
        public List<CrateClaim> CrateClaims { get; }

        /// <summary>
        /// Where the next crates will come down. Announced in the aftermath so the fight
        /// over them is something everybody scheduled in advance.
        /// </summary>
        public List<CrateTelegraph> NextCrates { get; }

        /// <summary>
        /// How many things went off. Worth having for the aftermath tally, and it is the
        /// direct way to see that a shot cancelled by damage never happened.
        /// </summary>
        public int Detonations { get; internal set; }

        /// <summary>
        /// True on the round the stalemate nudge bites, so the client can show everybody
        /// why their legs suddenly got shorter.
        /// </summary>
        public bool StalemateNudged { get; internal set; }

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
