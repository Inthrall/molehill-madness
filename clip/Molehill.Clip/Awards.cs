using System.Collections.Generic;
using MoleSim.Match;

namespace Molehill.Clip
{
    /// <summary>What one mole is remembered for.</summary>
    public enum Feat
    {
        /// <summary>Nothing happened worth a title.</summary>
        None = 0,

        /// <summary>Dealt the most damage to somebody else's platoon.</summary>
        Ordnance = 1,

        /// <summary>Took the most and was still standing at the end.</summary>
        Survivor = 2,

        /// <summary>Did the most damage to its own platoon, itself included.</summary>
        OwnGoal = 3,

        /// <summary>Got to the most crates.</summary>
        Scavenger = 4,
    }

    /// <summary>One mole, what it is remembered for, and how much of it there was.</summary>
    public readonly struct Award
    {
        public Award(int seat, int moleIndex, Feat feat, int score)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Feat = feat;
            Score = score;
        }

        public static Award Nobody => new Award(-1, -1, Feat.None, 0);

        public int Seat { get; }

        public int MoleIndex { get; }

        public Feat Feat { get; }

        /// <summary>Damage, or crates, depending on the feat. Only meaningful against itself.</summary>
        public int Score { get; }

        public bool Exists => Feat != Feat.None && Seat >= 0;
    }

    /// <summary>
    /// Picks the one mole out of sixteen that the match was about.
    /// </summary>
    /// <remarks>
    /// The design's promise is that any mole can be the hero of any match, because every mole is
    /// mechanically identical and there are no classes: the one who survives on four pluck and lands
    /// the last shot was not built for it in a menu, the match made them. This is the machinery that
    /// notices, and it only reads what happened.
    ///
    /// Beside the drama scorer because it is the same kind of thing done to the same input: a pure
    /// pass over the finished rounds, integer arithmetic, no simulation state and no opinion that
    /// could drift between two clients. Not in MoleSim, which is the rules and should stay them; an
    /// award changes nothing about a match and reading one out of the results afterwards is not the
    /// simulation's business.
    ///
    /// Four feats rather than the design's five. It lists "most damage, longest tunnel, luckiest
    /// survival, worst self-inflicted disaster", and the tunnel is the one that is missing: nothing
    /// in a round result says how much ground a particular mole moved, because carving returns a cell
    /// count that the movement solver throws away. Adding it means a counter on the mole and a
    /// per-mole total on the round, which is a deliberate change to make on its own rather than
    /// smuggled in behind an award. Written down here rather than left to be noticed.
    ///
    /// The plan says this "runs off round stats the sim already emits", and that was not true when it
    /// was written. A hit recorded who took it and not who caused it, so two of these four feats were
    /// uncomputable. Attribution went into <see cref="BlastHit"/> first, which the clip scorer had
    /// already asked for in writing and for the same reason.
    /// </remarks>
    public static class Awards
    {
        /// <summary>
        /// Reads a finished match and picks its mole of the match.
        /// </summary>
        /// <remarks>
        /// Ordered rather than weighted. A single scale across "dealt forty damage" and "collected
        /// three crates" would be inventing an exchange rate between two things that have none, so
        /// instead the feats have a precedence and the best example of the best-represented feat
        /// wins. Ordnance first because hitting people is what the game is about, then the own goal,
        /// because in a game where nobody dies it is the most shareable thing that can happen and it
        /// deserves to beat merely surviving.
        ///
        /// Ties go to the lower seat and then the lower mole, which is arbitrary and has to be
        /// something: what matters is that two clients reading the same rounds agree.
        /// </remarks>
        public static Award MoleOfTheMatch(
            IEnumerable<RoundResult> rounds, int playerCount, int molesPerPlatoon)
        {
            if (rounds is null)
            {
                return Award.Nobody;
            }

            List<BlastHit> hits = new List<BlastHit>();
            List<Knockout> knockouts = new List<Knockout>();
            List<CrateClaim> claims = new List<CrateClaim>();

            foreach (RoundResult round in rounds)
            {
                if (round is null)
                {
                    continue;
                }

                hits.AddRange(round.Hits);
                knockouts.AddRange(round.Knockouts);
                claims.AddRange(round.CrateClaims);
            }

            return From(hits, knockouts, claims, playerCount, molesPerPlatoon);
        }

        /// <summary>
        /// The same reading, from the pieces rather than from the rounds they arrived in.
        /// </summary>
        /// <remarks>
        /// Separate because a match cannot be made to produce a chosen tally on demand, and the
        /// precedence between the feats is an opinion this file is asserting about the game rather
        /// than an accident of arithmetic. It should be possible to state "an own goal beats merely
        /// surviving" as a test and have it fail loudly when somebody reorders them.
        ///
        /// The rest is checked against a real resolved match, because a scorer that agrees with
        /// itself about a fabricated round has proved nothing. Same split, and same reasoning, as the
        /// drama scorer beside it.
        /// </remarks>
        public static Award From(
            IEnumerable<BlastHit> hits,
            IEnumerable<Knockout> knockouts,
            IEnumerable<CrateClaim> claims,
            int playerCount,
            int molesPerPlatoon)
        {
            if (playerCount <= 0 || molesPerPlatoon <= 0)
            {
                return Award.Nobody;
            }

            int seats = playerCount;
            int moles = molesPerPlatoon;
            int[,] dealt = new int[seats, moles];
            int[,] ownGoals = new int[seats, moles];
            int[,] taken = new int[seats, moles];
            int[,] crates = new int[seats, moles];
            bool[,] wentOut = new bool[seats, moles];

            foreach (BlastHit hit in hits ?? System.Linq.Enumerable.Empty<BlastHit>())
            {
                if (Held(hit.Seat, hit.MoleIndex, seats, moles))
                {
                    taken[hit.Seat, hit.MoleIndex] += hit.Damage;
                }

                // Nobody to credit: lava, or a trap that only remembers the seat that laid it.
                if (!Held(hit.BySeat, hit.ByMoleIndex, seats, moles))
                {
                    continue;
                }

                if (hit.FriendlyFire)
                {
                    ownGoals[hit.BySeat, hit.ByMoleIndex] += hit.Damage;
                }
                else
                {
                    dealt[hit.BySeat, hit.ByMoleIndex] += hit.Damage;
                }
            }

            foreach (Knockout knockout in knockouts ?? System.Linq.Enumerable.Empty<Knockout>())
            {
                if (Held(knockout.Seat, knockout.MoleIndex, seats, moles))
                {
                    wentOut[knockout.Seat, knockout.MoleIndex] = true;
                }
            }

            foreach (CrateClaim claim in claims ?? System.Linq.Enumerable.Empty<CrateClaim>())
            {
                if (!claim.Shattered && Held(claim.Seat, claim.MoleIndex, seats, moles))
                {
                    crates[claim.Seat, claim.MoleIndex]++;
                }
            }

            // Best of each feat, then the feats in order of precedence.
            Award ordnance = Best(dealt, seats, moles, Feat.Ordnance, null);
            Award ownGoal = Best(ownGoals, seats, moles, Feat.OwnGoal, null);
            Award survivor = Best(taken, seats, moles, Feat.Survivor, wentOut);
            Award scavenger = Best(crates, seats, moles, Feat.Scavenger, null);

            if (ordnance.Exists)
            {
                return ordnance;
            }

            if (ownGoal.Exists)
            {
                return ownGoal;
            }

            if (survivor.Exists)
            {
                return survivor;
            }

            return scavenger.Exists ? scavenger : Award.Nobody;
        }

        /// <summary>The highest scorer in a table, optionally only among moles still standing.</summary>
        private static Award Best(
            int[,] table, int seats, int moles, Feat feat, bool[,]? excluded)
        {
            int bestSeat = -1;
            int bestMole = -1;
            int best = 0;

            for (int seat = 0; seat < seats; seat++)
            {
                for (int mole = 0; mole < moles; mole++)
                {
                    if (excluded is not null && excluded[seat, mole])
                    {
                        continue;
                    }

                    // Strictly greater, so the first seat and mole to reach a score keeps it and the
                    // answer does not depend on the order the loops happen to run in.
                    if (table[seat, mole] <= best)
                    {
                        continue;
                    }

                    best = table[seat, mole];
                    bestSeat = seat;
                    bestMole = mole;
                }
            }

            return best > 0 ? new Award(bestSeat, bestMole, feat, best) : Award.Nobody;
        }

        private static bool Held(int seat, int mole, int seats, int moles) =>
            seat >= 0 && seat < seats && mole >= 0 && mole < moles;
    }
}
