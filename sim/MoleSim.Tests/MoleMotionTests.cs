using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

/// <summary>
/// The movement solver, checked against the numbers the design document actually
/// promises rather than against whatever the code happens to do.
/// </summary>
[TestFixture]
public sealed class MoleMotionTests
{
    private const int WidthCells = 1200;
    private const int HeightCells = 400;
    private const int SurfaceCell = 96;

    /// <summary>Flat turf over loose soil over packed soil, with bedrock along the floor.</summary>
    private static TerrainGrid FlatGround()
    {
        TerrainGrid grid = new TerrainGrid(WidthCells, HeightCells);
        grid.FillRectangle(0, SurfaceCell, WidthCells, 3, Material.Turf);
        grid.FillRectangle(0, SurfaceCell + 3, WidthCells, 40, Material.LooseSoil);
        grid.FillRectangle(0, SurfaceCell + 43, WidthCells, HeightCells - SurfaceCell - 51, Material.PackedSoil);
        grid.FillRectangle(0, HeightCells - 8, WidthCells, 8, Material.Bedrock);
        return grid;
    }

    /// <summary>Places a mole standing on the surface at the given horizontal cell.</summary>
    private static Mole StandingOnSurface(TerrainGrid grid, int cellX)
    {
        Vec2 position = new Vec2(
            WorldScale.ToCentreMetres(cellX),
            WorldScale.ToMetres(SurfaceCell) - MatchSettings.Radius - WorldScale.CellSize);

        Mole mole = new Mole(seat: 0, index: 0, position);

        // Let it settle onto the ground before anything is measured.
        for (int tick = 0; tick < 10; tick++)
        {
            MoleMotion.Step(mole, grid, route: null);
        }

        mole.BeginRound();
        return mole;
    }

    private static void RunRound(Mole mole, TerrainGrid grid, Vec2[] route, int ticks = MatchSettings.TicksPerRound)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            MoleMotion.Step(mole, grid, route);
        }
    }

    [Test]
    public void AFullSurfaceRunCoversFortyMetresForSixtyStamina()
    {
        // The design's headline number, checked end to end through the real solver:
        // eight seconds at five metres a second is forty metres, and turf costs 1.5 a
        // metre, so a full run home spends sixty of the hundred.
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startX = mole.Position.X;

        RunRound(mole, grid, new[] { new Vec2(startX + Fix64.FromInt(200), mole.Position.Y) });

        Fix64 travelled = mole.Position.X - startX;
        Fix64 spent = Fix64.FromInt(MatchSettings.StartingStamina) - mole.Stamina;

        Assert.Multiple(() =>
        {
            Assert.That(travelled.ToDecimal(), Is.EqualTo(40m).Within(0.5m), "distance covered");
            Assert.That(spent.ToDecimal(), Is.EqualTo(60m).Within(1m), "stamina spent");
            Assert.That(mole.IsAirborne, Is.False, "should still be walking, not falling");
        });
    }

    [Test]
    public void ARoundOfTunnellingReachesFourteenMetresAndRunsOutOfPuff()
    {
        // The other half of the same design table: packed soil at seven a metre means a
        // hundred stamina buys about 14.3 m, and the stamina runs out well before the
        // clock does. Dirt is not slower, it is dearer, and this is that rule holding.
        TerrainGrid grid = FlatGround();
        Vec2 start = new Vec2(
            WorldScale.ToCentreMetres(100),
            WorldScale.ToCentreMetres(SurfaceCell + 80));

        Mole mole = new Mole(seat: 0, index: 0, start);
        TerrainQuery.CarveBody(grid, start, MatchSettings.Radius);
        mole.BeginRound();

        RunRound(mole, grid, new[] { new Vec2(start.X + Fix64.FromInt(200), start.Y) });

        Fix64 travelled = mole.Position.X - start.X;

        Assert.Multiple(() =>
        {
            Assert.That(travelled.ToDecimal(), Is.EqualTo(14.3m).Within(0.6m), "distance dug");
            Assert.That(mole.Stamina.ToDecimal(), Is.LessThan(1m), "stamina should be spent");
        });
    }

    [Test]
    public void TunnellingLeavesATunnelBehindIt()
    {
        TerrainGrid grid = FlatGround();
        Vec2 start = new Vec2(
            WorldScale.ToCentreMetres(100),
            WorldScale.ToCentreMetres(SurfaceCell + 80));

        Mole mole = new Mole(seat: 0, index: 0, start);
        TerrainQuery.CarveBody(grid, start, MatchSettings.Radius);
        mole.BeginRound();

        RunRound(mole, grid, new[] { new Vec2(start.X + Fix64.FromInt(200), start.Y) });

        // Halfway along the route the ground should now be open, and a following mole
        // would pay air prices for it.
        Vec2 midway = new Vec2(start.X + Fix64.FromInt(5), start.Y);

        Assert.Multiple(() =>
        {
            Assert.That(TerrainQuery.MaterialAt(grid, midway), Is.EqualTo(Material.Air));
            Assert.That(MaterialTable.CostPerMetre(TerrainQuery.MaterialAt(grid, midway)),
                Is.EqualTo(MaterialTable.CostPerMetre(Material.Turf)));
        });
    }

    [Test]
    public void WalkingTheSurfaceDoesNotChewItUp()
    {
        // Without the step-up rule a mole would tunnel along every gentle slope and the
        // map would dissolve within a couple of rounds.
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        ulong before = grid.Hash;

        RunRound(mole, grid, new[] { new Vec2(mole.Position.X + Fix64.FromInt(200), mole.Position.Y) });

        Assert.That(grid.Hash, Is.EqualTo(before), "walking on the surface should carve nothing");
    }

    [Test]
    public void AGentleSlopeIsWalkedOverRatherThanDugThrough()
    {
        TerrainGrid grid = FlatGround();

        // A long ramp, one cell up every eight along, which is well inside the step
        // height. It has to keep climbing for the whole route: an earlier version of this
        // test used a ramp that levelled off, and the mole obligingly walked up it and
        // straight back down before anything was measured.
        for (int step = 0; step < 400; step++)
        {
            int top = SurfaceCell - (step / 8) - 1;
            grid.FillRectangle(140 + step, top, 1, SurfaceCell - top + 3, Material.Turf);
        }

        Mole mole = StandingOnSurface(grid, 100);
        ulong before = grid.Hash;
        Fix64 startY = mole.Position.Y;

        // The waypoint sits above the top of the ramp, so the route never points downward
        // and the mole is never being asked to dig. Aiming at the starting height instead
        // would put the target underground once the mole had climbed, and digging down to
        // reach it would be the right answer to the wrong question.
        RunRound(mole, grid, new[]
        {
            new Vec2(mole.Position.X + Fix64.FromInt(20), mole.Position.Y - Fix64.FromInt(4)),
        });

        Assert.Multiple(() =>
        {
            Assert.That(grid.Hash, Is.EqualTo(before), "a walkable slope should not be carved");
            Assert.That(mole.Position.Y, Is.LessThan(startY), "the mole should have climbed");
        });
    }

    [Test]
    public void ARouteAimedDownwardDigsInsteadOfSteppingOver()
    {
        // The step-up rule and the dig rule pull in opposite directions, and step-up used
        // to win unconditionally: a mole told to tunnel found clear air just above the
        // ground it was aiming at, stepped into it, and burrowed precisely nowhere. The
        // tests all passed, because none of them pointed a route downward.
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startY = mole.Position.Y;
        ulong before = grid.Hash;

        RunRound(mole, grid, new[]
        {
            new Vec2(mole.Position.X + Fix64.FromInt(6), mole.Position.Y + Fix64.FromInt(8)),
        });

        Assert.Multiple(() =>
        {
            Assert.That(grid.Hash, Is.Not.EqualTo(before), "digging down should carve");
            Assert.That(mole.Position.Y, Is.GreaterThan(startY + Fix64.FromInt(2)),
                "and should actually get underground");
            Assert.That(mole.IsAirborne, Is.False, "a tunnel holds a mole up");
        });
    }

    [Test]
    public void RunningOutOfStaminaStopsTheMoleWhereItStands()
    {
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        mole.BeginRound();
        mole.Stamina = Fix64.FromInt(15);

        RunRound(mole, grid, new[] { new Vec2(mole.Position.X + Fix64.FromInt(200), mole.Position.Y) });

        Fix64 travelled = mole.Position.X - WorldScale.ToCentreMetres(100);

        // Fifteen stamina of turf at 1.5 a metre is ten metres, not the forty the clock
        // would otherwise have allowed.
        Assert.That(travelled.ToDecimal(), Is.EqualTo(10m).Within(0.6m));
    }

    [Test]
    public void BedrockWillNotBeDug()
    {
        TerrainGrid grid = new TerrainGrid(400, 200);
        grid.FillRectangle(0, 100, 400, 100, Material.PackedSoil);
        grid.FillRectangle(200, 0, 6, 200, Material.Bedrock);

        Vec2 start = new Vec2(WorldScale.ToCentreMetres(150), WorldScale.ToCentreMetres(120));
        Mole mole = new Mole(seat: 0, index: 0, start);
        TerrainQuery.CarveBody(grid, start, MatchSettings.Radius);
        mole.BeginRound();

        RunRound(mole, grid, new[] { new Vec2(start.X + Fix64.FromInt(20), start.Y) });

        Assert.Multiple(() =>
        {
            Assert.That(mole.Position.X, Is.LessThan(WorldScale.ToMetres(200)), "should not pass the wall");
            Assert.That(grid[203, 120], Is.EqualTo(Material.Bedrock), "the wall should be untouched");
        });
    }

    [Test]
    public void AMoleWithNothingBeneathItFalls()
    {
        TerrainGrid grid = FlatGround();
        Vec2 start = new Vec2(WorldScale.ToCentreMetres(100), WorldScale.ToMetres(20));
        Mole mole = new Mole(seat: 0, index: 0, start);
        mole.BeginRound();

        MoleMotion.Step(mole, grid, route: null);

        Assert.That(mole.IsAirborne, Is.True);

        RunRound(mole, grid, route: null!);

        Assert.Multiple(() =>
        {
            Assert.That(mole.IsAirborne, Is.False, "it should have landed by now");
            Assert.That(mole.Position.Y, Is.LessThan(WorldScale.ToMetres(SurfaceCell)), "and be above the turf");
            Assert.That(mole.Position.Y, Is.GreaterThan(start.Y), "having fallen");
        });
    }

    [Test]
    public void AMoleWalkingIntoAFreshCraterFallsInAndClimbsOut()
    {
        // "Intents replay through chaos": the plan is not cancelled by the world changing
        // underneath it, only by taking damage.
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startX = mole.Position.X;

        // Somebody blows a hole across the route before the mole gets there.
        grid.CarveCircle(150, SurfaceCell + 6, 28);

        RunRound(mole, grid, new[] { new Vec2(startX + Fix64.FromInt(30), mole.Position.Y) });

        Assert.Multiple(() =>
        {
            Assert.That(mole.Position.X, Is.GreaterThan(WorldScale.ToMetres(178)),
                "the crater should not have stopped it for good");
            Assert.That(mole.IsOffDuty, Is.False);
        });
    }

    [Test]
    public void DamageEndsTheMolesGoImmediately()
    {
        TerrainGrid grid = FlatGround();
        Mole mole = StandingOnSurface(grid, 100);
        Vec2[] route = { new Vec2(mole.Position.X + Fix64.FromInt(200), mole.Position.Y) };

        RunRound(mole, grid, route, ticks: 30);
        Fix64 atHalfway = mole.Position.X;

        mole.TakeDamage(20);
        RunRound(mole, grid, route, ticks: 120);

        Assert.Multiple(() =>
        {
            Assert.That(mole.InputCancelled, Is.True);
            Assert.That(mole.Position.X, Is.EqualTo(atHalfway), "it should not have moved a step further");
            Assert.That(mole.Pluck, Is.EqualTo(80));
        });
    }

    [Test]
    public void DamageThatEmptiesPluckSendsTheMoleOffDuty()
    {
        Mole mole = new Mole(seat: 0, index: 0, Vec2.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(mole.TakeDamage(40), Is.False);
            Assert.That(mole.Pluck, Is.EqualTo(60));
            Assert.That(mole.TakeDamage(60), Is.True, "the blow that empties it reports the knockout");
            Assert.That(mole.Pluck, Is.Zero, "pluck never goes negative");
            Assert.That(mole.IsOffDuty, Is.True);
            Assert.That(mole.TakeDamage(10), Is.False, "a mole already off duty is not knocked out twice");
        });
    }

    [Test]
    public void ARoundBeginsWithAFullTankAndAFreshPlan()
    {
        Mole mole = new Mole(seat: 0, index: 0, Vec2.Zero);
        mole.Stamina = Fix64.Zero;
        mole.InputCancelled = true;
        mole.WaypointIndex = 7;

        mole.BeginRound();

        Assert.Multiple(() =>
        {
            Assert.That(mole.Stamina, Is.EqualTo(Fix64.FromInt(100)));
            Assert.That(mole.InputCancelled, Is.False);
            Assert.That(mole.WaypointIndex, Is.Zero);
        });
    }

    [Test]
    public void MovementIsIdenticalEveryTimeItIsRun()
    {
        // The property the whole architecture rests on, at the level of one mole.
        Fix64 firstX = Fix64.Zero;
        Fix64 firstY = Fix64.Zero;
        ulong firstHash = 0;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            TerrainGrid grid = FlatGround();
            Mole mole = StandingOnSurface(grid, 100);

            RunRound(mole, grid, new[]
            {
                new Vec2(mole.Position.X + Fix64.FromInt(12), mole.Position.Y + Fix64.FromInt(4)),
                new Vec2(mole.Position.X + Fix64.FromInt(25), mole.Position.Y - Fix64.FromInt(2)),
            });

            if (attempt == 0)
            {
                firstX = mole.Position.X;
                firstY = mole.Position.Y;
                firstHash = grid.Hash;
                continue;
            }

            Assert.Multiple(() =>
            {
                Assert.That(mole.Position.X, Is.EqualTo(firstX), "x drifted between runs");
                Assert.That(mole.Position.Y, Is.EqualTo(firstY), "y drifted between runs");
                Assert.That(grid.Hash, Is.EqualTo(firstHash), "the terrain drifted between runs");
            });
        }
    }
}
