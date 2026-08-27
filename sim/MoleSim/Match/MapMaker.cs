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
        /// <summary>A third of sky over two thirds of caved-out ground, with bedrock along the floor.</summary>
        public static TerrainGrid Field(int widthCells, int heightCells, ulong seed)
        {
            TerrainGrid grid = new TerrainGrid(widthCells, heightCells);
            MatchRng rng = new MatchRng(seed);

            int[] surface = new int[widthCells];

            // A third sky, two thirds ground. The surface used to sit a quarter of the way down,
            // which left most of the map as strata nobody ever reached; putting it lower makes the
            // underground the larger half of the playing field rather than a basement.
            int baseSurface = heightCells / 3;

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
                surface[x] = top;

                grid.FillRectangle(x, top, 1, 3, Material.Turf);
                grid.FillRectangle(x, top + 3, 1, 34, Material.LooseSoil);
                grid.FillRectangle(x, top + 37, 1, heightCells - top - 37, Material.PackedSoil);
            }

            // A hard layer to make certain routes cost more than they are worth, and
            // bedrock along the very bottom so nothing digs out of the world.
            grid.FillRectangle(0, heightCells - (heightCells / 5), widthCells, heightCells / 14, Material.RootMat);
            grid.FillRectangle(0, heightCells - 10, widthCells, 10, Material.Bedrock);

            // Last, so the strata above do not fill them back in.
            Caves(grid, rng, surface, heightCells);

            return grid;
        }

        /// <summary>
        /// Hollows the underground out into caves.
        /// </summary>
        /// <remarks>
        /// A cellular automaton rather than the wandering tunnels this replaces. Tunnels gave tubes
        /// of a chosen width following a chosen heading, which reads as something bored on purpose
        /// however much it wobbles. Seeding noise and smoothing it repeatedly gives chambers of no
        /// particular size joined by passages of no particular width, which is what a cave looks
        /// like, and it comes out of two numbers rather than a route-planning routine.
        ///
        /// The rule is the usual one: a cell stays solid if most of what surrounds it is solid.
        /// Anything outside the region counts as solid, which seals the roof and the floor for free
        /// and is why there is no separate check keeping caves away from either. That matters more
        /// than it sounds, because a spawn stands on the first solid cell in its column, so a cave
        /// breaking the surface would quietly move where everybody starts.
        ///
        /// Whole-number arithmetic and one generator, so the same seed hollows out the same cave on
        /// every platform.
        /// </remarks>
        private static void Caves(TerrainGrid grid, MatchRng rng, int[] surface, int heightCells)
        {
            int deepestSurface = 0;

            foreach (int top in surface)
            {
                if (top > deepestSurface)
                {
                    deepestSurface = top;
                }
            }

            int from = deepestSurface + RoofCells;
            int to = heightCells - (heightCells / 5) - 1;
            int rows = to - from + 1;

            if (rows < ShallowestWorthCaving)
            {
                return;
            }

            int width = grid.Width;
            bool[] air = new bool[width * rows];
            bool[] settled = new bool[air.Length];

            // Value noise: a weight every few cells, interpolated between, then cut at a level.
            //
            // This took three goes. A weight per cell gave ground like pumice, because the smoothing
            // pass can remove speckle but cannot grow a chamber, so every hole stayed a cell or two
            // across and a mole twelve cells wide could not get into any of them. Deciding one bit
            // per block fixed the scale and introduced a worse problem: the blocks survive smoothing
            // intact, so the result was a right-angled maze on an eight-cell grid, which is the one
            // thing a cave never looks like. Interpolating between the weights instead of stepping
            // between them keeps the scale and loses the grid.
            int blockColumns = ((width - 1) / SeedBlock) + 2;
            int blockRows = ((rows - 1) / SeedBlock) + 2;
            int[] weights = new int[blockColumns * blockRows];

            for (int weight = 0; weight < weights.Length; weight++)
            {
                weights[weight] = rng.NextInt(WeightRange);
            }

            for (int row = 0; row < rows; row++)
            {
                int blockRow = row / SeedBlock;
                int downward = row % SeedBlock;

                for (int column = 0; column < width; column++)
                {
                    int blockColumn = column / SeedBlock;
                    int across = column % SeedBlock;
                    int corner = (blockRow * blockColumns) + blockColumn;

                    int above =
                        (weights[corner] * (SeedBlock - across))
                        + (weights[corner + 1] * across);
                    int below =
                        (weights[corner + blockColumns] * (SeedBlock - across))
                        + (weights[corner + blockColumns + 1] * across);

                    int value =
                        ((above * (SeedBlock - downward)) + (below * downward))
                        / (SeedBlock * SeedBlock);

                    air[(row * width) + column] = value < HollowBelow;
                }
            }

            for (int pass = 0; pass < SmoothingPasses; pass++)
            {
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        settled[(row * width) + column] =
                            SolidHereabouts(air, width, rows, column, row) < SolidToFillIn;
                    }
                }

                bool[] swap = air;
                air = settled;
                settled = swap;
            }

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    if (air[(row * width) + column])
                    {
                        grid.Set(column, from + row, Material.Air);
                    }
                }
            }
        }

        /// <summary>
        /// How many of the nine cells in a cell's own neighbourhood are solid, itself included, and
        /// counting anything off the edge of the region as solid so the caves seal themselves in.
        /// </summary>
        /// <remarks>
        /// Its own square counts, and that is the whole difference between a cave and gravel. The
        /// first attempt looked at the eight neighbours only and filled a cell in when five of them
        /// were solid, which sounds like a majority and is not one: a cell in ground that is half
        /// hollow has four solid neighbours on average, just under the threshold, so every pass
        /// eroded a little more and five passes left rubble floating in air. Nine cells with a
        /// threshold of five is an actual majority, which is stable: it wipes out speckle and leaves
        /// whatever was locally dominant alone.
        /// </remarks>
        private static int SolidHereabouts(bool[] air, int width, int rows, int column, int row)
        {
            int solid = 0;

            for (int downward = -1; downward <= 1; downward++)
            {
                for (int across = -1; across <= 1; across++)
                {
                    int neighbourColumn = column + across;
                    int neighbourRow = row + downward;

                    bool outside = neighbourColumn < 0 || neighbourColumn >= width
                        || neighbourRow < 0 || neighbourRow >= rows;

                    if (outside || !air[(neighbourRow * width) + neighbourColumn])
                    {
                        solid++;
                    }
                }
            }

            return solid;
        }

        /// <summary>
        /// Solid ground kept between the caves and the daylight, in cells. Measured from the
        /// deepest the surface gets anywhere, so the thinnest roof on the map is this one.
        /// </summary>
        private const int RoofCells = 10;

        /// <summary>Range of one noise weight. Arbitrary; only its ratio to the cut matters.</summary>
        private const int WeightRange = 256;

        /// <summary>
        /// Weight below which ground is hollow. Interpolated noise bunches toward the middle of its
        /// range rather than spreading evenly across it, so this sits well under half of
        /// <see cref="WeightRange"/> and still opens up a good third of the underground. Tuned
        /// against the proportion <c>MapMakerTests</c> measures rather than by arithmetic.
        /// </summary>
        private const int HollowBelow = 112;

        /// <summary>
        /// Cells between noise weights, which sets the scale of a cave. A mole is twelve cells
        /// across, so features around this size are chambers it can walk into rather than cracks.
        /// </summary>
        private const int SeedBlock = 12;

        /// <summary>Solid cells out of nine that fill a cell back in. A majority.</summary>
        private const int SolidToFillIn = 5;

        /// <summary>
        /// Smoothing passes. Too few leaves gravel, too many rounds everything off into blobs.
        /// </summary>
        private const int SmoothingPasses = 2;

        /// <summary>Rows below which a map is too thin to be worth hollowing out.</summary>
        private const int ShallowestWorthCaving = 40;

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
