using System.Collections.Generic;
using MoleSim.Match;

namespace Molehill.Online
{
    /// <summary>
    /// Puts a round's plans into a simulation, and throws away the ones that are not allowed.
    /// </summary>
    /// <remarks>
    /// This is the other half of the design's anti-cheat story, and the half that is easy to miss.
    /// The argument is that a plan is inputs rather than an outcome, so a client "cannot submit an
    /// illegal state, only illegal inputs, which every client's sim rejects identically". The relay
    /// never looks at a plan, so nothing before this point has checked one.
    ///
    /// MoleMatch.SubmitPlan enforces the rules by throwing, which is right for a local game where an
    /// illegal plan is a bug in the client that built it. Online it is not a bug, it is a stranger,
    /// or an older build, or a plan that was legal when it was written and is not now. Letting that
    /// exception out would take the whole game down on the word of somebody else's phone.
    ///
    /// So a refused plan is dropped, and the platoon that sent it does nothing that round. The
    /// important part is that this is not leniency: every client runs this same code over the same
    /// bytes, so every client drops exactly the same plans and they all stay in the same world. A
    /// cheat costs the cheat its turn.
    /// </remarks>
    public static class RoundFeeder
    {
        /// <summary>
        /// Feeds every plan it can and reports the ones it could not.
        /// </summary>
        /// <returns>How many plans were refused, which is zero in every honest match.</returns>
        public static int Feed(MoleMatch match, IReadOnlyList<Plan> plans, out List<int> refused)
        {
            refused = new List<int>();

            if (match is null || plans is null)
            {
                return 0;
            }

            foreach (Plan plan in plans)
            {
                try
                {
                    match.SubmitPlan(plan);
                }
                catch (InvalidPlanException)
                {
                    // Deliberately swallowed, and deliberately not logged as an error. The plan
                    // broke a rule, the platoon loses its turn, and every other participant reached
                    // the same conclusion from the same bytes.
                    refused.Add(plan.Seat);
                }
            }

            return refused.Count;
        }

        /// <summary>The same, for callers with no use for the list.</summary>
        public static int Feed(MoleMatch match, IReadOnlyList<Plan> plans) =>
            Feed(match, plans, out _);
    }
}
