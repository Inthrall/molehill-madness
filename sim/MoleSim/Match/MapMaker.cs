using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// Builds playable ground procedurally, until real maps come from baked art.
    /// </summary>
    /// <remarks>
    /// Deliberately whole-number arithmetic throughout, so the same seed produces the same
    /// hillside on every platform. The shipped game gets its maps from an artist through
    /// the map baker; this exists so that the simulation can be exercised long before
    /// there is any art at all.
    /// </remarks>
    public static class MapMaker
    {
        /// <summary>A rolling field with strata beneath and bedrock along the floor.</summary>
        public static TerrainGrid Field(int widthCells, int heightCells, ulong seed)
        {
            TerrainGrid grid = new TerrainGrid(widthCells, heightCells);
            MatchRng rng = new MatchRng(seed);

            int baseSurface = heightCells / 4;

            // Three offset waves, which is enough to look unplanned without needing noise.
            int firstPeriod = 90 + rng.NextInt(70);
            int secondPeriod = 200 + rng.NextInt(160);
            int firstHeight = 8 + rng.NextInt(14);
            int secondHeight = 14 + rng.NextInt(22);
            int firstPhase = rng.NextInt(firstPeriod);
            int secondPhase = rng.NextInt(secondPeriod);

            for (int x = 0; x < widthCells; x++)
            {
                int rise =
                    Triangle(x + firstPhase, firstPeriod, firstHeight)
                    + Triangle(x + secondPhase, secondPeriod, secondHeight);

                int top = baseSurface - rise;

                grid.FillRectangle(x, top, 1, 3, Material.Turf);
                grid.FillRectangle(x, top + 3, 1, 34, Material.LooseSoil);
                grid.FillRectangle(x, top + 37, 1, heightCells - top - 37, Material.PackedSoil);
            }

            // A hard layer to make certain routes cost more than they are worth, and
            // bedrock along the very bottom so nothing digs out of the world.
            grid.FillRectangle(0, heightCells - (heightCells / 5), widthCells, heightCells / 14, Material.RootMat);
            grid.FillRectangle(0, heightCells - 10, widthCells, 10, Material.Bedrock);

            return grid;
        }

        /// <summary>
        /// Spawn points spread along the surface, one band per platoon so nobody starts
        /// inside somebody else's opening move.
        /// </summary>
        public static Vec2[] SpawnPoints(TerrainGrid grid, int playerCount, int molesPerPlatoon)
        {
            Vec2[] points = new Vec2[playerCount * molesPerPlatoon];
            int total = points.Length;

            // Leave a margin at each edge, then space everybody evenly. Platoons interleave
            // rather than clustering, which keeps anybody from being cornered at the start.
            int margin = grid.Width / 12;
            int span = grid.Width - (margin * 2);

            for (int slot = 0; slot < total; slot++)
            {
                int cellX = margin + (span * slot / (total > 1 ? total - 1 : 1));
                points[slot] = new Vec2(
                    WorldScale.ToCentreMetres(cellX),
                    SurfaceHeight(grid, cellX) - MatchSettings.Radius - WorldScale.CellSize);
            }

            return points;
        }

        /// <summary>World height of the first solid cell in a column.</summary>
        public static Fix64 SurfaceHeight(TerrainGrid grid, int cellX)
        {
            for (int cellY = 0; cellY < grid.Height; cellY++)
            {
                if (MaterialTable.IsSolid(grid[cellX, cellY]))
                {
                    return WorldScale.ToMetres(cellY);
                }
            }

            return WorldScale.ToMetres(grid.Height);
        }

        /// <summary>A triangle wave, so the ground rolls without any transcendental maths.</summary>
        private static int Triangle(int position, int period, int amplitude)
        {
            int phase = position % period;
            int half = period / 2;

            int climb = phase < half
                ? phase
                : period - phase;

            return climb * amplitude / half;
        }
    }
}
