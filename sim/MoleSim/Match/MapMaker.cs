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
            grid.FillRectangle(0, heightCells - BedrockCells, widthCells, BedrockCells, Material.Bedrock);

            // Last, so the strata above do not fill them back in.
            Caves(grid, rng, surface, heightCells);
            Clutter(grid, rng, surface);

            return grid;
        }

        /// <summary>
        /// Scatters garden things along the surface.
        /// </summary>
        /// <remarks>
        /// The caves gave the underground plenty to do and left the surface a bare rolling line, so
        /// this is the other half: pots, mushrooms, logs, fences, gnomes and bird baths standing
        /// about in it. The design's first biome is a garden with lawns, flowerbeds and a sprinkler,
        /// and its second is an allotment where root vegetables are "chunky soft cover", so cover
        /// you can hide behind and blow apart is the point rather than decoration.
        ///
        /// They are built out of terrain rather than drawn on top of it, which is why there is no
        /// rendering code here at all. The shader fills ground and outlines wherever it meets air, so
        /// a pot made of cells arrives as an outlined pot for nothing, it is destructible on the same
        /// terms as everything else, and it cannot end up floating over a crater somebody made
        /// underneath it. Mostly loose soil, so it is cheap to dig through and cheap to remove.
        /// </remarks>
        private static void Clutter(TerrainGrid grid, MatchRng rng, int[] surface)
        {
            int wanted = ThingsPerThousandCells * grid.Width / 1000;
            int attempts = wanted * AttemptsPerThing;
            int placed = 0;

            // Counted separately from the loop bound. They were the same variable to begin with, so
            // the bound shrank every time something was placed and the search gave up early.
            for (int attempt = 0; attempt < attempts && placed < wanted; attempt++)
            {
                int half = FootprintHalf;
                int centre = half + rng.NextInt(grid.Width - (half * 2));

                if (NearASpawn(grid.Width, centre))
                {
                    continue;
                }

                int highest = surface[centre];
                int lowest = surface[centre];

                for (int column = centre - half; column <= centre + half; column++)
                {
                    highest = surface[column] < highest ? surface[column] : highest;
                    lowest = surface[column] > lowest ? surface[column] : lowest;
                }

                // Flat ground only. A pot halfway up a slope reads as a mistake, and the object is
                // planted on the lowest point of its own footprint so nothing floats.
                if (lowest - highest > SteepestGroundToStandOn)
                {
                    continue;
                }

                // Planted on the lowest point of its own footprint, so the high side of a slope
                // buries a little of it rather than the low side leaving it hanging in the air.
                Place(grid, rng, centre, lowest - 1);
                placed++;
            }
        }

        /// <summary>
        /// Whether a column is close to where somebody might start.
        /// </summary>
        /// <remarks>
        /// A spawn stands on the first solid cell in its column, so a mole would otherwise start the
        /// match balanced on top of a mushroom, which is funny once and then is a mole falling off a
        /// mushroom. The map is built before anybody says how many are playing, so this checks every
        /// count the game allows rather than the one in use.
        /// </remarks>
        private static bool NearASpawn(int gridWidth, int column)
        {
            for (int players = 2; players <= 4; players++)
            {
                int total = players * MatchSettings.MolesPerPlatoon;

                for (int slot = 0; slot < total; slot++)
                {
                    if (System.Math.Abs(SpawnColumn(gridWidth, slot, total) - column) < SpawnClearance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Builds one thing, standing on the given cell.</summary>
        private static void Place(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            switch (rng.NextInt(6))
            {
                case 0:
                    Flowerpot(grid, rng, centre, standsOn);
                    break;

                case 1:
                    Mushroom(grid, rng, centre, standsOn);
                    break;

                case 2:
                    Log(grid, rng, centre, standsOn);
                    break;

                case 3:
                    Fence(grid, rng, centre, standsOn);
                    break;

                case 4:
                    Gnome(grid, rng, centre, standsOn);
                    break;

                default:
                    BirdBath(grid, rng, centre, standsOn);
                    break;
            }
        }

        /// <summary>A pot, wider at the top, with a rim.</summary>
        private static void Flowerpot(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int height = 20 + rng.NextInt(8);
            int top = 10 + rng.NextInt(2);
            int bottom = top - 4;

            for (int row = 0; row < height; row++)
            {
                int half = bottom + ((top - bottom) * row / (height - 1));
                Bar(grid, centre, standsOn - row, half, Material.LooseSoil);
            }

            Bar(grid, centre, standsOn - height, top + 2, Material.LooseSoil);
            Bar(grid, centre, standsOn - height - 1, top + 2, Material.LooseSoil);
        }

        /// <summary>A stalk under a cap wider than it is, which is the overhang worth having.</summary>
        private static void Mushroom(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int stalk = 14 + rng.NextInt(6);
            int cap = 11 + rng.NextInt(2);

            for (int row = 0; row < stalk; row++)
            {
                Bar(grid, centre, standsOn - row, 4, Material.LooseSoil);
            }

            grid.DepositCircle(centre, standsOn - stalk - (cap / 3), cap, Material.LooseSoil);
            Bar(grid, centre, standsOn - stalk, cap, Material.LooseSoil);
        }

        /// <summary>A fallen log lying along the ground.</summary>
        private static void Log(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int half = 11 + rng.NextInt(2);
            int thick = 6 + rng.NextInt(3);

            for (int row = 0; row < thick * 2; row++)
            {
                Bar(grid, centre, standsOn - row, half, Material.LooseSoil);
            }

            // Ends tucked inside the log's own width. Centred on the ends instead, they stuck out a
            // whole radius further than anything had accounted for, which is how a log came to be
            // lying across a starting position that was supposed to be clear.
            grid.DepositCircle(centre - half + thick, standsOn - thick, thick, Material.LooseSoil);
            grid.DepositCircle(centre + half - thick, standsOn - thick, thick, Material.LooseSoil);
        }

        /// <summary>Pickets and two rails. The one thing here with gaps to shoot through.</summary>
        private static void Fence(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int posts = 3;
            int gap = 9 + rng.NextInt(2);
            int height = 22 + rng.NextInt(8);
            int from = centre - (gap * (posts - 1) / 2);

            for (int post = 0; post < posts; post++)
            {
                for (int row = 0; row < height; row++)
                {
                    Bar(grid, from + (post * gap), standsOn - row, 2, Material.LooseSoil);
                }
            }

            for (int rail = 0; rail < 2; rail++)
            {
                int y = standsOn - (height / 3) - (rail * height / 3);

                for (int row = 0; row < 3; row++)
                {
                    Bar(grid, centre, y - row, (gap * (posts - 1) / 2) + 2, Material.LooseSoil);
                }
            }
        }

        /// <summary>A gnome: a body and a pointed hat. The moles take him very seriously.</summary>
        private static void Gnome(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int body = 16 + rng.NextInt(5);
            int hat = 14 + rng.NextInt(5);

            for (int row = 0; row < body; row++)
            {
                Bar(grid, centre, standsOn - row, 10 - (row * 3 / body), Material.LooseSoil);
            }

            for (int row = 0; row < hat; row++)
            {
                Bar(grid, centre, standsOn - body - row, 9 - (row * 9 / hat), Material.LooseSoil);
            }
        }

        /// <summary>A pedestal under a wide shallow basin. Stone, so it is dearer to remove.</summary>
        private static void BirdBath(TerrainGrid grid, MatchRng rng, int centre, int standsOn)
        {
            int stem = 16 + rng.NextInt(5);
            int basin = 11 + rng.NextInt(2);

            for (int row = 0; row < stem; row++)
            {
                Bar(grid, centre, standsOn - row, 5, Material.PackedSoil);
            }

            for (int row = 0; row < 4; row++)
            {
                Bar(grid, centre, standsOn - stem - row, basin - row, Material.PackedSoil);
            }
        }

        /// <summary>
        /// One row of cells, centred, which is what all of these are built out of.
        /// </summary>
        /// <remarks>
        /// Fills air and leaves ground alone. A thing standing on the ground does not replace the
        /// ground it stands on, and overwriting was doing real damage: a thing is planted on the
        /// lowest point of its own footprint, so on any slope its lower rows sat at or under the
        /// turf on the high side and painted the turf away. That left columns with no turf in them
        /// at all, and the turf line is how anything else works out where the original ground was.
        /// </remarks>
        private static void Bar(TerrainGrid grid, int centre, int y, int half, Material material)
        {
            if (y < 0 || y >= grid.Height)
            {
                return;
            }

            for (int x = centre - half; x <= centre + half; x++)
            {
                if (x >= 0 && x < grid.Width && !MaterialTable.IsSolid(grid[x, y]))
                {
                    grid.Set(x, y, material);
                }
            }
        }

        /// <summary>Things per thousand cells of width, so a narrow map gets proportionally fewer.</summary>
        private const int ThingsPerThousandCells = 24;

        /// <summary>Tries per thing wanted, since most spots are too steep or too near a spawn.</summary>
        private const int AttemptsPerThing = 40;

        /// <summary>
        /// Half the ground a thing is checked over, in cells, before it is put there. At least as
        /// wide as the widest thing built below, which is the flowerpot counting its rim.
        /// </summary>
        private const int FootprintHalf = 14;

        /// <summary>
        /// Cells of surface height change a thing will stand on, across its whole footprint.
        /// </summary>
        /// <remarks>
        /// This was four to begin with and nothing was ever placed. The surface comes from triangle
        /// waves whose slopes reach half a cell per column, so it moves ten to twenty cells across a
        /// footprint this wide and every candidate spot failed. Ten is loose enough to find spots and
        /// tight enough that things are not left standing on a hillside at an angle they cannot have.
        /// </remarks>
        private const int SteepestGroundToStandOn = 10;

        /// <summary>
        /// Cells kept clear either side of anywhere somebody might start.
        /// </summary>
        /// <remarks>
        /// Just wider than the widest thing here, which is all that is needed: the point is that
        /// nothing covers a spawn column, not that nothing stands near one. It was twenty-two, and
        /// with sixteen spawns fifty-five cells apart across three possible player counts, that
        /// blocked nearly the whole span between the margins. Things were still placed, and a test
        /// confirmed as much, but every one of them ended up huddled at the two edges of the map
        /// where nobody was looking.
        /// </remarks>
        private const int SpawnClearance = 15;

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

            // All the way down to the bedrock, rather than stopping a fifth of the map short of it.
            // The bottom fifth is the root mat and the world's floor, and leaving it solid meant the
            // deepest sixth of the map was undisturbed strata nobody had a reason to visit, which is
            // the same objection that moved the surface down in the first place. The bedrock itself
            // is still untouched, which is what stops anything digging out of the world.
            int to = heightCells - BedrockCells - 1;
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
        /// Cells between noise weights, which sets the scale of a cave.
        /// </summary>
        /// <remarks>
        /// A mole is twelve cells across. At twelve, a chamber was about one mole wide: enough to
        /// get into and not enough to be a place, so the underground read as a sponge to squeeze
        /// through rather than as somewhere to go. Three times that makes a chamber three moles
        /// across and a passage wide enough for two to pass, which is what makes reaching one worth
        /// the stamina.
        /// </remarks>
        private const int SeedBlock = 36;

        /// <summary>Solid cells out of nine that fill a cell back in. A majority.</summary>
        private const int SolidToFillIn = 5;

        /// <summary>
        /// Smoothing passes. Too few leaves gravel, too many rounds everything off into blobs.
        /// </summary>
        private const int SmoothingPasses = 2;

        /// <summary>Bedrock along the floor of the world, in cells. Never carved.</summary>
        private const int BedrockCells = 10;

        /// <summary>Rows below which a map is too thin to be worth hollowing out.</summary>
        private const int ShallowestWorthCaving = 40;

        /// <summary>
        /// Spawn points spread across the map, some on the surface and some down in the caves.
        /// </summary>
        /// <remarks>
        /// Everybody used to start on the surface, which made the underground somewhere to go rather
        /// than somewhere anybody was, and meant the first minute of every match was fought along one
        /// line. Half of them start on a cave floor now, which puts platoons in the tunnels from tick
        /// zero, gives some moles cover they did not have to dig for, and makes the caves worth having
        /// on the very first turn rather than the fourth.
        ///
        /// The column each spawn stands in has not changed: a margin at each edge then everybody
        /// spaced evenly, platoons interleaved. Only the height is chosen now, and only from ledges
        /// that column actually has, so a column with no cave in it simply starts on the surface.
        ///
        /// Chosen off the terrain's own hash rather than from a generator, which keeps this out of the
        /// map's random sequence entirely: the same map gives the same spawns, and adding this took no
        /// draw away from anything that was already drawing.
        /// </remarks>
        public static Vec2[] SpawnPoints(TerrainGrid grid, int playerCount, int molesPerPlatoon)
        {
            Vec2[] points = new Vec2[playerCount * molesPerPlatoon];
            int total = points.Length;
            int[] ledges = new int[MostLedgesConsidered];

            for (int slot = 0; slot < total; slot++)
            {
                int cellX = SpawnColumn(grid.Width, slot, total);
                int found = Ledges(grid, cellX, ledges);

                points[slot] = new Vec2(
                    WorldScale.ToCentreMetres(cellX),
                    WorldScale.ToMetres(Standing(grid, cellX, slot, ledges, found))
                        - MatchSettings.Radius - WorldScale.CellSize);
            }

            return points;
        }

        /// <summary>
        /// Which ledge in a column this spawn stands on: the surface, or one of its caves.
        /// </summary>
        /// <remarks>
        /// A coin from the terrain hash decides surface or cave, and a second draw off the same
        /// number picks which cave. Falling back to the surface when the column has none, which is
        /// why a map with no caves at all still spawns everybody exactly where it used to.
        /// </remarks>
        private static int Standing(TerrainGrid grid, int cellX, int slot, int[] ledges, int found)
        {
            if (found == 0)
            {
                // No ledge with room to stand anywhere in the column, which a narrow or shallow map
                // can manage. The old behaviour, and better than a spawn in the middle of nothing.
                return WorldScale.ToCell(SurfaceHeight(grid, cellX));
            }

            ulong pick = Scramble(grid.Hash ^ ((ulong)slot * 0x9E3779B97F4A7C15UL));

            if (found == 1 || (pick & 1UL) == 0UL)
            {
                return ledges[0];
            }

            return ledges[1 + (int)((pick >> 1) % (ulong)(found - 1))];
        }

        /// <summary>
        /// Every ledge in a column, top down: a floor with room for a mole above it. Returns how many.
        /// </summary>
        /// <remarks>
        /// A ledge is a run of clear cells with something solid under it, which is the same thing
        /// whether the clear part is the sky or a chamber, so the surface comes out of this as ledge
        /// zero without being a special case.
        ///
        /// The floor has to have some thickness to it. A spawn on a one-cell crust over a cave was
        /// the hazard the old surface-only rule was tested against, and it is no less of one now that
        /// caves are somewhere to start: it reads as standing on the ground and behaves like standing
        /// on a lid.
        /// </remarks>
        private static int Ledges(TerrainGrid grid, int cellX, int[] into)
        {
            int found = 0;
            int clear = 0;
            int floor = grid.Height - BedrockCells;

            for (int cellY = 0; cellY < floor && found < into.Length; cellY++)
            {
                if (!MaterialTable.IsSolid(grid[cellX, cellY]))
                {
                    clear++;
                    continue;
                }

                if (clear >= HeadRoomCells && Thick(grid, cellX, cellY))
                {
                    into[found] = cellY;
                    found++;
                }

                clear = 0;
            }

            return found;
        }

        /// <summary>Whether a floor is more than a lid over the next hole down.</summary>
        private static bool Thick(TerrainGrid grid, int cellX, int cellY)
        {
            for (int down = 0; down < LedgeCells; down++)
            {
                if (!grid.Contains(cellX, cellY + down)
                    || !MaterialTable.IsSolid(grid[cellX, cellY + down]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// SplitMix64's finaliser, so a hash and a slot number make a well-spread choice.
        /// </summary>
        /// <remarks>
        /// The terrain hash is an XOR fold, so its low bits move with the last cell written rather
        /// than with the map as a whole, and reading a coin straight off it would have every spawn on
        /// a map agreeing with every other. Mixing first is what makes sixteen independent choices
        /// out of one number.
        /// </remarks>
        private static ulong Scramble(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;

            return value ^ (value >> 31);
        }

        /// <summary>
        /// Clear cells a mole needs above a floor to stand on it. A mole is twelve cells across.
        /// </summary>
        private const int HeadRoomCells = 14;

        /// <summary>Solid cells under a ledge before it counts as ground rather than as a lid.</summary>
        private const int LedgeCells = 3;

        /// <summary>Ledges looked at in one column. More than anybody could start on.</summary>
        private const int MostLedgesConsidered = 8;

        /// <summary>
        /// Which column one spawn stands in.
        /// </summary>
        /// <remarks>
        /// A margin at each edge, then everybody spaced evenly. Platoons interleave rather than
        /// clustering, which keeps anybody from being cornered at the start. Shared with the surface
        /// clutter, which needs to know where not to put a mushroom.
        /// </remarks>
        private static int SpawnColumn(int gridWidth, int slot, int total)
        {
            int margin = gridWidth / 12;
            int span = gridWidth - (margin * 2);

            return margin + (span * slot / (total > 1 ? total - 1 : 1));
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
