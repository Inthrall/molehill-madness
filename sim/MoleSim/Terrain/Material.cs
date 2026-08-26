using MoleSim.Numerics;

namespace MoleSim.Terrain
{
    /// <summary>
    /// What a terrain cell is made of. One byte per cell, and the whole movement economy
    /// is a cost looked up on this value.
    /// </summary>
    public enum Material : byte
    {
        /// <summary>
        /// Open space. Carving turns any diggable material into this, so a tunnel and the
        /// sky above the grass are the same thing to the movement rules, which is exactly
        /// why re-using somebody else's tunnel is as cheap as walking.
        /// </summary>
        Air = 0,

        /// <summary>The thin grass layer along the surface.</summary>
        Turf = 1,

        /// <summary>Flowerbeds, molehills, spoil heaps, anything freshly disturbed.</summary>
        LooseSoil = 2,

        /// <summary>The default underground.</summary>
        PackedSoil = 3,

        /// <summary>Placed deliberately to make certain routes cost more than they are worth.</summary>
        RootMat = 4,

        /// <summary>The only hard wall in the game. Never carves, never digs.</summary>
        Bedrock = 5,
    }

    /// <summary>
    /// The movement economy, in one place. Retuning the pacing of the whole game means
    /// editing these six numbers.
    /// </summary>
    public static class MaterialTable
    {
        /// <summary>Number of distinct materials, for table sizing.</summary>
        public const int Count = 6;

        private static readonly Fix64[] StaminaPerMetre = BuildCostTable();

        private static readonly bool[] Solid = { false, true, true, true, true, true };

        private static readonly bool[] Diggable = { false, true, true, true, true, false };

        /// <summary>
        /// Stamina spent per metre travelled through this material. Air and turf are the
        /// cheap going; bedrock is unreachable so its entry is never read.
        /// </summary>
        public static Fix64 CostPerMetre(Material material) =>
            StaminaPerMetre[(int)material];

        /// <summary>Whether a mole collides with this material.</summary>
        public static bool IsSolid(Material material) => Solid[(int)material];

        /// <summary>Whether this material can be dug through or blown up.</summary>
        public static bool IsDiggable(Material material) => Diggable[(int)material];

        /// <summary>Whether a route may pass through this material at any price.</summary>
        public static bool IsPassable(Material material) => material != Material.Bedrock;

        private static Fix64[] BuildCostTable()
        {
            Fix64[] table = new Fix64[Count];

            // Design defaults. Chosen, not proven: the playtests move them.
            table[(int)Material.Air] = Fix64.Ratio(3, 2);         // 1.5
            table[(int)Material.Turf] = Fix64.Ratio(3, 2);        // 1.5
            table[(int)Material.LooseSoil] = Fix64.FromInt(4);
            table[(int)Material.PackedSoil] = Fix64.FromInt(7);
            table[(int)Material.RootMat] = Fix64.FromInt(12);

            // Bedrock is impassable, so no route ever spends this. A deliberately absurd
            // number rather than zero, so a bug that reads it shows up immediately as a
            // route nobody can afford instead of a free tunnel through the map floor.
            table[(int)Material.Bedrock] = Fix64.FromInt(1_000_000);

            return table;
        }
    }
}
