using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>What a route would actually do, worked out before committing to it.</summary>
    /// <remarks>
    /// The design's planning screen shows a ghost of the mole walking the laid route while
    /// the mole itself stands still, with the time and stamina gauges draining as the pen
    /// moves. All of that needs a real answer to "what would this cost", including the
    /// digging, and digging changes the world.
    ///
    /// So the preview runs the actual movement solver against a copy of the terrain. Not an
    /// estimate, not a straight-line approximation: the same code that will resolve the
    /// round, on a throwaway map. That is what makes the gauges honest, and it is the
    /// reason the client can promise that what the ghost does is what the mole will do.
    /// </remarks>
    public sealed class GhostPreview
    {
        private GhostPreview(List<Vec2> path, Fix64 staminaSpent, int ticksUsed, bool ranOutOfPuff)
        {
            Path = path;
            StaminaSpent = staminaSpent;
            TicksUsed = ticksUsed;
            RanOutOfPuff = ranOutOfPuff;
        }

        /// <summary>Where the ghost is at each tick. One entry per tick actually used.</summary>
        public IReadOnlyList<Vec2> Path { get; }

        public Fix64 StaminaSpent { get; }

        /// <summary>How much of the eight seconds the route consumes.</summary>
        public int TicksUsed { get; }

        /// <summary>Whether the route is longer than the mole can afford.</summary>
        public bool RanOutOfPuff { get; }

        /// <summary>Where the ghost ends up, which is where a stamped shot would fire from.</summary>
        public Vec2 End => Path.Count > 0 ? Path[Path.Count - 1] : Vec2.Zero;

        /// <summary>
        /// Walks a candidate route on a copy of the world and reports what happened.
        /// </summary>
        public static GhostPreview Walk(Mole mole, TerrainGrid terrain, IReadOnlyList<Vec2> route)
        {
            TerrainGrid scratch = terrain.Clone();

            // A stand-in carrying the real mole's state, so the preview accounts for a
            // stamina budget already shortened by the stalemate nudge.
            Mole ghost = new Mole(mole.Seat, mole.Index, mole.Position)
            {
                Stamina = mole.Stamina,
                IsAirborne = mole.IsAirborne,
                Velocity = mole.Velocity,
                Facing = mole.Facing,
                DiggingIsCheap = mole.DiggingIsCheap,
                IsSnared = mole.IsSnared,
            };

            Fix64 startingStamina = ghost.Stamina;
            Vec2[] worldRoute = new Vec2[route.Count];

            for (int index = 0; index < route.Count; index++)
            {
                worldRoute[index] = route[index];
            }

            List<Vec2> path = new List<Vec2>(MatchSettings.TicksPerRound) { ghost.Position };
            int ticks = 0;

            for (int tick = 0; tick < MatchSettings.TicksPerRound; tick++)
            {
                Vec2 before = ghost.Position;
                MoleMotion.Step(ghost, scratch, worldRoute);
                path.Add(ghost.Position);
                ticks = tick + 1;

                bool finished = ghost.WaypointIndex >= worldRoute.Length
                    && !ghost.IsAirborne
                    && ghost.Position == before;

                if (finished)
                {
                    break;
                }
            }

            return new GhostPreview(
                path,
                startingStamina - ghost.Stamina,
                ticks,
                ghost.Stamina <= Fix64.Zero);
        }
    }
}
