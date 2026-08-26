using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Diagnostics
{
    /// <summary>
    /// A scripted workout for every part of the simulation that could plausibly differ
    /// between platforms, reduced to a handful of numbers.
    /// </summary>
    /// <remarks>
    /// Two machines that produce different numbers here do not agree about the rules of
    /// the game, and online play between them is impossible. This is the check Phase 0
    /// turns on before anything else gets built.
    ///
    /// It deliberately lives in the simulation rather than in the tools, so that the
    /// desktop runner and the phone are executing byte-identical code. A probe
    /// reimplemented per platform could drift, and would then be measuring itself.
    /// </remarks>
    public readonly struct DeterminismFingerprint
    {
        internal DeterminismFingerprint(
            long arithmetic, long driftX, long driftY, int cellX, int cellY,
            ulong randomness, ulong terrainRolling, ulong terrainFull)
        {
            Arithmetic = arithmetic;
            DriftX = driftX;
            DriftY = driftY;
            CellX = cellX;
            CellY = cellY;
            Randomness = randomness;
            TerrainRolling = terrainRolling;
            TerrainFull = terrainFull;
        }

        /// <summary>Raw result of a long chain of multiplies, divides, roots and lengths.</summary>
        public long Arithmetic { get; }

        /// <summary>Raw components of a vector accumulated through normalise and cap.</summary>
        public long DriftX { get; }

        public long DriftY { get; }

        /// <summary>Cell that vector lands in, exercising the world scale conversion.</summary>
        public int CellX { get; }

        public int CellY { get; }

        /// <summary>Fold of ten thousand draws from a seeded generator.</summary>
        public ulong Randomness { get; }

        /// <summary>Terrain hash kept rolling through two thousand carves.</summary>
        public ulong TerrainRolling { get; }

        /// <summary>The same terrain hashed from scratch. Must equal the rolling one.</summary>
        public ulong TerrainFull { get; }

        /// <summary>
        /// Whether the rolling terrain hash still agrees with a full recompute. A false
        /// here is a bug in the grid rather than a difference between platforms.
        /// </summary>
        public bool TerrainHashesAgree => TerrainRolling == TerrainFull;

        /// <summary>
        /// Everything above folded into one number. This is the value to read off a phone
        /// screen and compare against a desktop: one line, and either it matches or the
        /// project has a problem.
        /// </summary>
        public ulong Combined
        {
            get
            {
                unchecked
                {
                    ulong hash = 0xCBF29CE484222325UL;
                    hash = Fold(hash, (ulong)Arithmetic);
                    hash = Fold(hash, (ulong)DriftX);
                    hash = Fold(hash, (ulong)DriftY);
                    hash = Fold(hash, (ulong)(uint)CellX);
                    hash = Fold(hash, (ulong)(uint)CellY);
                    hash = Fold(hash, Randomness);
                    hash = Fold(hash, TerrainRolling);
                    hash = Fold(hash, TerrainFull);
                    return hash;
                }
            }
        }

        private static ulong Fold(ulong hash, ulong value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 0x100000001B3UL;
                return hash ^ (hash >> 29);
            }
        }
    }

    /// <summary>Runs the fingerprint workout.</summary>
    public static class DeterminismProbe
    {
        /// <summary>
        /// Runs every check and returns the results. Takes a few milliseconds, so it is
        /// cheap enough to run at startup on a phone.
        /// </summary>
        public static DeterminismFingerprint Run()
        {
            long arithmetic = RunArithmetic();
            Vec2 drift = RunVectors();
            WorldScale.ToCell(drift, out int cellX, out int cellY);
            ulong randomness = RunRandomness();
            RunTerrain(out ulong rolling, out ulong full);

            return new DeterminismFingerprint(
                arithmetic, drift.X.Raw, drift.Y.Raw, cellX, cellY, randomness, rolling, full);
        }

        /// <summary>Exercises the wide intermediates in multiply, divide, root and length.</summary>
        private static long RunArithmetic()
        {
            Fix64 accumulator = Fix64.One;

            for (int step = 1; step <= 500; step++)
            {
                Fix64 value = Fix64.FromInt(step);
                accumulator += Fix64.Sqrt(value) * Fix64.Ratio(7, 13);
                accumulator = accumulator / Fix64.Ratio(1001, 1000);
                accumulator += Fix64.Hypot(value, Fix64.FromInt(step * 3));
            }

            return accumulator.Raw;
        }

        /// <summary>Exercises normalise, cap and the divisions inside them.</summary>
        private static Vec2 RunVectors()
        {
            Vec2 drift = Vec2.Zero;

            for (int step = 1; step <= 400; step++)
            {
                Vec2 velocity = new Vec2(
                    Fix64.FromInt(step % 37) - Fix64.FromInt(18),
                    Fix64.FromInt(step % 23));

                drift += velocity.Normalised() * Fix64.Ratio(step % 11, 4);
                drift = drift.WithMaxLength(Fix64.FromInt(120));
            }

            return drift;
        }

        /// <summary>Exercises the exact draw sequence a match depends on.</summary>
        private static ulong RunRandomness()
        {
            MatchRng rng = new MatchRng(20260826UL);
            ulong mixed = 0;

            for (int draw = 0; draw < 10_000; draw++)
            {
                mixed ^= rng.NextUInt64();
                mixed = (mixed << 1) | (mixed >> 63);
            }

            return mixed;
        }

        /// <summary>Exercises carving, clipping and both hash paths over a real map.</summary>
        private static void RunTerrain(out ulong rolling, out ulong full)
        {
            TerrainGrid grid = BuildProbeMap(600, 320);
            MatchRng rng = new MatchRng(1337UL);

            for (int carve = 0; carve < 2000; carve++)
            {
                grid.CarveCircle(
                    rng.NextInt(grid.Width),
                    rng.NextInt(grid.Height),
                    rng.NextInt(2, 14));
            }

            rolling = grid.Hash;
            full = grid.ComputeFullHash();
        }

        /// <summary>
        /// A miniature of the shipping cross-section. Built from whole-number arithmetic
        /// so the shape is identical everywhere, which the real maps get from baked art
        /// instead.
        /// </summary>
        public static TerrainGrid BuildProbeMap(int width, int height)
        {
            TerrainGrid grid = new TerrainGrid(width, height);
            int surface = height / 4;

            for (int x = 0; x < width; x++)
            {
                int rise = ((x * 7 / 40) % 24) - 12;
                int top = surface + (rise / 3);

                grid.FillRectangle(x, top, 1, 3, Material.Turf);
                grid.FillRectangle(x, top + 3, 1, height / 8, Material.LooseSoil);
                grid.FillRectangle(x, top + 3 + (height / 8), 1, height / 3, Material.PackedSoil);
            }

            grid.FillRectangle(0, height - (height / 6), width, height / 12, Material.RootMat);
            grid.FillRectangle(0, height - (height / 12), width, height / 12, Material.Bedrock);

            return grid;
        }
    }
}
