using System;
using System.Collections.Generic;
using MoleSim.Match;

namespace Molehill.Clip
{
    /// <summary>A moment worth showing somebody, and how worth showing it is.</summary>
    public readonly struct Moment
    {
        public Moment(int round, int tick, int score, MomentKind kind, int slot = -1)
        {
            Round = round;
            Tick = tick;
            Score = score;
            Kind = kind;
            Slot = slot;
        }

        public static Moment Nothing => new Moment(-1, -1, 0, MomentKind.Nothing);

        /// <summary>
        /// Which mole the moment happened to, as a recording slot, or -1 if it is nobody in particular.
        /// </summary>
        /// <remarks>
        /// The camera needs this. A moment with a tick and no subject can be cut to the right instant and
        /// still be pointed at the wrong end of the map, which is the same defect as the replay camera
        /// that framed a tight shot of empty sky.
        /// </remarks>
        public int Slot { get; }

        public int Round { get; }

        /// <summary>The tick the moment turns on, which a clip is cut around rather than started at.</summary>
        public int Tick { get; }

        public int Score { get; }

        public MomentKind Kind { get; }

        public bool Exists => Round > 0 && Score > 0;
    }

    /// <summary>What kind of thing happened, which is most of why it is worth watching.</summary>
    public enum MomentKind
    {
        Nothing = 0,

        /// <summary>Somebody hit somebody. The floor.</summary>
        Hit = 1,

        /// <summary>A mole went off duty.</summary>
        Knockout = 2,

        /// <summary>Two or more went off duty at once, from one shot.</summary>
        TwoAtOnce = 3,

        /// <summary>The shot that ended the match.</summary>
        Winner = 4,

    }

    /// <summary>
    /// Picks the moment out of a match that somebody would actually want to watch.
    /// </summary>
    /// <remarks>
    /// Version zero, and deliberately a table of integers rather than anything clever. The plan calls
    /// this a drama scorer and the temptation is to make it clever, but the thing it has to get right
    /// is an ordering, and an ordering that a person can read off the page is one they can argue with.
    /// Every weight below is a claim about what is funny, and all of them are guesses that a playtest
    /// should be allowed to overturn cheaply.
    ///
    /// Integer arithmetic throughout, and not because determinism requires it: nothing here feeds the
    /// simulation. It is so that two builds pick the same moment out of the same match, which matters
    /// the first time somebody says "the clip it chose was the wrong one" and the round has to be
    /// re-scored on a different machine to see what it saw.
    ///
    /// One thing is missing and is missing on purpose. In a game where "nobody dies, moles go
    /// comically off duty", somebody blowing themselves up is the most shareable thing that can
    /// happen, and it ought to outrank an ordinary knockout. It cannot be scored yet: a Knockout
    /// records who went off duty and not who did it, so an own goal is indistinguishable from being
    /// shot by somebody else. Teaching the simulation to record the attacker is a change worth making
    /// deliberately rather than smuggling in for the sake of a clip scorer, so it is written down here
    /// instead.
    /// </remarks>
    public static class Drama
    {
        /// <summary>
        /// What a moment is worth. The whole opinion, in one function.
        /// </summary>
        /// <remarks>
        /// Public so the ordering claims can be tested directly, which matters more than it looks:
        /// every number in here is a guess about what is funny, and the useful thing a test can do is
        /// pin the claims rather than the arithmetic. That a knockout beats any hit, that catching two
        /// beats catching one, and that the shot which ends a match beats everything are the three
        /// things this file is asserting about the game, and they should fail loudly if somebody
        /// retunes a weight and breaks one by accident.
        /// </remarks>
        public static int Score(MomentKind kind, int caughtTogether = 1, int damage = 0)
        {
            int worth = Worth(kind);

            // Nothing plus a bonus is still nothing. Adding the bonuses unconditionally scored a
            // Nothing moment with four moles and ninety damage at 630, which is above a knockout: a
            // bonus modifies something, and is not a thing on its own.
            if (worth == 0)
            {
                return 0;
            }

            return worth
                + ((Math.Max(caughtTogether, 1) - 1) * PerExtraCaught)
                + (damage * PerDamage);
        }

        /// <summary>What each kind of moment is worth before anything is added to it.</summary>
        private static int Worth(MomentKind kind) => kind switch
        {
            MomentKind.Winner => 1000,
            MomentKind.TwoAtOnce => 700,
            MomentKind.Knockout => 400,
            MomentKind.Hit => 100,
            _ => 0,
        };

        /// <summary>How much a point of damage adds. Small, so a big hit cannot outrank a knockout.</summary>
        private const int PerDamage = 2;

        /// <summary>
        /// How much each extra mole caught in one blast adds.
        /// </summary>
        /// <remarks>
        /// Generous, because a shot that catches three moles is the story of the whole match and
        /// there is no other signal that says so.
        /// </remarks>
        private const int PerExtraCaught = 150;

        /// <summary>
        /// Scores one round and hands back its best moment.
        /// </summary>
        /// <remarks>
        /// Needs the recording, because a moment without a tick cannot be cut around. A round resolved
        /// without recording scores nothing rather than guessing a tick, since a clip starting in the
        /// wrong place is worse than no clip.
        /// </remarks>
        public static Moment Best(RoundResult result)
        {
            if (result?.Recording is null)
            {
                return Moment.Nothing;
            }

            Moment best = Moment.Nothing;

            foreach (Moment moment in Moments(result))
            {
                if (moment.Score > best.Score)
                {
                    best = moment;
                }
            }

            return best;
        }

        /// <summary>The best moment across a whole match, for the one clip it gets offered.</summary>
        public static Moment Best(IEnumerable<RoundResult> rounds)
        {
            Moment best = Moment.Nothing;

            if (rounds is null)
            {
                return best;
            }

            foreach (RoundResult round in rounds)
            {
                Moment moment = Best(round);

                // Strictly better, so the earliest round wins a tie. A match whose first round was
                // the funniest thing in it should offer that, not the last equally-good one.
                if (moment.Score > best.Score)
                {
                    best = moment;
                }
            }

            return best;
        }

        /// <summary>Every moment in a round, scored.</summary>
        public static IReadOnlyList<Moment> Moments(RoundResult result)
        {
            List<Moment> found = new List<Moment>();

            if (result?.Recording is null)
            {
                return found;
            }

            RoundRecording recording = result.Recording;

            // Knockouts first, because they are the moments worth having and the hits are the floor.
            for (int index = 0; index < result.Knockouts.Count; index++)
            {
                Knockout knockout = result.Knockouts[index];
                int slot = (knockout.Seat * MatchSettings.MolesPerPlatoon) + knockout.MoleIndex;
                int tick = WentOff(recording, slot);

                if (tick < 0)
                {
                    continue;
                }

                int together = AtTheSameTick(result, recording, tick);

                MomentKind kind =
                    result.MatchOver && index == result.Knockouts.Count - 1 ? MomentKind.Winner
                    : together > 1 ? MomentKind.TwoAtOnce
                    : MomentKind.Knockout;

                int score = Score(kind, together);

                found.Add(new Moment(result.Round, tick, score, kind, slot));
            }

            // And the best plain hit, so a round where nobody went off duty can still offer
            // something rather than nothing.
            Moment hardest = Moment.Nothing;

            foreach (BlastHit hit in result.Hits)
            {
                int score = Score(MomentKind.Hit, damage: hit.Damage);

                if (score > hardest.Score)
                {
                    // Mid-round rather than at a known tick: a hit's tick is not recorded, and the
                    // middle of the round is a better guess than the start.
                    hardest = new Moment(
                        result.Round, recording.Ticks / 2, score, MomentKind.Hit);
                }
            }

            if (hardest.Exists)
            {
                found.Add(hardest);
            }

            return found;
        }

        /// <summary>The tick a mole went off duty, or -1 if it did not.</summary>
        private static int WentOff(RoundRecording recording, int slot)
        {
            if (slot < 0 || slot >= recording.MoleCount)
            {
                return -1;
            }

            for (int tick = 0; tick < recording.Ticks; tick++)
            {
                if (recording.IsOffDutyAt(tick, slot))
                {
                    return tick;
                }
            }

            return -1;
        }

        /// <summary>
        /// How many moles went off duty on the same tick.
        /// </summary>
        /// <remarks>
        /// Which is what makes a double a double: one blast catching two platoons at once, rather than
        /// two unrelated knockouts in the same round.
        /// </remarks>
        private static int AtTheSameTick(
            RoundResult result, RoundRecording recording, int tick)
        {
            int together = 0;

            foreach (Knockout knockout in result.Knockouts)
            {
                int slot = (knockout.Seat * MatchSettings.MolesPerPlatoon) + knockout.MoleIndex;

                if (WentOff(recording, slot) == tick)
                {
                    together++;
                }
            }

            return Math.Max(together, 1);
        }
    }
}
