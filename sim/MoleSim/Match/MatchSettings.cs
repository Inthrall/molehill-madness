using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// Every tunable number in the game, in one place.
    /// </summary>
    /// <remarks>
    /// The design document is explicit that these are chosen rather than proven, and that
    /// retuning the pacing of the whole game should mean editing a table rather than
    /// hunting through the physics. Nothing outside this type may invent a constant.
    ///
    /// Material movement costs live next door in <see cref="Terrain.MaterialTable"/>,
    /// because they belong to the terrain rather than to the match.
    /// </remarks>
    public static class MatchSettings
    {
        // ---- Time -------------------------------------------------------------------

        /// <summary>Simulation rate. The renderer interpolates to whatever the display wants.</summary>
        public const int TicksPerSecond = 30;

        /// <summary>Length of the resolution beat.</summary>
        public const int RoundSeconds = 8;

        /// <summary>240 ticks, and the same number regardless of how many are playing.</summary>
        public const int TicksPerRound = TicksPerSecond * RoundSeconds;

        /// <summary>One tick, in seconds.</summary>
        public static Fix64 TickDuration => Fix64.Ratio(1, TicksPerSecond);

        /// <summary>
        /// The longest a round will run past its own length waiting for everybody to come to rest.
        /// </summary>
        /// <remarks>
        /// A safety stop rather than a budget, and it is set well above anything the physics can
        /// actually produce: a blast cannot fling a mole faster than <see cref="TerminalSpeed"/>,
        /// so the worst case is one thrown straight up at 45 taking 45/18 seconds to stop climbing
        /// and as long again to come back down, which is five seconds. Anything still moving after
        /// six is not momentum finishing, it is something wrong, and a round that never ends is a
        /// worse answer than a mole that stops in the air.
        /// </remarks>
        public const int MaxSettleTicks = TicksPerSecond * 6;

        // ---- The mole ---------------------------------------------------------------

        /// <summary>Health, in the game's own vocabulary. Nobody dies.</summary>
        public const int StartingPluck = 100;

        /// <summary>Movement budget, refilled every round.</summary>
        public const int StartingStamina = 100;

        /// <summary>Moles per platoon, at every player count.</summary>
        public const int MolesPerPlatoon = 4;

        /// <summary>
        /// Body radius in metres. Six cells exactly, so the collision footprint lands on
        /// cell boundaries rather than straddling them differently at different positions.
        /// </summary>
        public static Fix64 Radius => Fix64.Ratio(6, WorldScale.CellsPerMetre);

        /// <summary>
        /// One speed, everywhere, in every material. Dirt is not slower, it is dearer,
        /// and that single rule is what the stamina economy exists to express.
        /// </summary>
        /// <remarks>
        /// Four rather than five, which is a deliberate step toward the weight a turn has in the
        /// games this one is answering. Five is eighteen kilometres an hour and read as a scurry.
        ///
        /// It costs reach, and that is the thing to watch rather than the speed itself. A turn on
        /// open ground is bounded by the round and not by the legs: eight seconds at five covered
        /// forty metres of a sixty metre map, where a hundred stamina at one and a half a metre
        /// would have allowed sixty-seven. At four it is thirty-two. Digging is bounded the other
        /// way round, by what it costs, so a slower walk does not make a tunnel dearer, only longer
        /// to cut, which spends round rather than puff.
        /// </remarks>
        public static Fix64 WalkSpeed => Fix64.FromInt(4);

        /// <summary>
        /// How high a lip the mole steps over rather than digging through. Without this a
        /// mole walking a gentle slope would carve a trench along it, and the surface of
        /// the map would dissolve after a couple of rounds.
        /// </summary>
        public static Fix64 StepHeight => Fix64.Ratio(8, WorldScale.CellsPerMetre);

        /// <summary>
        /// How hard a hop pushes off, in metres a second.
        /// </summary>
        /// <remarks>
        /// Lived privately in <see cref="MoleMatch"/> while resolution was the only thing that
        /// jumped. The planning preview jumps too now, and two copies of a number that has to agree
        /// is how a preview starts lying about what a plan will do.
        /// </remarks>
        public static Fix64 HopSpeed => Fix64.FromInt(9);

        /// <summary>
        /// How hard a mole can push sideways while off the ground, in metres a second squared.
        /// </summary>
        /// <remarks>
        /// A jump used to be a ballistic arc nobody could influence once it started, which made the
        /// hop a commitment rather than a move: you could not clear a gap you had misjudged by a
        /// foot, and you could not aim a jump at anything.
        ///
        /// Seven, against a walk of four, gets a mole to walking pace sideways in a little over half
        /// a second of air. Twelve did it in four tenths, which let a hop be re-aimed most of the
        /// way through and made the arc feel like a suggestion. The game this one is answering has
        /// no air control at all, so the direction of travel is toward less of it: enough to save a
        /// gap misjudged by a foot, not enough to turn a jump into flight.
        /// </remarks>
        public static Fix64 AirControl => Fix64.FromInt(7);

        /// <summary>
        /// How much of the escape direction has to point upward for a contact to count as a floor.
        /// </summary>
        /// <remarks>
        /// Which decides whether hitting something in mid-air lands the mole or starts it digging.
        /// Solid below pushes a body upward, so an escape that is mostly up is a floor and gets the
        /// old settle-or-bounce; anything else is a ceiling or a wall and gets dug into. Six tenths
        /// puts the boundary at about fifty degrees, so a steep bank counts as a wall and a shallow
        /// one counts as ground, which is the same place the walking solver draws the line.
        /// </remarks>
        public static Fix64 FloorContact => Fix64.Ratio(6, 10);

        /// <summary>
        /// How far below its feet the mole looks for ground before deciding it is falling.
        /// Also how far it drops to follow a slope down without going ballistic.
        /// </summary>
        public static Fix64 GroundSnap => Fix64.Ratio(5, WorldScale.CellsPerMetre);

        // ---- Physics ----------------------------------------------------------------

        /// <summary>
        /// Rather brisker than the real thing. Earth gravity makes an artillery game feel
        /// like it is played underwater; this is tuned for the arc of a lobbed clod.
        /// </summary>
        public static Fix64 Gravity => Fix64.FromInt(18);

        /// <summary>Speed cap, so a chain of blasts cannot fling a mole across the map in a tick.</summary>
        public static Fix64 TerminalSpeed => Fix64.FromInt(45);

        /// <summary>How much of its speed a mole keeps when it bounces off terrain.</summary>
        public static Fix64 Restitution => Fix64.Ratio(3, 10);

        /// <summary>Below this speed a bouncing mole is considered to have settled.</summary>
        public static Fix64 SettleSpeed => Fix64.Ratio(3, 2);

        /// <summary>
        /// How hard a mole may hit the ground for nothing, in metres a second.
        /// </summary>
        /// <remarks>
        /// Fourteen is a fall of about five and a half metres at this gravity, which is seven
        /// mole heights. Chosen so the two things a player does on purpose are always free: a hop
        /// leaves at nine metres a second and comes back at nine, and walking off any ledge worth
        /// walking off lands well inside it. What is left is the drop nobody chose, off the roof
        /// of a cave or out of somebody else's blast, which is exactly what should hurt.
        /// </remarks>
        public static Fix64 SafeLandingSpeed => Fix64.FromInt(14);

        /// <summary>Pluck a landing costs for each metre a second past the safe speed.</summary>
        public const int FallDamagePerSpeed = 2;

        /// <summary>
        /// The most one landing can cost, however far the drop was.
        /// </summary>
        /// <remarks>
        /// Half a mole's pluck. Terminal speed is forty-five metres a second, which uncapped would
        /// be sixty-two, and a fall that is very nearly a knockout on its own would make blast
        /// knockback the strongest weapon in the game by a distance: everything that throws a mole
        /// would be throwing it at the ground as well.
        /// </remarks>
        public const int WorstFallDamage = 50;

        /// <summary>
        /// Movement is resolved in fractions of a tick so a fast mole cannot pass through
        /// a thin wall. Four is enough at walking pace; ballistic motion scales this up
        /// with speed.
        /// </summary>
        public const int MinimumSubsteps = 4;

        /// <summary>Longest distance a single substep may cover, in metres.</summary>
        public static Fix64 MaxSubstepDistance => Fix64.Ratio(3, WorldScale.CellsPerMetre);

        // ---- Lava -------------------------------------------------------------------

        /// <summary>The round lava arrives, every match.</summary>
        public const int BoilingPointRound = 8;

        /// <summary>How far the line climbs each round afterwards, in metres.</summary>
        public static Fix64 LavaRisePerRound => Fix64.FromInt(3);

        /// <summary>How far each side creeps in per round, once the line passes halfway.</summary>
        public static Fix64 LavaClosePerRound => Fix64.FromInt(4);

        /// <summary>Cost of touching lava. The third touch is the knockout instead.</summary>
        public const int LavaBounceDamage = 10;

        /// <summary>Touches a mole survives. The third landing ends its match.</summary>
        public const int LavaStrikesAllowed = 2;

        /// <summary>Upward kick a lava bounce imparts.</summary>
        public static Fix64 LavaBounceSpeed => Fix64.FromInt(14);

        // ---- The rest of the arsenal ------------------------------------------------

        /// <summary>How close the Big Whack needs its target to be. Body lengths, not metres.</summary>
        public static Fix64 MeleeReach => Radius * Fix64.FromInt(3);

        /// <summary>Upward speed a Fracking gusher, or a capped vent, imparts.</summary>
        public static Fix64 GusherSpeed => Fix64.FromInt(26);

        /// <summary>Half-width of the column a gusher throws things up.</summary>
        public static Fix64 GusherHalfWidth => Fix64.FromInt(2);

        /// <summary>
        /// How long a girder is.
        /// </summary>
        /// <remarks>
        /// Four metres, which is a little over five mole widths: long enough to bridge a gap worth
        /// bridging and short enough that it cannot span a chasm the map meant to be a chasm.
        /// </remarks>
        public static Fix64 GirderLength => Fix64.FromInt(4);

        /// <summary>How far a Tunnel Torpedo drills in one turn, wound up to full.</summary>
        public static Fix64 TorpedoRange => Fix64.FromInt(12);

        /// <summary>
        /// How far it drills with barely any wind-up at all.
        /// </summary>
        /// <remarks>
        /// Three metres is four mole widths: through a wall and out the other side, and no further.
        /// It is deliberately not zero. A drill is one of a turn's two allowances and the mole is
        /// committed to the tunnel the moment it starts, so a wind-up released a fraction early
        /// should cost distance, not the whole use.
        /// </remarks>
        public static Fix64 TorpedoShortestRange => Fix64.FromInt(3);

        /// <summary>
        /// How far a torpedo cuts for a given wind-up.
        /// </summary>
        /// <remarks>
        /// The drill used to take a direction and nothing else, and its whole twelve metres went in
        /// whatever the player did: aiming it at a wall two metres away spent the same allowance as
        /// crossing a chasm, and surfacing in the middle of somebody else's platoon was something
        /// that happened to you rather than something you chose. Charging it for distance makes the
        /// far end of the tunnel the thing being aimed at, which is what a player is thinking about
        /// anyway.
        ///
        /// Linear between the two ranges, because the gauge sweeps linearly and a player reading it
        /// is reading a distance. Bedrock still stops it early, so this is what it will cut through
        /// air and soil rather than a promise about where the mole ends up.
        /// </remarks>
        public static Fix64 TorpedoRangeFor(byte power) =>
            TorpedoShortestRange
            + ((TorpedoRange - TorpedoShortestRange) * Fix64.Ratio(power, byte.MaxValue));

        /// <summary>
        /// How fast it drills, in metres per second.
        /// </summary>
        /// <remarks>
        /// The drill used to happen inside the tick it was ordered on: the whole twelve metres of
        /// tunnel appeared at once and the mole was simply somewhere else, which is not something a
        /// player can watch, learn from or be scared of. Spread over time it is about four fifths of
        /// a second, which is long enough to see and short enough to still feel like a torpedo.
        ///
        /// Faster than a hop and much faster than a walk, because it is the one thing in the game
        /// that goes through packed soil at speed.
        /// </remarks>
        public static Fix64 TorpedoSpeed => Fix64.FromInt(14);

        /// <summary>
        /// Rounds before a placed trap becomes dangerous. One, so it is visible as a
        /// suspicious mound for a whole round before anybody can be caught by it.
        /// </summary>
        public const int TrapArmDelay = 1;

        /// <summary>How far above the ground something dropped from the sky starts.</summary>
        public static Fix64 SkyDropHeight => Fix64.FromInt(30);

        /// <summary>
        /// How far away a mole can call something down at full power. The aim names a
        /// direction and the power names a distance, so the pair picks a spot on the map.
        /// </summary>
        public static Fix64 SkyTargetRange => Fix64.FromInt(40);

        /// <summary>Sideways spread between the sacks of a Special Delivery.</summary>
        public static Fix64 SkySpread => Fix64.FromInt(4);

        /// <summary>Sideways speed given to each piece of a cluster when it splits.</summary>
        public static Fix64 ClusterSpread => Fix64.FromInt(6);

        // ---- Wind -------------------------------------------------------------------

        /// <summary>Strongest wind either way, in metres per second.</summary>
        public static Fix64 MaxWindSpeed => Fix64.FromInt(8);

        /// <summary>
        /// Turns a wind speed into a sideways acceleration on the things that ride it.
        /// </summary>
        /// <remarks>
        /// Wind is quoted in metres per second because that is what the drifting seeds
        /// show, but a projectile feels it as a push rather than as a current. Treating it
        /// as linear drag toward the wind speed gives 2.4 m/s^2 at full strength, which
        /// bends a full-length flight by about the five metres the design asks for.
        /// </remarks>
        public static Fix64 WindDragFactor => Fix64.Ratio(3, 10);
    }
}
