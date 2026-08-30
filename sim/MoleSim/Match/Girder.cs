using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// A girder somebody laid: where it started, and which way it runs.
    /// </summary>
    /// <remarks>
    /// Recorded rather than derived, because a girder leaves nothing behind that says it was one.
    /// <see cref="Tools.LayGirder"/> deposits ordinary loose soil, deliberately, so that it can be
    /// dug back out; and once it is in the ground a plank of soil is indistinguishable from any
    /// other soil in the same place. Which is the right answer for the simulation and no answer at
    /// all for the client, which has a picture of a steel beam to put somewhere.
    ///
    /// Not a <see cref="Placement"/>, though it is close enough to look like one. A placement is
    /// something that acts on its own and therefore something the rules ask about every round; this
    /// is only a note of what happened, and giving it arming and expiry fields it would never use
    /// would be inviting somebody to make it dangerous later without deciding to.
    ///
    /// Deliberately outside <see cref="MoleMatch.StateHash"/>, which is worth saying plainly now
    /// that the hash reaches the placements and the crates and everything else that survives a
    /// round. The rule there is coverage of state the simulation acts on, and this is the one piece
    /// of match state it never reads: nothing here is consulted by any rule, so a machine cannot act
    /// on a girder list at all, let alone act on a different one. It could only differ from another
    /// machine's if the aim that made it differed, and that same aim lays the soil, so the terrain
    /// hash has already caught it by the time this could disagree.
    ///
    /// Which also keeps it off the corpus pins, so a picture the client draws cannot move a number
    /// that is supposed to mean the rules changed.
    /// </remarks>
    public sealed class Girder
    {
        public Girder(Vec2 at, Vec2 along, int laidOnRound, int laidOnTick)
        {
            At = at;
            Along = along;
            LaidOnRound = laidOnRound;
            LaidOnTick = laidOnTick;
        }

        /// <summary>The mole's own position when it laid this. The beam runs out from here.</summary>
        public Vec2 At { get; }

        /// <summary>Which way it runs, as a unit vector.</summary>
        public Vec2 Along { get; }

        public int LaidOnRound { get; }

        /// <summary>
        /// The tick within that round it went down on.
        /// </summary>
        /// <remarks>
        /// Kept for the same reason <see cref="Placement.PlacedOnTick"/> is: a round has already
        /// resolved before anybody watches it, so anything read from live state during playback is
        /// on the screen from the first frame, and a bridge that exists before the mole builds it
        /// gives away that the mole was going to.
        /// </remarks>
        public int LaidOnTick { get; }
    }
}
