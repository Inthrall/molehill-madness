using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// What the tools do to the world, in one place so the round and the preview cannot disagree.
    /// </summary>
    /// <remarks>
    /// Every one of these has to happen twice: once when the round resolves, and once on the
    /// planning screen so the player can see what they are buying. That is two call sites for one
    /// rule, and two call sites for one rule is how the Tunnel Torpedo ended up working perfectly in
    /// the round and not at all in the ghost.
    ///
    /// Only the tools that change the world or the mole are here. Anything that throws something is
    /// resolved by the projectile solver and is not previewed at all, which is consistent: no thrown
    /// weapon shows its flight before the round runs.
    /// </remarks>
    internal static class Tools
    {
        /// <summary>
        /// Drops a sandbag under a mole's feet.
        /// </summary>
        /// <remarks>
        /// Two cells below the centre, so it lands under the mole rather than around it, and as
        /// loose soil so anybody can dig back out of it cheaply.
        /// </remarks>
        public static void DropSandbag(TerrainGrid terrain, Vec2 at)
        {
            terrain.DepositCircle(
                WorldScale.ToCell(at.X),
                WorldScale.ToCell(at.Y) + 2,
                Fix64.FloorToInt(WeaponTable.Of(WeaponId.Sandbag).BlastRadius / WorldScale.CellSize),
                Material.LooseSoil);
        }
    }
}
