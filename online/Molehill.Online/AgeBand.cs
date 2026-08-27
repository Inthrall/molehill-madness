using System;

namespace Molehill.Online
{
    /// <summary>
    /// Which side of the age threshold an account is on. The only thing about age this game keeps.
    /// </summary>
    /// <remarks>
    /// A band and not a date, and not a number of years either. The date of birth is typed once, used
    /// once to work this out, and stays on the device that asked for it: what travels to the relay is
    /// one of these three values. There is nothing to be gained by the relay knowing how old anybody
    /// is and a great deal to be lost by storing it.
    ///
    /// Three values rather than two, because "we have not asked yet" is a real state and must not be
    /// confused with either answer. Defaulting an unasked account to the safe side would be tempting
    /// and wrong: it would silently apply child protections to adults and, worse, would make a bug
    /// that skipped the gate look like a working gate.
    /// </remarks>
    public enum AgeBand
    {
        /// <summary>Nobody has been asked yet. Not an answer, and not a default.</summary>
        Unknown = 0,

        /// <summary>Under the threshold. Every protection on, and no way to turn them off.</summary>
        Child = 1,

        /// <summary>Over the threshold.</summary>
        Adult = 2,
    }

    /// <summary>
    /// What an account of a given age is allowed to do.
    /// </summary>
    /// <remarks>
    /// The design's rules, in one place, because the failure mode for this kind of thing is a check
    /// that exists in three places and disagrees with itself in one of them. Everything here is a
    /// pure function of the band, so it can be tested exhaustively, and every caller asks the same
    /// question of the same code.
    ///
    /// The design's shape is worth restating, because the interesting part is what is not gated:
    /// "local couch play is open to everyone and needs no account at all". Nothing here has any
    /// bearing on four people round one screen, and it must not acquire any: an age gate that stops
    /// a child playing a game on their own sofa is a gate protecting nobody from anything.
    /// </remarks>
    public static class Allowed
    {
        /// <summary>
        /// The age the design's protections hang off.
        /// </summary>
        /// <remarks>
        /// Thirteen, which is COPPA's line in the United States and the design's stated threshold.
        /// The design also says "and equivalent thresholds elsewhere", and elsewhere they differ:
        /// the GDPR leaves it to member states and the answers run from thirteen to sixteen.
        ///
        /// Deliberately not implemented as a per-country table. Getting that wrong is a legal problem
        /// rather than a bug, it needs advice this project has not taken, and a table of guesses would
        /// look far more authoritative than it deserved. One conservative number is the honest
        /// placeholder, and this comment is the note to come back to.
        /// </remarks>
        public const int Threshold = 13;

        /// <summary>
        /// Whether this account may be matched with strangers.
        /// </summary>
        /// <remarks>
        /// The design: "random matchmaking with strangers is gated to accounts over the threshold, or
        /// to younger ones with platform-level parental approval". This is the one rule the whole age
        /// gate exists to enforce, and it is the enforcement point: anything that pairs a player with
        /// somebody they did not invite has to come through here.
        ///
        /// Nothing calls it yet, because there is no matchmaking. That is deliberate on the plan's
        /// part rather than an omission on this one's: codes travel in friend groups and couch play
        /// works at a population of zero, so matchmaking is not needed until there are people to
        /// match. The guard exists first so that whoever adds matchmaking finds it already written
        /// rather than having to remember it.
        /// </remarks>
        public static bool Matchmaking(AgeBand band, bool parentalApproval = false) =>
            band switch
            {
                AgeBand.Adult => true,
                AgeBand.Child => parentalApproval,

                // Never on an unasked account. An account that has not been through the gate has not
                // been cleared for anything, and treating silence as consent is the whole failure.
                _ => false,
            };

        /// <summary>
        /// Whether an email address may be asked for or kept.
        /// </summary>
        /// <remarks>
        /// The design: under-threshold accounts get "no email collection". Not "collection with a
        /// consent box", not "collection we delete later". None.
        /// </remarks>
        public static bool EmailCollection(AgeBand band) => band == AgeBand.Adult;

        /// <summary>
        /// Whether anything beyond crash reporting may be measured.
        /// </summary>
        /// <remarks>
        /// The design allows crash reporting for everybody and calls it "the one permitted
        /// analytics". Everything else is off under the threshold, and since this project measures
        /// nothing else yet, the useful thing this does is make that a decision rather than an
        /// accident: whoever adds the first analytics call has a function to fail.
        /// </remarks>
        public static bool Analytics(AgeBand band) => band == AgeBand.Adult;

        /// <summary>Whether crash reporting is allowed. It always is, for everybody.</summary>
        public static bool CrashReporting(AgeBand band) => true;

        /// <summary>
        /// Whether the store may be reached.
        /// </summary>
        /// <remarks>
        /// The design: no store access under the threshold "without a platform-level parental
        /// approval". Platform-level matters: this game does not implement parental approval and must
        /// not pretend to, so the flag is something a platform hands us rather than something a
        /// player can tick.
        /// </remarks>
        public static bool Store(AgeBand band, bool parentalApproval = false) =>
            band == AgeBand.Adult || (band == AgeBand.Child && parentalApproval);

        /// <summary>
        /// Whether a match can be joined by code.
        /// </summary>
        /// <remarks>
        /// Everybody, including a child and including an account that has not been asked. A code
        /// arrives from somebody you know, in person or over a message, which is the design's whole
        /// model: "no friends list, no persistent codes", and a code travelling in a friend group is
        /// not a stranger encounter. Gating this would stop a child playing with their own family
        /// while doing nothing about the risk the gate exists for.
        /// </remarks>
        public static bool JoiningByCode(AgeBand band) => true;

        /// <summary>
        /// Works out the band from a date of birth.
        /// </summary>
        /// <remarks>
        /// The one place a date of birth is looked at, and it hands back a band rather than an age so
        /// that no caller is ever holding one. A date in the future, or an implausible one, comes back
        /// Unknown rather than being clamped: somebody who mistyped has not answered the question, and
        /// guessing on their behalf is exactly what this must not do.
        /// </remarks>
        public static AgeBand From(DateTime born, DateTime today)
        {
            if (born > today || born.Year < 1900)
            {
                return AgeBand.Unknown;
            }

            int years = today.Year - born.Year;

            // The birthday itself counts, so somebody turning thirteen today is thirteen today.
            if (today.Month < born.Month
                || (today.Month == born.Month && today.Day < born.Day))
            {
                years--;
            }

            return years >= Threshold ? AgeBand.Adult : AgeBand.Child;
        }
    }
}
