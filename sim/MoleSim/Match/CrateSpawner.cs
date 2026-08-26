using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// Decides where the next crates come down.
    /// </summary>
    /// <remarks>
    /// The spawn is never neutral ground by accident. The design asks for crates that land
    /// centrally and as near equidistant from every platoon as the terrain allows, because
    /// that is the whole mechanism by which the game encourages a fight without ever
    /// nagging anybody: it just keeps putting the good things somewhere everybody has to
    /// come out to reach.
    ///
    /// So a candidate is scored on fairness first, by how far apart the nearest mole of
    /// each platoon would be from it, and on centrality second. Nobody gets a crate that
    /// lands in their own trench.
    /// </remarks>
    public static class CrateSpawner
    {
        /// <summary>How far apart two crates in the same round must be.</summary>
        private static Fix64 MinimumSeparation => Fix64.FromInt(20);

        /// <summary>How deep a crate embeds itself, so the last stretch has to be dug.</summary>
        private static Fix64 EmbedDepth => Fix64.One;

        /// <summary>Crates in a round: one for a duel, two for a crowd.</summary>
        public static int CountFor(int playerCount) => playerCount >= 3 ? 2 : 1;

        /// <summary>
        /// Picks landing spots for the coming round, and rolls what is in them.
        /// </summary>
        public static List<Crate> Telegraph(
            TerrainGrid terrain, IReadOnlyList<Mole> moles, int playerCount, MatchRng rng)
        {
            List<Crate> crates = new List<Crate>();
            int wanted = CountFor(playerCount);

            // Sampled every metre across the middle three-fifths of the map, which keeps
            // crates out of the corners where a platoon starts.
            int step = WorldScale.CellsPerMetre;
            int from = terrain.Width / 5;
            int to = terrain.Width - (terrain.Width / 5);

            for (int index = 0; index < wanted; index++)
            {
                Vec2? best = null;
                Fix64 bestScore = Fix64.MaxValue;

                for (int cellX = from; cellX < to; cellX += step)
                {
                    Vec2? candidate = LandingSpot(terrain, cellX);

                    if (candidate is null)
                    {
                        continue;
                    }

                    if (TooCloseToAnother(candidate.Value, crates))
                    {
                        continue;
                    }

                    Fix64 score = Score(candidate.Value, terrain, moles, playerCount);

                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    best = candidate;
                }

                if (best is null)
                {
                    break;
                }

                crates.Add(new Crate(best.Value, RollContents(rng)));
            }

            return crates;
        }

        /// <summary>
        /// Where a crate dropped down this column would come to rest: on the surface, then
        /// a metre into it, so somebody has to dig the last stretch rather than stroll up.
        /// </summary>
        private static Vec2? LandingSpot(TerrainGrid terrain, int cellX)
        {
            for (int cellY = 0; cellY < terrain.Height; cellY++)
            {
                Material material = terrain[cellX, cellY];

                if (!MaterialTable.IsSolid(material))
                {
                    continue;
                }

                // Landing on bedrock would leave a crate nobody could dig out.
                if (!MaterialTable.IsDiggable(material))
                {
                    return null;
                }

                return new Vec2(
                    WorldScale.ToCentreMetres(cellX),
                    WorldScale.ToMetres(cellY) + EmbedDepth);
            }

            return null;
        }

        private static bool TooCloseToAnother(Vec2 candidate, List<Crate> placed)
        {
            foreach (Crate crate in placed)
            {
                if (Vec2.Distance(crate.Position, candidate) < MinimumSeparation)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Lower is better. Fairness dominates: the gap between the nearest and furthest
        /// platoon is what decides it, with distance from the middle of the map only
        /// breaking ties.
        /// </summary>
        private static Fix64 Score(
            Vec2 candidate, TerrainGrid terrain, IReadOnlyList<Mole> moles, int playerCount)
        {
            Fix64 nearest = Fix64.MaxValue;
            Fix64 furthest = Fix64.Zero;
            int platoonsCounted = 0;

            for (int seat = 0; seat < playerCount; seat++)
            {
                Fix64 closest = Fix64.MaxValue;

                foreach (Mole mole in moles)
                {
                    if (mole.Seat != seat || mole.IsOffDuty)
                    {
                        continue;
                    }

                    closest = Fix64.Min(closest, Vec2.Distance(mole.Position, candidate));
                }

                if (closest == Fix64.MaxValue)
                {
                    continue;
                }

                platoonsCounted++;
                nearest = Fix64.Min(nearest, closest);
                furthest = Fix64.Max(furthest, closest);
            }

            if (platoonsCounted == 0)
            {
                return Fix64.MaxValue;
            }

            Fix64 unfairness = furthest - nearest;
            Fix64 middle = WorldScale.ToMetres(terrain.Width) / Fix64.FromInt(2);
            Fix64 offCentre = Fix64.Abs(candidate.X - middle);

            // Centrality is worth a tenth of fairness, so it only settles ties.
            return unfairness + (offCentre / Fix64.FromInt(10));
        }

        private static CrateContents RollContents(MatchRng rng)
        {
            // Weighted so grubs are common and the rarities are rare, which is what makes
            // a telegraphed crate worth arguing over rather than worth ignoring.
            int roll = rng.NextInt(100);

            if (roll < 30)
            {
                return new CrateContents(CrateKind.Grub, WeaponId.None, 25);
            }

            if (roll < 45)
            {
                return new CrateContents(CrateKind.ResetToken, WeaponId.None, 1);
            }

            if (roll < 60)
            {
                return new CrateContents(CrateKind.Dynamite, WeaponId.BoomBeets, 2);
            }

            if (roll < 96)
            {
                WeaponId[] limited =
                {
                    WeaponId.AcornMortar,
                    WeaponId.Fracking,
                    WeaponId.BigWhack,
                    WeaponId.SnapTrap,
                    WeaponId.RootSnare,
                    WeaponId.TunnelTorpedo,
                    WeaponId.PowerClaws,
                    WeaponId.Sandbag,
                    WeaponId.GeyserCap,
                };

                return new CrateContents(
                    CrateKind.Weapon, limited[rng.NextIndex(limited.Length)], 1);
            }

            // The crate rarities, four times in a hundred between them.
            return new CrateContents(
                CrateKind.Weapon,
                rng.NextBool() ? WeaponId.MolyHandGrenade : WeaponId.GnomeMercy,
                1);
        }
    }
}
