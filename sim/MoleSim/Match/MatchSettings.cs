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
        public static Fix64 WalkSpeed => Fix64.FromInt(5);

        /// <summary>
        /// How high a lip the mole steps over rather than digging through. Without this a
        /// mole walking a gentle slope would carve a trench along it, and the surface of
        /// the map would dissolve after a couple of rounds.
        /// </summary>
        public static Fix64 StepHeight => Fix64.Ratio(8, WorldScale.CellsPerMetre);

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
