using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

[TestFixture]
public sealed class TerrainGridTests
{
    private static TerrainGrid MakeStrataGrid(int width = 200, int height = 120)
    {
        // A miniature of the shipping cross-section: air, a turf line, then the strata,
        // then bedrock along the floor.
        TerrainGrid grid = new TerrainGrid(width, height);
        int surface = height / 4;

        grid.FillRectangle(0, surface, width, 2, Material.Turf);
        grid.FillRectangle(0, surface + 2, width, 20, Material.LooseSoil);
        grid.FillRectangle(0, surface + 22, width, 40, Material.PackedSoil);
        grid.FillRectangle(0, surface + 62, width, 10, Material.RootMat);
        grid.FillRectangle(0, height - 8, width, 8, Material.Bedrock);

        return grid;
    }

    [Test]
    public void NewGridIsAllAirAndHashesToZero()
    {
        TerrainGrid grid = new TerrainGrid(64, 32);

        Assert.Multiple(() =>
        {
            Assert.That(grid[0, 0], Is.EqualTo(Material.Air));
            Assert.That(grid[63, 31], Is.EqualTo(Material.Air));
            Assert.That(grid.Hash, Is.Zero);
            Assert.That(grid.CellCount, Is.EqualTo(64 * 32));
        });
    }

    [Test]
    public void ConstructorRejectsEmptyDimensions()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainGrid(0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainGrid(10, -1));
        });
    }

    [Test]
    public void OutsideTheGridIsAirAboveAndBedrockEverywhereElse()
    {
        TerrainGrid grid = new TerrainGrid(32, 32);

        Assert.Multiple(() =>
        {
            Assert.That(grid[5, -1], Is.EqualTo(Material.Air), "above the map, moles can fly");
            Assert.That(grid[-1, 5], Is.EqualTo(Material.Bedrock), "left wall");
            Assert.That(grid[32, 5], Is.EqualTo(Material.Bedrock), "right wall");
            Assert.That(grid[5, 32], Is.EqualTo(Material.Bedrock), "under the floor");
        });
    }

    [Test]
    public void RollingHashAlwaysAgreesWithAFullRecompute()
    {
        // This is the invariant that lets the game hash three million cells for free.
        // If it ever fails, every determinism check downstream is worthless.
        TerrainGrid grid = MakeStrataGrid();
        Assert.That(grid.Hash, Is.EqualTo(grid.ComputeFullHash()), "after building strata");

        MatchRng rng = new MatchRng(20260826UL);

        for (int carve = 0; carve < 200; carve++)
        {
            grid.CarveCircle(rng.NextInt(grid.Width), rng.NextInt(grid.Height), rng.NextInt(1, 12));
            Assert.That(grid.Hash, Is.EqualTo(grid.ComputeFullHash()), $"after carve {carve}");
        }

        for (int deposit = 0; deposit < 50; deposit++)
        {
            grid.DepositCircle(rng.NextInt(grid.Width), rng.NextInt(grid.Height), rng.NextInt(1, 8), Material.LooseSoil);
            Assert.That(grid.Hash, Is.EqualTo(grid.ComputeFullHash()), $"after deposit {deposit}");
        }
    }

    [Test]
    public void CarvingAwayEverythingReturnsTheHashToEmpty()
    {
        TerrainGrid grid = new TerrainGrid(64, 64);
        grid.FillRectangle(10, 10, 20, 20, Material.PackedSoil);

        ulong filled = grid.Hash;
        Assert.That(filled, Is.Not.Zero);

        grid.FillRectangle(10, 10, 20, 20, Material.Air);

        Assert.That(grid.Hash, Is.Zero,
            "removing exactly what was added should undo its hash contribution");
    }

    [Test]
    public void TwoCellsSwappingMaterialsStillChangesTheHash()
    {
        // A hash that folded in only the materials, not their positions, would miss this
        // and let two different maps look identical.
        TerrainGrid first = new TerrainGrid(8, 8);
        first.Set(1, 1, Material.PackedSoil);
        first.Set(2, 2, Material.RootMat);

        TerrainGrid second = new TerrainGrid(8, 8);
        second.Set(1, 1, Material.RootMat);
        second.Set(2, 2, Material.PackedSoil);

        Assert.That(second.Hash, Is.Not.EqualTo(first.Hash));
    }

    [Test]
    public void CarvingLeavesBedrockAlone()
    {
        TerrainGrid grid = new TerrainGrid(64, 64);
        grid.FillRectangle(0, 30, 64, 4, Material.Bedrock);
        grid.FillRectangle(0, 20, 64, 10, Material.PackedSoil);

        int changed = grid.CarveCircle(32, 31, 20);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.GreaterThan(0), "the soil should have gone");
            Assert.That(grid[32, 31], Is.EqualTo(Material.Bedrock), "bedrock must survive a direct hit");
            Assert.That(grid[32, 25], Is.EqualTo(Material.Air), "soil above it should not");
        });
    }

    [Test]
    public void CarvedCellsBecomeAirSoTunnelsCostWhatOpenGroundCosts()
    {
        // The design point that makes re-using somebody else's tunnel worthwhile: a dug
        // tunnel is not a special material, it is simply air.
        TerrainGrid grid = new TerrainGrid(32, 32);
        grid.FillRectangle(0, 0, 32, 32, Material.PackedSoil);
        grid.CarveCircle(16, 16, 5);

        Assert.That(grid[16, 16], Is.EqualTo(Material.Air));
        Assert.That(MaterialTable.CostPerMetre(grid[16, 16]),
            Is.EqualTo(MaterialTable.CostPerMetre(Material.Turf)));
    }

    [Test]
    public void CarveIsRoundAndTheRightSize()
    {
        TerrainGrid grid = new TerrainGrid(128, 128);
        grid.FillRectangle(0, 0, 128, 128, Material.LooseSoil);

        const int Radius = 20;
        int changed = grid.CarveCircle(64, 64, Radius);

        // Cells whose centres fall inside a circle of this radius: close to pi r squared.
        double expected = Math.PI * Radius * Radius;
        Assert.That(changed, Is.EqualTo((int)expected).Within(expected * 0.05),
            "carve area is not circular");

        Assert.Multiple(() =>
        {
            Assert.That(grid[64, 64], Is.EqualTo(Material.Air), "centre");
            Assert.That(grid[64 + Radius, 64], Is.EqualTo(Material.Air), "edge on the axis");
            Assert.That(grid[64 + Radius + 1, 64], Is.EqualTo(Material.LooseSoil), "just outside");
            Assert.That(grid[64 + 15, 64 + 15], Is.EqualTo(Material.LooseSoil), "corner of the bounding box");
        });
    }

    [Test]
    public void CarvingTheSamePlaceTwiceChangesNothingTheSecondTime()
    {
        TerrainGrid grid = new TerrainGrid(64, 64);
        grid.FillRectangle(0, 0, 64, 64, Material.PackedSoil);

        int first = grid.CarveCircle(32, 32, 10);
        ulong hashAfterFirst = grid.Hash;
        int second = grid.CarveCircle(32, 32, 10);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan(0));
            Assert.That(second, Is.Zero, "nothing left to remove");
            Assert.That(grid.Hash, Is.EqualTo(hashAfterFirst));
        });
    }

    [Test]
    public void CarvingOffTheEdgeIsClippedNotCrashed()
    {
        TerrainGrid grid = new TerrainGrid(32, 32);
        grid.FillRectangle(0, 0, 32, 32, Material.LooseSoil);

        Assert.DoesNotThrow(() => grid.CarveCircle(0, 0, 10));
        Assert.DoesNotThrow(() => grid.CarveCircle(31, 31, 10));
        Assert.DoesNotThrow(() => grid.CarveCircle(-50, -50, 5));

        Assert.That(grid[0, 0], Is.EqualTo(Material.Air));
    }

    [Test]
    public void ZeroRadiusCarvesNothing()
    {
        TerrainGrid grid = new TerrainGrid(16, 16);
        grid.FillRectangle(0, 0, 16, 16, Material.PackedSoil);

        Assert.That(grid.CarveCircle(8, 8, 0), Is.Zero);
        Assert.That(grid[8, 8], Is.EqualTo(Material.PackedSoil));
    }

    [Test]
    public void DepositFillsAirButNotSolidGroundByDefault()
    {
        TerrainGrid grid = new TerrainGrid(64, 64);
        grid.FillRectangle(0, 40, 64, 10, Material.PackedSoil);

        grid.DepositCircle(32, 38, 6, Material.LooseSoil);

        Assert.Multiple(() =>
        {
            Assert.That(grid[32, 36], Is.EqualTo(Material.LooseSoil), "filled the air above");
            Assert.That(grid[32, 44], Is.EqualTo(Material.PackedSoil), "left the packed soil alone");
        });
    }

    [Test]
    public void DepositNeverOverwritesBedrockEvenWhenForced()
    {
        TerrainGrid grid = new TerrainGrid(32, 32);
        grid.FillRectangle(0, 16, 32, 4, Material.Bedrock);

        grid.DepositCircle(16, 17, 8, Material.LooseSoil, overwriteSolid: true);

        Assert.That(grid[16, 17], Is.EqualTo(Material.Bedrock));
    }

    [Test]
    public void CopyToHandsOutCellsWithoutExposingTheArray()
    {
        TerrainGrid grid = new TerrainGrid(8, 4);
        grid.Set(3, 2, Material.RootMat);

        byte[] copy = new byte[grid.CellCount];
        grid.CopyTo(copy);

        Assert.That(copy[(2 * 8) + 3], Is.EqualTo((byte)Material.RootMat));

        // Mutating the copy must not reach back into the grid.
        copy[0] = (byte)Material.Bedrock;
        Assert.That(grid[0, 0], Is.EqualTo(Material.Air));
    }

    [Test]
    public void CopyToRejectsAnUndersizedDestination()
    {
        TerrainGrid grid = new TerrainGrid(8, 8);
        Assert.Throws<ArgumentException>(() => grid.CopyTo(new byte[10]));
    }

    [Test]
    public void MaterialTableMatchesTheDesignDefaults()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MaterialTable.CostPerMetre(Material.Air).ToDecimal(), Is.EqualTo(1.5m));
            Assert.That(MaterialTable.CostPerMetre(Material.Turf).ToDecimal(), Is.EqualTo(1.5m));
            Assert.That(MaterialTable.CostPerMetre(Material.LooseSoil).ToDecimal(), Is.EqualTo(4m));
            Assert.That(MaterialTable.CostPerMetre(Material.PackedSoil).ToDecimal(), Is.EqualTo(7m));
            Assert.That(MaterialTable.CostPerMetre(Material.RootMat).ToDecimal(), Is.EqualTo(12m));

            Assert.That(MaterialTable.IsSolid(Material.Air), Is.False);
            Assert.That(MaterialTable.IsSolid(Material.Turf), Is.True);
            Assert.That(MaterialTable.IsDiggable(Material.Bedrock), Is.False);
            Assert.That(MaterialTable.IsPassable(Material.Bedrock), Is.False);
            Assert.That(MaterialTable.IsPassable(Material.RootMat), Is.True);
        });
    }

    [Test]
    public void OneRoundTurnBudgetBuysTheDesignedReach()
    {
        // The headline numbers from the design document, checked against the cost table so
        // that retuning a cost without revisiting the design is caught here.
        // 100 stamina, cells of 5 cm.
        Fix64 budget = Fix64.FromInt(100);

        Fix64 surfaceMetres = budget / MaterialTable.CostPerMetre(Material.Turf);
        Fix64 looseMetres = budget / MaterialTable.CostPerMetre(Material.LooseSoil);
        Fix64 packedMetres = budget / MaterialTable.CostPerMetre(Material.PackedSoil);
        Fix64 rootMetres = budget / MaterialTable.CostPerMetre(Material.RootMat);

        Assert.Multiple(() =>
        {
            // The surface run is capped by the 8-second clock at 40 m, not by stamina,
            // so the budget must buy more than that.
            Assert.That(surfaceMetres, Is.GreaterThan(Fix64.FromInt(40)));
            Assert.That(looseMetres.ToDecimal(), Is.EqualTo(25m).Within(0.01m));
            Assert.That(packedMetres.ToDecimal(), Is.EqualTo(14.2857m).Within(0.01m));
            Assert.That(rootMetres.ToDecimal(), Is.EqualTo(8.3333m).Within(0.01m));
        });
    }
}
