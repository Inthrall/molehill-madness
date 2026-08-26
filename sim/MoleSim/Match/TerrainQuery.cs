using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// Everything the movement solver needs to ask the terrain.
    /// </summary>
    /// <remarks>
    /// Bodies are circles and the world is a grid of cells, so every question here comes
    /// down to testing cell centres against a radius. Integer comparisons only, so the
    /// shape of a collision is identical on every platform.
    /// </remarks>
    public static class TerrainQuery
    {
        /// <summary>Material under a point. Outside the grid answers air above, bedrock elsewhere.</summary>
        public static Material MaterialAt(TerrainGrid terrain, Vec2 position) =>
            terrain[WorldScale.ToCell(position.X), WorldScale.ToCell(position.Y)];

        /// <summary>Whether a body of the given radius would overlap anything solid.</summary>
        public static bool IsBlocked(TerrainGrid terrain, Vec2 position, Fix64 radius)
        {
            int minX = WorldScale.ToCell(position.X - radius);
            int maxX = WorldScale.ToCell(position.X + radius);
            int minY = WorldScale.ToCell(position.Y - radius);
            int maxY = WorldScale.ToCell(position.Y + radius);
            Fix64 radiusSquared = radius * radius;

            for (int cellY = minY; cellY <= maxY; cellY++)
            {
                Fix64 centreY = WorldScale.ToCentreMetres(cellY);
                Fix64 deltaY = centreY - position.Y;
                Fix64 deltaYSquared = deltaY * deltaY;

                if (deltaYSquared > radiusSquared)
                {
                    continue;
                }

                for (int cellX = minX; cellX <= maxX; cellX++)
                {
                    if (!MaterialTable.IsSolid(terrain[cellX, cellY]))
                    {
                        continue;
                    }

                    Fix64 deltaX = WorldScale.ToCentreMetres(cellX) - position.X;

                    if ((deltaX * deltaX) + deltaYSquared <= radiusSquared)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a body is standing on something: clear where it is, blocked just below.
        /// </summary>
        public static bool IsSupported(TerrainGrid terrain, Vec2 position, Fix64 radius)
        {
            if (IsBlocked(terrain, position, radius))
            {
                return false;
            }

            Vec2 justBelow = new Vec2(position.X, position.Y + ProbeDepth);
            return IsBlocked(terrain, justBelow, radius);
        }

        /// <summary>How far below its feet a body looks when asking whether it is standing.</summary>
        private static Fix64 ProbeDepth => Fix64.Ratio(2, WorldScale.CellsPerMetre);

        /// <summary>
        /// Looks for a clear position at or above <paramref name="position"/>, within the
        /// step height. This is what lets a mole walk up a slope instead of tunnelling
        /// along it and dissolving the surface of the map.
        /// </summary>
        public static bool TryStepUp(
            TerrainGrid terrain, Vec2 position, Fix64 radius, Fix64 maxRise, out Vec2 stepped)
        {
            Fix64 increment = WorldScale.CellSize;

            for (Fix64 rise = increment; rise <= maxRise; rise += increment)
            {
                Vec2 candidate = new Vec2(position.X, position.Y - rise);

                if (!IsBlocked(terrain, candidate, radius))
                {
                    stepped = candidate;
                    return true;
                }
            }

            stepped = position;
            return false;
        }

        /// <summary>
        /// Looks for ground within the snap distance below a clear position, so a mole
        /// walking down a slope follows it rather than launching off every bump.
        /// </summary>
        public static bool TrySnapDown(
            TerrainGrid terrain, Vec2 position, Fix64 radius, Fix64 maxDrop, out Vec2 snapped)
        {
            Fix64 increment = WorldScale.CellSize;

            for (Fix64 drop = increment; drop <= maxDrop; drop += increment)
            {
                Vec2 candidate = new Vec2(position.X, position.Y + drop);

                if (IsBlocked(terrain, candidate, radius))
                {
                    // One cell back up is the last clear spot, which is where it lands.
                    snapped = new Vec2(position.X, candidate.Y - increment);
                    return true;
                }
            }

            snapped = position;
            return false;
        }

        /// <summary>
        /// Removes a body-sized hole, which is what a mole moving through dirt leaves
        /// behind. Returns how many cells actually went, so a caller can tell whether any
        /// digging happened at all.
        /// </summary>
        public static int CarveBody(TerrainGrid terrain, Vec2 position, Fix64 radius)
        {
            // One cell wider than the body, and that margin is load-bearing rather than
            // generosity. Carving is measured in whole cells from the centre cell, while
            // the overlap test is measured from a continuous position, so a body sitting
            // part-way across a cell can see solid up to half a cell further out than the
            // carve cleared. Without the margin a tunnelling mole clears its own path,
            // still reports itself blocked by the sliver it left behind, and stops dead
            // after a single step.
            int cellRadius = Fix64.FloorToInt(radius / WorldScale.CellSize) + 1;

            return terrain.CarveCircle(
                WorldScale.ToCell(position.X),
                WorldScale.ToCell(position.Y),
                cellRadius);
        }

        /// <summary>
        /// Whether solid ground stands between two points.
        /// </summary>
        /// <remarks>
        /// This is what makes the underground genuinely safe. A ballistic blast reaches
        /// only what it can see, so a mole in a tunnel with a metre of dirt between it and
        /// the surface takes nothing from a shell landing overhead. Seismic weapons
        /// deliberately ignore this, which is why Fracking is the answer to a table that
        /// has all gone to ground.
        ///
        /// Walked one cell at a time along the line, which is coarse but cheap and, more
        /// to the point, identical everywhere.
        /// </remarks>
        public static bool HasLineOfSight(TerrainGrid terrain, Vec2 from, Vec2 to)
        {
            Vec2 offset = to - from;
            Fix64 distance = offset.Length();

            if (distance <= WorldScale.CellSize)
            {
                return true;
            }

            int steps = Fix64.ToInt(distance / WorldScale.CellSize) + 1;
            Vec2 stride = offset / Fix64.FromInt(steps);

            // Endpoints are skipped: the shell is allowed to be touching the ground it
            // just hit, and the mole is allowed to be standing on something.
            for (int step = 1; step < steps; step++)
            {
                Vec2 along = from + (stride * Fix64.FromInt(step));

                if (MaterialTable.IsSolid(MaterialAt(terrain, along)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Fills roofed cavities back in, which is what a seismic shock does to a tunnel.
        /// </summary>
        /// <remarks>
        /// A cell collapses when there is solid ground somewhere above it in the same
        /// column, which picks out tunnels and dugouts and leaves open sky alone without
        /// needing to work out where the surface is.
        ///
        /// Testing only the cell immediately above is not enough, and was wrong at first:
        /// a tunnel is many cells tall, and every cell except the topmost has more tunnel
        /// above it, so only the ceiling fell in and the tunnel stayed perfectly usable.
        /// Each column is walked from the top instead, remembering whether a roof has been
        /// passed. Returns how many cells fell in.
        /// </remarks>
        public static int CollapseCavities(TerrainGrid terrain, Vec2 centre, Fix64 radius)
        {
            int centreX = WorldScale.ToCell(centre.X);
            int centreY = WorldScale.ToCell(centre.Y);
            int cellRadius = Fix64.FloorToInt(radius / WorldScale.CellSize);
            long radiusSquared = (long)cellRadius * cellRadius;

            int filled = 0;

            for (int x = centreX - cellRadius; x <= centreX + cellRadius; x++)
            {
                if (x < 0 || x >= terrain.Width)
                {
                    continue;
                }

                long deltaX = x - centreX;
                bool roofed = false;

                for (int y = 0; y < terrain.Height; y++)
                {
                    Material material = terrain[x, y];

                    if (MaterialTable.IsSolid(material))
                    {
                        roofed = true;
                        continue;
                    }

                    if (!roofed)
                    {
                        continue;
                    }

                    long deltaY = y - centreY;

                    if ((deltaX * deltaX) + (deltaY * deltaY) > radiusSquared)
                    {
                        continue;
                    }

                    terrain.Set(x, y, Material.LooseSoil);
                    filled++;
                }
            }

            return filled;
        }

        /// <summary>
        /// An approximate outward direction from solid terrain, for pushing a body out of
        /// a wall it has ended up inside. Sums the offsets of nearby solid cells and
        /// points the other way.
        /// </summary>
        public static Vec2 EscapeDirection(TerrainGrid terrain, Vec2 position, Fix64 radius)
        {
            int minX = WorldScale.ToCell(position.X - radius);
            int maxX = WorldScale.ToCell(position.X + radius);
            int minY = WorldScale.ToCell(position.Y - radius);
            int maxY = WorldScale.ToCell(position.Y + radius);
            Fix64 radiusSquared = radius * radius;

            Vec2 pull = Vec2.Zero;

            for (int cellY = minY; cellY <= maxY; cellY++)
            {
                Fix64 deltaY = WorldScale.ToCentreMetres(cellY) - position.Y;

                for (int cellX = minX; cellX <= maxX; cellX++)
                {
                    if (!MaterialTable.IsSolid(terrain[cellX, cellY]))
                    {
                        continue;
                    }

                    Fix64 deltaX = WorldScale.ToCentreMetres(cellX) - position.X;
                    Fix64 distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

                    if (distanceSquared <= radiusSquared)
                    {
                        pull += new Vec2(deltaX, deltaY);
                    }
                }
            }

            // Solid pulls one way, so escape points the other. A body exactly surrounded
            // has no preference and is told to go up, which is the least surprising answer
            // for something buried.
            Vec2 escape = -pull;

            return escape.LengthSquared() == Fix64.Zero ? -Vec2.UnitY : escape.Normalised();
        }
    }
}
