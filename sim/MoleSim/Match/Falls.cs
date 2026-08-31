using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// What hitting the ground costs.
    /// </summary>
    /// <remarks>
    /// Its own type rather than a method on the mole or the match, because three places have to
    /// agree about it and only one of them is the round. <see cref="MoleMotion"/> measures the
    /// landing, <see cref="MoleMatch"/> charges for it, <see cref="SteeredWalk"/> charges the ghost
    /// for it so the planning gauges are honest about a route that walks off a cliff, and the client
    /// asks the same question to decide whether to throw up any dust. A formula copied into four
    /// places is a preview that lies about the round.
    ///
    /// The rule is deliberately as plain as it can be: nothing below the safe speed, and a flat
    /// price per metre a second above it, up to a cap. The design's one rule about damage is that
    /// taking any of it ends a mole's turn, and that is what makes an unplanned drop expensive
    /// rather than the number itself.
    /// </remarks>
    public static class Falls
    {
        /// <summary>
        /// What a landing at this closing speed costs, in pluck. Zero for anything survivable.
        /// </summary>
        /// <param name="landingSpeed">
        /// How fast the mole was closing on the surface, not how fast it was travelling. A mole
        /// sliding along a slope at speed has not landed on anything.
        /// </param>
        public static int DamageFor(Fix64 landingSpeed)
        {
            if (landingSpeed <= MatchSettings.SafeLandingSpeed)
            {
                return 0;
            }

            int over = Fix64.ToInt(landingSpeed - MatchSettings.SafeLandingSpeed);
            int damage = over * MatchSettings.FallDamagePerSpeed;

            return damage > MatchSettings.WorstFallDamage ? MatchSettings.WorstFallDamage : damage;
        }
    }
}
