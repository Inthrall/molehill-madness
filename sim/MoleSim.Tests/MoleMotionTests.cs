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

    /// <summary>
    /// A mole underground can dig straight up, and gets somewhere for what it spends.
    /// </summary>
    /// <remarks>
    /// Here because this broke three times. Digging upward was impossible and cost stamina to
    /// attempt, which is the worst pair of properties a control can have, and each of the three fixes
    /// looked right until the tick after it. The failure is never in the digging: the carve happens
    /// and the mole moves up. It is that a mole standing in the clear shaft it just made is neither
    /// blocked nor standing on anything, so the ground-follow at the end of the move and the check at
    /// the top of the next tick both pull it back down. Fixing one leaves the other.
    ///
    /// Measured before the fix: zero cells of rise for sixty stamina of a hundred. The numbers below
    /// are deliberately generous, because what is being defended is "it works at all", but the rise
    /// has to be big enough that a snap-back cannot pass as progress.
    /// </remarks>
    [Test]
    public void AMoleUndergroundCanDigStraightUp()
    {
        TerrainGrid grid = FlatGround();
        Mole mole = Buried(grid, 100, SurfaceCell + 160);
        Fix64 startY = mole.Position.Y;

        // A waypoint two metres overhead, renewed every tick, which is what a held key becomes once
        // the steering has turned it into a push.
        for (int tick = 0; tick < MatchSettings.TicksPerRound; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(mole, grid, new[] { new Vec2(mole.Position.X, mole.Position.Y - Fix64.FromInt(2)) });
        }

        Fix64 rose = startY - mole.Position.Y;
        int cells = Fix64.ToInt(rose * Fix64.FromInt(WorldScale.CellsPerMetre));

        Assert.Multiple(() =>
        {
            Assert.That(cells, Is.GreaterThan(40), "a round of holding up should get somewhere");
            Assert.That(mole.IsAirborne, Is.False, "it should be in its shaft, not falling down it");
        });
    }

    /// <summary>
    /// And it stays where it dug rather than sliding back down the shaft.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule, and the reason it is bracing rather than intent. A mole in a
    /// body-width shaft is wedged against both walls, so letting go of the key leaves it where it is.
    /// A rule that instead asked which way the player was pushing would have passed the test above
    /// and failed this one, by dropping the mole the moment it stopped being told to climb.
    /// </remarks>
    [Test]
    public void AMoleThatHasDugUpwardStaysInItsShaft()
    {
        TerrainGrid grid = FlatGround();
        Mole mole = Buried(grid, 100, SurfaceCell + 160);

        for (int tick = 0; tick < 60; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(mole, grid, new[] { new Vec2(mole.Position.X, mole.Position.Y - Fix64.FromInt(2)) });
        }

        Fix64 climbedTo = mole.Position.Y;

        // Nobody pushing anything, for a good while.
        for (int tick = 0; tick < 120; tick++)
        {
            MoleMotion.Step(mole, grid, route: null);
        }

        Assert.Multiple(() =>
        {
            Assert.That(mole.Position.Y, Is.EqualTo(climbedTo), "it slid back down its own shaft");
            Assert.That(mole.IsAirborne, Is.False, "it fell");
        });
    }

    /// <summary>
    /// Bracing is not a way to hang in the open air.
    /// </summary>
    /// <remarks>
    /// The objection to the whole idea, checked rather than argued. If a shaft holds a mole up then
    /// the rule has to be about the walls and not about the pushing, or a mole in mid-air would hold
    /// itself there the same way. There are no walls in the sky, so it falls.
    /// </remarks>
    [Test]
    public void BracingCannotHoldAMoleUpInMidAir()
    {
        TerrainGrid grid = FlatGround();
        Vec2 high = new Vec2(
            WorldScale.ToCentreMetres(100),
            WorldScale.ToMetres(SurfaceCell) - Fix64.FromInt(10));

        Mole mole = new Mole(seat: 0, index: 0, high);

        for (int tick = 0; tick < 30; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(mole, grid, new[] { new Vec2(mole.Position.X, mole.Position.Y - Fix64.FromInt(2)) });
        }

        Assert.That(mole.Position.Y, Is.GreaterThan(high.Y), "it should have fallen, not hung there");
    }

    /// <summary>Puts a mole in a body-sized hole at a depth, which is where a tunnelling one sits.</summary>
    private static Mole Buried(TerrainGrid grid, int cellX, int cellY)
    {
        Vec2 position = new Vec2(WorldScale.ToCentreMetres(cellX), WorldScale.ToMetres(cellY));

        TerrainQuery.CarveBody(grid, position, MatchSettings.Radius);

        Mole mole = new Mole(seat: 0, index: 0, position);
        mole.BeginRound();
        return mole;
    }

    /// <summary>
    /// A hop can be steered sideways while it is in the air.
    /// </summary>
    /// <remarks>
    /// A jump used to be a ballistic arc nobody could influence once it started, because the route
    /// was withheld from the solver for as long as a mole was off the ground. Asserted against a
    /// hop with nothing held, so the test says the push is what moved it rather than that it moved.
    /// </remarks>
    [Test]
    public void AHopCanBeSteeredInTheAir()
    {
        TerrainGrid grid = FlatGround();

        Fix64 drifted = Hopped(grid, lean: 0);
        Fix64 steered = Hopped(FlatGround(), lean: 1);

        Assert.Multiple(() =>
        {
            Assert.That(drifted.ToDecimal(), Is.EqualTo(0m).Within(0.05m), "an unsteered hop went sideways");
            Assert.That(steered.ToDecimal(), Is.GreaterThan(2m), "a steered hop went nowhere");
        });
    }

    /// <summary>
    /// Landing is decided by how fast a mole is closing on the ground, not how fast it is going.
    /// </summary>
    /// <remarks>
    /// The two were the same test until a mole could steer in the air, and then they were not: air
    /// control adds up to walking pace sideways, which by itself exceeds the settle speed, so a mole
    /// falling onto flat ground with a direction held never slowed enough to land. Measured, it
    /// skittered along the surface, permanently airborne, for the rest of the round.
    /// </remarks>
    [Test]
    public void AMoleFallingWhileSteeredStillLands()
    {
        TerrainGrid grid = FlatGround();
        Vec2 high = new Vec2(
            WorldScale.ToCentreMetres(100),
            WorldScale.ToMetres(SurfaceCell) - Fix64.FromInt(6));

        Mole mole = new Mole(seat: 0, index: 0, high);

        for (int tick = 0; tick < 90; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(
                mole, grid,
                new[] { new Vec2(mole.Position.X + Fix64.FromInt(2), mole.Position.Y) });
        }

        Assert.That(mole.IsAirborne, Is.False, "it never settled");
    }

    /// <summary>
    /// A mole that jumps into a ceiling while pushing at it digs in rather than bouncing off.
    /// </summary>
    /// <remarks>
    /// The design's rule is that surface and underground are one seamless move and only the price
    /// changes; being off the ground was the one case that never obeyed it, because a jump could
    /// only ever bounce.
    ///
    /// This caught the fault that made the first attempt do nothing. A body counts as blocked as
    /// soon as anything solid comes within its radius, so at the moment of contact its own centre
    /// cell is still open air; asking what material is there answers air, which is not diggable, and
    /// every contact declined the dig. The material has to be sampled a radius back along the escape
    /// direction, which is the thing actually being hit.
    /// </remarks>
    [Test]
    public void AJumpIntoACeilingBecomesADig()
    {
        TerrainGrid grid = FlatGround();

        // A roof with thirteen cells of clearance over the mole's head.
        grid.FillRectangle(0, SurfaceCell - 46, WidthCells, 20, Material.PackedSoil);

        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startY = mole.Position.Y;
        Fix64 stamina = mole.Stamina;

        mole.AddImpulse(-Vec2.UnitY * MatchSettings.HopSpeed);

        for (int tick = 0; tick < 90; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(
                mole, grid,
                new[] { new Vec2(mole.Position.X, mole.Position.Y - Fix64.FromInt(2)) });
        }

        int rose = Fix64.ToInt((startY - mole.Position.Y) * Fix64.FromInt(WorldScale.CellsPerMetre));

        Assert.Multiple(() =>
        {
            Assert.That(rose, Is.GreaterThan(20), "it never got up through the roof");
            Assert.That(
                stamina - mole.Stamina, Is.GreaterThan(Fix64.Zero),
                "it got through without paying to dig, so it did not dig");
        });
    }

    /// <summary>
    /// And falling onto a floor does not dig, however hard somebody is pushing.
    /// </summary>
    /// <remarks>
    /// The other half of the rule, and the one that keeps it from being a nuisance. Digging on
    /// contact is gated on the surface not being a floor, or every landing would punch a hole in the
    /// ground the mole was trying to land on.
    ///
    /// Pushed sideways rather than downward, and the first version of this test got that wrong. Held
    /// downward, a mole lands and then digs down, which is not the contact rule misfiring: it is the
    /// walking solver doing exactly what the design asks of it, since a route that points down is an
    /// instruction to tunnel. What is being checked here is that arriving at a floor is not itself a
    /// reason to dig.
    /// </remarks>
    [Test]
    public void FallingOntoTheGroundDoesNotDigThroughIt()
    {
        TerrainGrid grid = FlatGround();
        Vec2 high = new Vec2(
            WorldScale.ToCentreMetres(100),
            WorldScale.ToMetres(SurfaceCell) - Fix64.FromInt(6));

        Mole mole = new Mole(seat: 0, index: 0, high);
        ulong before = grid.Hash;

        for (int tick = 0; tick < 90; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(
                mole, grid,
                new[] { new Vec2(mole.Position.X + Fix64.FromInt(2), mole.Position.Y) });
        }

        Assert.Multiple(() =>
        {
            Assert.That(grid.Hash, Is.EqualTo(before), "landing carved the ground");
            Assert.That(
                mole.Position.Y, Is.LessThan(WorldScale.ToMetres(SurfaceCell)),
                "it ended up under the surface");
        });
    }

    /// <summary>Hops a mole off flat ground, optionally leaning, and reports how far it travelled.</summary>
    private static Fix64 Hopped(TerrainGrid grid, int lean)
    {
        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startX = mole.Position.X;

        mole.AddImpulse(-Vec2.UnitY * MatchSettings.HopSpeed);

        for (int tick = 0; tick < 60 && mole.IsAirborne; tick++)
        {
            Vec2[]? route = lean == 0
                ? null
                : new[] { new Vec2(mole.Position.X + Fix64.FromInt(2 * lean), mole.Position.Y) };

            mole.WaypointIndex = 0;
            MoleMotion.Step(mole, grid, route);
        }

        return mole.Position.X - startX;
    }

    /// <summary>
    /// A jump into a ceiling right overhead still gets somewhere, on the jump's own momentum.
    /// </summary>
    /// <remarks>
    /// The case the first version of contact-digging was worst at, and the reason it now keeps its
    /// velocity. Hitting a roof used to stop a mole dead and hand it to the walking solver, so a hop
    /// into a ceiling bought one body length of tunnel however hard it was going, and with only a
    /// hand's breadth of clearance it measured at minus one cell for twenty-three stamina: it dug,
    /// fell back down its own shaft, and ground away at it for the rest of the round.
    ///
    /// With the rise carried through it is thirty-eight cells for thirteen. Cheaper as well as
    /// further, which is the tell that the jump is doing the work rather than the digging.
    /// </remarks>
    [Test]
    public void AJumpWithNoHeadroomStillCarriesIntoTheCeiling()
    {
        TerrainGrid grid = FlatGround();

        // Seven cells of clearance over the mole's head, which is about a hand's breadth.
        grid.FillRectangle(0, SurfaceCell - 40, WidthCells, 20, Material.PackedSoil);

        Mole mole = StandingOnSurface(grid, 100);
        Fix64 startY = mole.Position.Y;

        mole.AddImpulse(-Vec2.UnitY * MatchSettings.HopSpeed);

        for (int tick = 0; tick < 90; tick++)
        {
            mole.WaypointIndex = 0;
            MoleMotion.Step(
                mole, grid,
                new[] { new Vec2(mole.Position.X, mole.Position.Y - Fix64.FromInt(2)) });
        }

        int rose = Fix64.ToInt((startY - mole.Position.Y) * Fix64.FromInt(WorldScale.CellsPerMetre));

        Assert.That(rose, Is.GreaterThan(20), "the jump was thrown away on impact");
    }
}
