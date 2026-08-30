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
        /// Where a crate dropped down this column comes to rest: on a floor a mole could stand on,
        /// which is the surface or a cave.
        /// </summary>
        /// <remarks>
        /// It used to be the first solid cell and then a metre into it, so the last stretch had to be
        /// dug rather than strolled up. Two things were wrong with that once the caves grew. A buried
        /// crate is invisible, and with a parachute drawn over it, it read as a crate floating in
        /// solid rock. And the surface was the only place one could land, so the half of the map that
        /// is now chambers and passages never got a crate in it, which is the same objection as
        /// everybody spawning on the grass.
        ///
        /// So it sits on top of a ledge, found by the same scan the spawns use: a run of clear cells
        /// with something solid under it. One definition of somewhere you can stand, shared, because
        /// a crate that lands where no mole can reach is worse than no crate.
        ///
        /// Which ledge comes off the terrain hash rather than a generator, exactly as the spawns do,
        /// so nothing here disturbs the map's own random sequence.
        ///
        /// Not uniform across the ledges, though, which was the first attempt and measured badly:
        /// seventeen crates of twenty-three went underground, because a column has one surface and
        /// several chambers under it and a fair die does not care which is which. One bit of the hash
        /// chooses between the open air and the burrows instead, which measures at an even split.
        ///
        /// How deep the underground half lands is left to the map. Trimming the pick to the
        /// shallowest few chambers was tried and dropped: with fourteen cells of headroom demanded of
        /// a ledge, a column has one to four chambers in it and not the seven that would make such a
        /// rule bite, so it changed nothing it claimed to. Where the crates do land deep, it is
        /// because the column's only chamber is deep, and the caves connect now, so getting there is
        /// more a matter of finding the way in than of digging twelve metres.
        /// </remarks>
        private static Vec2? LandingSpot(TerrainGrid terrain, int cellX)
        {
            int[] ledges = new int[MapMaker.MostLedgesConsidered];
            int found = MapMaker.Ledges(terrain, cellX, ledges);

            if (found == 0)
            {
                return null;
            }

            ulong pick = terrain.Hash ^ ((ulong)cellX * 0x9E3779B97F4A7C15UL);
            pick ^= pick >> 29;
            pick *= 0xBF58476D1CE4E5B9UL;
            pick ^= pick >> 32;

            // The topmost ledge is the surface, and everything below it is a chamber. One bit of the
            // hash decides between the two, and the rest picks which chamber.
            int caves = found - 1;
            int cellY = (pick & 1UL) == 0UL || caves == 0
                ? ledges[0]
                : ledges[1 + (int)((pick >> 1) % (ulong)caves)];

            // Landing on bedrock would leave a crate nobody could dig out. The ledge scan already
            // stops above the world's floor, so this is the root mat and anything else undiggable.
            if (!MaterialTable.IsDiggable(terrain[cellX, cellY]))
            {
                return null;
            }

            // On top of the floor rather than in it, sitting where a mole would stand.
            return new Vec2(
                WorldScale.ToCentreMetres(cellX),
                WorldScale.ToMetres(cellY) - MatchSettings.Radius);
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

        /// <summary>
        /// Everything a platoon holds a finite number of, which an ordinary crate can replace.
        /// </summary>
        /// <remarks>
        /// A weapon that can run out and never be replaced would be a one-match curiosity, so
        /// anything with a finite starting stock has to be reachable from here. Public because
        /// that is a property worth asserting rather than hoping for.
        /// </remarks>
        public static readonly WeaponId[] Restockable =
        {
            WeaponId.BeetleLauncher,
            WeaponId.AcornMortar,
            WeaponId.Fracking,
            WeaponId.BigWhack,
            WeaponId.SnapTrap,
            WeaponId.RootSnare,
            WeaponId.TunnelTorpedo,
            WeaponId.PowerClaws,
            WeaponId.Sandbag,
            WeaponId.GeyserCap,
            WeaponId.Girder,
            WeaponId.SpecialDelivery,
        };

        /// <summary>The two nobody starts with and only a crate can provide.</summary>
        public static readonly WeaponId[] Rarities =
        {
            WeaponId.MolyHandGrenade,
            WeaponId.GnomeMercy,
        };

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
                return new CrateContents(
                    CrateKind.Weapon, Restockable[rng.NextIndex(Restockable.Length)], 1);
            }

            // The crate rarities, four times in a hundred between them.
            return new CrateContents(
                CrateKind.Weapon, Rarities[rng.NextBool() ? 0 : 1], 1);
        }
    }
}
