using System.Collections.Generic;
using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

/// <summary>
/// Steering a mole through its turn, which is how a plan gets made.
/// </summary>
/// <remarks>
/// The whole point of walking the turn out rather than drawing it is that what the player sees
/// is what the round will do. That promise is only worth anything if the waypoints left behind
/// actually retrace the walk when the simulation replays them, which is what
/// <see cref="TheRouteItLeavesRetracesTheWalk"/> is here to hold on to. Everything else defends
/// the budget: time is spent by moving and by falling, and by nothing else.
/// </remarks>
[TestFixture]
public sealed class SteeredWalkTests
{
    private const int WidthCells = 900;
    private const int HeightCells = 400;
    private const int SurfaceCell = 100;

    private static TerrainGrid FlatField()
    {
        TerrainGrid grid = new TerrainGrid(WidthCells, HeightCells);
        grid.FillRectangle(0, SurfaceCell, WidthCells, 3, Material.Turf);
        grid.FillRectangle(0, SurfaceCell + 3, WidthCells, 34, Material.LooseSoil);
        grid.FillRectangle(0, SurfaceCell + 37, WidthCells, HeightCells - SurfaceCell - 47, Material.PackedSoil);
        grid.FillRectangle(0, HeightCells - 10, WidthCells, 10, Material.Bedrock);
        return grid;
    }

    private static MoleMatch NewMatch(int playerCount = 2) =>
        MoleMatch.Create(FlatField(), playerCount, 20260826UL);

    private static Mole FirstActor(MoleMatch match)
    {
        foreach (Mole candidate in match.Eligible(0))
        {
            return candidate;
        }

        return match.Moles[0];
    }

    private static void Push(SteeredWalk walk, Vec2 direction, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            walk.Advance(direction);
        }
    }

    // ---- The budget -----------------------------------------------------------------

    [Test]
    public void StandingStillCostsNothing()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.Zero, 500);

        // A player who spends the planning phase thinking has spent none of the round. The
        // eight seconds are eight seconds of walking, not eight seconds of wall clock.
        Assert.Multiple(() =>
        {
            Assert.That(walk.TicksUsed, Is.Zero);
            Assert.That(walk.StaminaSpent, Is.EqualTo(Fix64.Zero));
            Assert.That(walk.HasMoved, Is.False);
        });
    }

    [Test]
    public void PushingWalksTheMoleAndSpendsTheBudget()
    {
        MoleMatch match = NewMatch();
        Mole actor = FirstActor(match);
        Vec2 from = actor.Position;
        SteeredWalk walk = SteeredWalk.From(actor, match.Terrain);

        Push(walk, Vec2.UnitX, 60);

        Assert.Multiple(() =>
        {
            Assert.That(walk.TicksUsed, Is.EqualTo(60));
            Assert.That(walk.Position.X, Is.GreaterThan(from.X + Fix64.FromInt(5)));
            Assert.That(walk.StaminaSpent, Is.GreaterThan(Fix64.Zero));
            Assert.That(walk.HasMoved, Is.True);
        });
    }

    [Test]
    public void TheWalkCannotRunPastTheEndOfTheRound()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.UnitX, MatchSettings.TicksPerRound * 2);

        Assert.Multiple(() =>
        {
            Assert.That(walk.TicksUsed, Is.EqualTo(MatchSettings.TicksPerRound));
            Assert.That(walk.HasTimeLeft, Is.False);
        });
    }

    [Test]
    public void DiggingRunsTheMoleOutOfPuffLongBeforeWalkingWould()
    {
        MoleMatch match = NewMatch();
        SteeredWalk digging = SteeredWalk.From(FirstActor(match), match.Terrain);
        SteeredWalk strolling = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(digging, Vec2.UnitY, 90);
        Push(strolling, Vec2.UnitX, 90);

        // Dirt is dearer, not slower, which is the one rule the whole stamina economy exists
        // to express. Ninety ticks straight down should cost far more than ninety along the top.
        Assert.That(digging.StaminaSpent, Is.GreaterThan(strolling.StaminaSpent));
    }

    [Test]
    public void AMoleWalkedOffALedgeKeepsFallingWithNobodyPushing()
    {
        TerrainGrid grid = FlatField();

        // A cliff, with the far side dug out from the surface all the way down.
        grid.FillRectangle(500, SurfaceCell, WidthCells - 500, HeightCells - SurfaceCell - 10, Material.Air);

        MoleMatch match = MoleMatch.Create(grid, 2, 20260826UL);
        Mole actor = FirstActor(match);
        actor.Position = new Vec2(
            WorldScale.ToCentreMetres(480), WorldScale.ToCentreMetres(SurfaceCell - 7));

        SteeredWalk walk = SteeredWalk.From(actor, match.Terrain);
        Push(walk, Vec2.UnitX, 30);

        Fix64 leftTheEdge = walk.Position.Y;
        int ticksWalking = walk.TicksUsed;

        Push(walk, Vec2.Zero, 40);

        // Falling is not something the mole chose and not something it can decline, so the
        // ticks go by whether or not anybody is asking it to move.
        Assert.Multiple(() =>
        {
            Assert.That(walk.TicksUsed, Is.GreaterThan(ticksWalking));
            Assert.That(walk.Position.Y, Is.GreaterThan(leftTheEdge));
        });
    }

    // ---- What it leaves alone -------------------------------------------------------

    [Test]
    public void TryingSomethingOutMovesNeitherTheMoleNorTheMap()
    {
        MoleMatch match = NewMatch();
        Mole actor = FirstActor(match);
        Vec2 stood = actor.Position;
        Fix64 stamina = actor.Stamina;
        ulong before = match.Terrain.Hash;

        SteeredWalk walk = SteeredWalk.From(actor, match.Terrain);
        Push(walk, Vec2.UnitY, 120);

        Assert.Multiple(() =>
        {
            Assert.That(actor.Position, Is.EqualTo(stood));
            Assert.That(actor.Stamina, Is.EqualTo(stamina));

            // A tunnel dug while deciding whether to dig it would be a tunnel everybody else
            // could walk down, which is a rather larger gift than a planning screen should give.
            Assert.That(match.Terrain.Hash, Is.EqualTo(before));
            Assert.That(walk.Position.Y, Is.GreaterThan(stood.Y));
        });
    }

    // ---- The waypoints it leaves behind ---------------------------------------------

    [Test]
    public void WaypointsAreSpacedFarEnoughApartToBeFollowedOneAtATime()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.UnitX, 200);
        List<Vec2> route = walk.Waypoints();

        Assert.That(route, Is.Not.Empty);

        // Two waypoints inside one arrival radius are consumed in the same tick, and the second
        // of them silently does nothing. Spacing them is what makes the route replay as walked.
        for (int index = 1; index < route.Count - 1; index++)
        {
            Assert.That(
                Vec2.Distance(route[index - 1], route[index]),
                Is.GreaterThan(MatchSettings.Radius),
                $"waypoints {index - 1} and {index} are too close to be told apart");
        }
    }

    [Test]
    public void AWalkThatWentNowhereLeavesNoRouteAtAll()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.Zero, 100);

        Assert.That(walk.Waypoints(), Is.Empty);
    }

    [Test]
    public void TheRouteEndsWhereTheWalkEnded()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        // Deliberately not a whole number of spacings, so the tail waypoint is the thing under
        // test rather than an accident of where the last full one landed.
        Push(walk, Vec2.UnitX, 37);
        List<Vec2> route = walk.Waypoints();

        Assert.That(
            Vec2.Distance(route[route.Count - 1], walk.Position),
            Is.LessThanOrEqualTo(MatchSettings.Radius));
    }

    [Test]
    public void TheRouteItLeavesRetracesTheWalk()
    {
        MoleMatch match = NewMatch();
        Mole actor = FirstActor(match);
        SteeredWalk walk = SteeredWalk.From(actor, match.Terrain);

        Push(walk, Vec2.UnitX, 70);
        Push(walk, new Vec2(Fix64.Ratio(7, 10), Fix64.Ratio(7, 10)), 30);

        Vec2 steeredTo = walk.Position;
        List<Vec2> waypoints = walk.Waypoints();
        RoutePoint[] route = new RoutePoint[waypoints.Count];

        for (int index = 0; index < waypoints.Count; index++)
        {
            route[index] = RoutePoint.FromWorld(waypoints[index]);
        }

        match.SubmitPlan(
            new Plan(actor.Seat, actor.Index, WeaponId.ClodLobber, route, System.Array.Empty<PlanAction>()));

        RoundResult result = match.ResolveRound(record: true);
        int slot = match.Moles.ToList().IndexOf(actor);

        // The promise the planning screen makes: where you steered it to is where it goes. The
        // tolerance is a body length, which is the granularity a waypoint is followed at anyway.
        Assert.That(
            Vec2.Distance(result.Recording!.PositionOf(walk.TicksUsed, slot), steeredTo),
            Is.LessThan(MatchSettings.Radius * Fix64.FromInt(3)));
    }
}
