using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

/// <summary>
/// The ground a match is played on.
/// </summary>
/// <remarks>
/// Generated ground is a stand-in until maps come from an artist through the map baker, but it is
/// the ground every corpus fixture and every playtest actually uses, so its invariants are worth
/// holding on to. The one that matters most is that the caves stay buried: a spawn point is placed
/// on the first solid cell in its column, so a cave that broke the surface would quietly move where
/// everybody starts.
/// </remarks>
[TestFixture]
public sealed class MapMakerTests
{
    private const int Width = 1000;
    private const int Height = 480;

    private static TerrainGrid Field(ulong seed = 20260827UL) =>
        MapMaker.Field(Width, Height, seed);

    /// <summary>The first solid cell in a column, which is what a spawn stands on.</summary>
    private static int SurfaceCell(TerrainGrid grid, int cellX)
    {
        for (int cellY = 0; cellY < grid.Height; cellY++)
        {
            if (MaterialTable.IsSolid(grid[cellX, cellY]))
            {
                return cellY;
            }
        }

        return grid.Height;
    }

    /// <summary>How much of the ground under the surface is hollow, as a percentage.</summary>
    private static int HollowPercent(TerrainGrid grid)
    {
        int hollow = 0;
        int underground = 0;

        for (int cellX = 0; cellX < grid.Width; cellX++)
        {
            for (int cellY = SurfaceCell(grid, cellX) + 1; cellY < grid.Height; cellY++)
            {
                underground++;

                if (grid[cellX, cellY] == Material.Air)
                {
                    hollow++;
                }
            }
        }

        return underground == 0 ? 0 : hollow * 100 / underground;
    }

    /// <summary>
    /// Enough cave to matter, and not so much that the map is lacework.
    /// </summary>
    /// <remarks>
    /// Both ends of this are worth defending. Too little and the caves are scenery: every route stays
    /// over the top, which is what the generator did before. Too much and the stamina economy loses
    /// its teeth, because digging is the price the whole movement design is built around and ground
    /// somebody else already dug is free.
    ///
    /// A proportion rather than a count, which is what caught the first attempt: wandering tunnels
    /// spread evenly through the full depth passed a bare "are there any" check while putting most of
    /// them further down than anybody would ever pay to reach.
    ///
    /// It sits around a quarter as tuned. The ceiling is well clear of that, because it is there to
    /// catch ground turned to lacework rather than to pin the number.
    /// </remarks>
    [Test]
    public void AFieldIsHollowedOutWithoutBecomingASponge()
    {
        int hollow = HollowPercent(Field());

        Assert.Multiple(() =>
        {
            Assert.That(hollow, Is.GreaterThan(3), "a generated field should be hollowed out");
            Assert.That(hollow, Is.LessThan(38), "a generated field should still be mostly ground");
        });
    }

    [Test]
    public void CavesSitWhereAMoleCouldReachThem()
    {
        TerrainGrid grid = Field();
        int shallow = 0;

        // Within about five metres of the surface, which is a dig somebody might pay for. A cave
        // thirty metres down is terrain nobody will ever see.
        for (int cellX = 0; cellX < Width; cellX++)
        {
            int surface = SurfaceCell(grid, cellX);

            for (int cellY = surface; cellY < surface + 80 && cellY < Height; cellY++)
            {
                if (grid[cellX, cellY] == Material.Air)
                {
                    shallow++;
                }
            }
        }

        Assert.That(shallow, Is.GreaterThan(Width * 4), "the caves are all out of reach");
    }

    [Test]
    public void EveryCaveKeepsARoofOverIt()
    {
        TerrainGrid grid = Field();

        for (int cellX = 0; cellX < Width; cellX++)
        {
            int surface = SurfaceCell(grid, cellX);

            Assert.That(surface, Is.LessThan(Height), $"column {cellX} has no ground at all");

            // Ten solid cells below the surface everywhere. This is the guarantee that keeps the
            // spawn points where they were: break it and a mole starts down a hole instead.
            for (int depth = 0; depth < 10; depth++)
            {
                Assert.That(
                    MaterialTable.IsSolid(grid[cellX, surface + depth]), Is.True,
                    $"column {cellX} is hollow {depth} cells below its surface");
            }
        }
    }

    [Test]
    public void CavingLeavesTheFloorOfTheWorldAlone()
    {
        TerrainGrid grid = Field();

        for (int cellX = 0; cellX < Width; cellX++)
        {
            for (int cellY = Height - 10; cellY < Height; cellY++)
            {
                Assert.That(
                    grid[cellX, cellY], Is.EqualTo(Material.Bedrock),
                    $"the world floor is open at {cellX},{cellY}");
            }
        }
    }

    /// <summary>
    /// Nobody starts down a hole.
    /// </summary>
    /// <remarks>
    /// Deliberately not asserting that a spawn is clear of solid ground. A mole's body is six cells
    /// in the radius and a spawn sits one cell above its own column, so on any slope the body
    /// overlaps the hillside beside it. That has been true since spawns existed and the motion
    /// solver settles it on the first tick; it is not something caving introduced.
    ///
    /// What caving could break is this: a spawn standing over a void rather than on the
    /// surface. So that is what gets checked.
    /// </remarks>
    [Test]
    public void NobodyStartsDownAHole()
    {
        TerrainGrid grid = Field();
        Vec2[] spawns = MapMaker.SpawnPoints(grid, 4, MatchSettings.MolesPerPlatoon);

        foreach (Vec2 spawn in spawns)
        {
            int cellX = WorldScale.ToCell(spawn.X);
            int cellY = WorldScale.ToCell(spawn.Y);
            int surface = SurfaceCell(grid, cellX);

            Assert.Multiple(() =>
            {
                Assert.That(cellY, Is.LessThan(surface), $"column {cellX} starts below its ground");
                Assert.That(surface, Is.LessThan(Height - 10), $"column {cellX} starts in bedrock");
                Assert.That(
                    MaterialTable.IsSolid(grid[cellX, surface + 1]), Is.True,
                    $"column {cellX} stands on a one-cell crust over a cave");
            });
        }
    }

    [Test]
    public void TheSameSeedHollowsOutTheSameCaves()
    {
        Assert.That(Field(4242UL).ComputeFullHash(), Is.EqualTo(Field(4242UL).ComputeFullHash()));
    }

    [Test]
    public void DifferentSeedsHollowOutDifferentCaves()
    {
        Assert.That(
            Field(4242UL).ComputeFullHash(), Is.Not.EqualTo(Field(4243UL).ComputeFullHash()));
    }

    [Test]
    public void ANarrowMapIsNotRiddledWithThem()
    {
        // A narrow map gets the same treatment at the same scale, so it should not come out any
        // more hollow than a wide one.
        Assert.That(
            HollowPercent(MapMaker.Field(600, 320, 31337UL)), Is.LessThan(38),
            "a narrow map is mostly still ground");
    }
}
