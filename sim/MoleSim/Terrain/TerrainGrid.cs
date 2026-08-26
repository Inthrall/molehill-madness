using System;

namespace MoleSim.Terrain
{
    /// <summary>
    /// The destructible world: one material per cell, plus a hash that stays current as
    /// the map is chewed apart.
    /// </summary>
    /// <remarks>
    /// Cells are 5 cm square, so the shipping map size of 2500x1200 is a field about
    /// 125 m wide and 60 m deep. Storage is one byte per cell, which is 3 MB: small
    /// enough to keep two copies around when a tool wants to diff them.
    ///
    /// Outside the grid, the world is bedrock on the sides and underneath, and open air
    /// above, so a mole can be launched off the top of the screen and fall back but can
    /// never leave through a wall.
    /// </remarks>
    public sealed class TerrainGrid
    {
        private readonly byte[] _cells;
        private ulong _hash;

        public TerrainGrid(int width, int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
            }

            Width = width;
            Height = height;
            _cells = new byte[width * height];

            // Every cell starts as Air, whose contribution to the hash is defined to be
            // zero, so an empty map hashes to zero and a fresh grid needs no walk.
            _hash = 0;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Total cells, for tools reporting coverage.</summary>
        public int CellCount => _cells.Length;

        /// <summary>
        /// Hash of the whole grid, kept current on every write rather than recomputed.
        /// Cheap enough that carving thousands of cells a round costs nothing measurable.
        /// </summary>
        public ulong Hash => _hash;

        /// <summary>Whether a cell coordinate is inside the grid.</summary>
        public bool Contains(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;

        /// <summary>
        /// Material at a cell. Reads outside the grid answer with the surrounding world:
        /// air above, bedrock everywhere else.
        /// </summary>
        public Material this[int x, int y]
        {
            get
            {
                if (!Contains(x, y))
                {
                    return y < 0 ? Material.Air : Material.Bedrock;
                }

                return (Material)_cells[(y * Width) + x];
            }
        }

        /// <summary>
        /// Writes a cell and keeps the hash current. Out-of-range writes are ignored so
        /// that blast radii near an edge need no special casing at every call site.
        /// </summary>
        public void Set(int x, int y, Material material)
        {
            if (!Contains(x, y))
            {
                return;
            }

            int index = (y * Width) + x;
            byte existing = _cells[index];
            byte replacement = (byte)material;

            if (existing == replacement)
            {
                return;
            }

            // The hash is a XOR over per-cell contributions, so a single cell changing
            // costs two mixes rather than a walk of three million cells.
            _hash ^= Contribution(index, existing) ^ Contribution(index, replacement);
            _cells[index] = replacement;
        }

        /// <summary>
        /// Fills a rectangle, clipped to the grid. The workhorse for building test maps
        /// and for laying horizontal strata.
        /// </summary>
        public void FillRectangle(int x, int y, int width, int height, Material material)
        {
            int startX = Math.Max(0, x);
            int startY = Math.Max(0, y);
            int endX = Math.Min(Width, x + width);
            int endY = Math.Min(Height, y + height);

            for (int cellY = startY; cellY < endY; cellY++)
            {
                for (int cellX = startX; cellX < endX; cellX++)
                {
                    Set(cellX, cellY, material);
                }
            }
        }

        /// <summary>
        /// Blows a circular hole, leaving bedrock alone. Returns how many cells actually
        /// changed, which is what the renderer uses to decide whether a chunk needs
        /// rebuilding and what the tests assert on.
        /// </summary>
        public int CarveCircle(int centreX, int centreY, int radiusInCells) =>
            FillCircle(centreX, centreY, radiusInCells, Material.Air, respectBedrock: true);

        /// <summary>
        /// Deposits a circular blob of material, for the Sandbag and for authoring. Will
        /// not overwrite bedrock, and will not paint over existing solid ground unless
        /// <paramref name="overwriteSolid"/> is set.
        /// </summary>
        public int DepositCircle(int centreX, int centreY, int radiusInCells, Material material, bool overwriteSolid = false)
        {
            int changed = 0;
            ForEachCellInCircle(centreX, centreY, radiusInCells, (x, y) =>
            {
                Material existing = this[x, y];

                if (existing == Material.Bedrock)
                {
                    return;
                }

                if (!overwriteSolid && MaterialTable.IsSolid(existing))
                {
                    return;
                }

                if (existing != material)
                {
                    Set(x, y, material);
                    changed++;
                }
            });

            return changed;
        }

        /// <summary>
        /// Recomputes the hash from every cell. The rolling hash is the one used in play;
        /// this is the round-end cross-check that proves the rolling one has not drifted.
        /// </summary>
        public ulong ComputeFullHash()
        {
            ulong hash = 0;

            for (int index = 0; index < _cells.Length; index++)
            {
                byte material = _cells[index];

                if (material != (byte)Material.Air)
                {
                    hash ^= Contribution(index, material);
                }
            }

            return hash;
        }

        /// <summary>
        /// Copies the raw cells into a caller-supplied span, for renderers and dump tools.
        /// The grid never hands out its own array.
        /// </summary>
        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < _cells.Length)
            {
                throw new ArgumentException(
                    "Destination is too small for the grid.", nameof(destination));
            }

            _cells.AsSpan().CopyTo(destination);
        }

        private int FillCircle(int centreX, int centreY, int radiusInCells, Material material, bool respectBedrock)
        {
            int changed = 0;
            ForEachCellInCircle(centreX, centreY, radiusInCells, (x, y) =>
            {
                Material existing = this[x, y];

                if (respectBedrock && existing == Material.Bedrock)
                {
                    return;
                }

                if (existing != material)
                {
                    Set(x, y, material);
                    changed++;
                }
            });

            return changed;
        }

        /// <summary>
        /// Visits every cell whose centre lies within the radius. Integer comparison only,
        /// so the shape of a blast is identical on every platform.
        /// </summary>
        private void ForEachCellInCircle(int centreX, int centreY, int radiusInCells, Action<int, int> visit)
        {
            if (radiusInCells <= 0)
            {
                return;
            }

            int startX = Math.Max(0, centreX - radiusInCells);
            int startY = Math.Max(0, centreY - radiusInCells);
            int endX = Math.Min(Width - 1, centreX + radiusInCells);
            int endY = Math.Min(Height - 1, centreY + radiusInCells);
            long radiusSquared = (long)radiusInCells * radiusInCells;

            for (int y = startY; y <= endY; y++)
            {
                long deltaY = y - centreY;
                long deltaYSquared = deltaY * deltaY;

                for (int x = startX; x <= endX; x++)
                {
                    long deltaX = x - centreX;

                    if ((deltaX * deltaX) + deltaYSquared <= radiusSquared)
                    {
                        visit(x, y);
                    }
                }
            }
        }

        /// <summary>
        /// One cell's contribution to the grid hash. The cell index is folded in so that
        /// two cells swapping materials still changes the hash, and Air contributes
        /// nothing so an untouched map hashes to zero.
        /// </summary>
        private static ulong Contribution(int index, byte material)
        {
            if (material == (byte)Material.Air)
            {
                return 0;
            }

            unchecked
            {
                ulong value = ((ulong)(uint)index * 0x9E3779B97F4A7C15UL) ^ (material * 0xD6E8FEB86659FD93UL);
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
