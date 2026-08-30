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
        /// Drops a sandbag around a mole, leaving the mole standing in it.
        /// </summary>
        /// <remarks>
        /// Centred on the mole rather than below it, and then the body is carved back out, so what
        /// is left is a mound with a mole-shaped pocket in it. Both halves of that are needed. A
        /// deposit skips ground that is already solid, so a bag centred below the feet put all of
        /// its material in the air above and around the mole instead: it entombed whoever dropped
        /// it and built nothing that could be stood on, which is why it looked like the bag was
        /// doing nothing at all.
        ///
        /// Loose soil, so anybody can dig back out of it cheaply, including the mole in the middle.
        /// </remarks>
        public static void DropSandbag(TerrainGrid terrain, Vec2 at)
        {
            terrain.DepositCircle(
                WorldScale.ToCell(at.X),
                WorldScale.ToCell(at.Y),
                Fix64.FloorToInt(WeaponTable.Of(WeaponId.Sandbag).BlastRadius / WorldScale.CellSize),
                Material.LooseSoil);

            TerrainQuery.CarveBody(terrain, at, MatchSettings.Radius);
        }

        /// <summary>
        /// Lays a girder: a straight plank out from the mole along the aim.
        /// </summary>
        /// <remarks>
        /// Started clear of the body and the body carved back out afterwards, for the same reason the
        /// sandbag needs both: a deposit skips ground that is already solid, so a plank begun at the
        /// mole's centre would wall the mole in at one end of its own bridge.
        ///
        /// Stepped a cell at a time rather than drawn as a rectangle, because it has to follow a
        /// diagonal and a rectangle cannot. Loose soil like the sandbag, so it can be dug through:
        /// something permanent and indestructible would let one mole seal a cave for the rest of the
        /// match, and nothing else in the arsenal can do that.
        /// </remarks>
        public static void LayGirder(TerrainGrid terrain, Vec2 at, Vec2 aim)
        {
            if (aim.LengthSquared() == Fix64.Zero)
            {
                return;
            }

            Vec2 along = aim.Normalised();
            Fix64 stride = WorldScale.CellSize;
            Fix64 laid = Fix64.Zero;

            int thickness = Fix64.FloorToInt(
                WeaponTable.Of(WeaponId.Girder).BlastRadius / WorldScale.CellSize);

            if (thickness < 1)
            {
                thickness = 1;
            }

            while (laid < MatchSettings.GirderLength)
            {
                laid += stride;
                Vec2 point = at + (along * laid);

                terrain.DepositCircle(
                    WorldScale.ToCell(point.X),
                    WorldScale.ToCell(point.Y),
                    thickness,
                    Material.LooseSoil);
            }

            TerrainQuery.CarveBody(terrain, at, MatchSettings.Radius);
        }
    }
}
