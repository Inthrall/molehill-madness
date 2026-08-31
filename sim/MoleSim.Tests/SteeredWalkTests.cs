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

        // Inward, because ninety ticks is fifteen metres and a mole that starts near an edge runs
        // out of map: out of bounds reads as solid, so the stroll turns into a dig against the
        // boundary and costs the whole budget. This test used to pass by luck, on the first mole
        // happening to have room to its right, and stopped the moment the turn order rotated.
        Vec2 inward = FirstActor(match).Position.X
            > WorldScale.ToCentreMetres(WidthCells / 2)
            ? -Vec2.UnitX
            : Vec2.UnitX;

        Push(digging, Vec2.UnitY, 90);
        Push(strolling, inward, 90);

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

    // ---- The drill ------------------------------------------------------------------

    /// <summary>
    /// A Tunnel Torpedo drills in the preview, not only in the round.
    /// </summary>
    /// <remarks>
    /// The complaint, exactly: the drill did nothing in ghost mode. Weapon uses are carried out by
    /// the match at resolution, and the planning screen moves the ghost and nothing else, so the one
    /// weapon whose entire purpose is to move the mole twelve metres showed no movement at all while
    /// the move was being planned. Worse, the route the plan recorded was the route of a mole that
    /// had stood still.
    /// </remarks>
    [Test]
    public void TheGhostDrillsWhenAToredoIsOrdered()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);
        Fix64 startX = walk.Position.X;

        Assert.That(walk.Drill(Vec2.UnitX, byte.MaxValue), Is.True, "the ghost refused to drill");

        // Nothing held, which is what a thumb off the stick hands over. A drill runs anyway.
        Push(walk, Vec2.Zero, MatchSettings.TicksPerSecond * 2);

        Assert.That(
            walk.Position.X - startX, Is.GreaterThan(Fix64.FromInt(8)),
            "the ghost stayed put while the plan said it would travel twelve metres");
    }

    /// <summary>
    /// And it takes time rather than happening all at once.
    /// </summary>
    /// <remarks>
    /// The other half of the complaint. The whole tunnel used to be cut inside the tick the torpedo
    /// was ordered on, so there was nothing to watch, nothing to react to and nothing to learn from.
    /// A tick is checked partway through: the mole should be somewhere in the middle of its tunnel
    /// and not already at the end of it.
    /// </remarks>
    [Test]
    public void TheDrillTakesTimeRatherThanHappeningAtOnce()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);
        Fix64 startX = walk.Position.X;

        walk.Drill(Vec2.UnitX, byte.MaxValue);
        Push(walk, Vec2.Zero, 1);

        Fix64 afterOneTick = walk.Position.X - startX;

        Assert.Multiple(() =>
        {
            Assert.That(afterOneTick, Is.GreaterThan(Fix64.Zero), "it did not start");
            Assert.That(
                afterOneTick, Is.LessThan(MatchSettings.TorpedoRange / Fix64.FromInt(2)),
                "the whole drill happened in one tick, which is what this is here to prevent");
            Assert.That(walk.IsDrilling, Is.True, "it finished in a single tick");
        });
    }

    [Test]
    public void ADrillCannotBeSteeredOrJumpedOutOf()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        walk.Drill(Vec2.UnitX, byte.MaxValue);
        Fix64 startY = walk.Position.Y;

        // Pushing straight down mid-drill, and asking for a hop, neither of which a torpedo obeys.
        Assert.That(walk.Hop(), Is.False, "a hop interrupted the drill");

        Push(walk, Vec2.UnitY, 4);

        Assert.That(walk.Position.Y, Is.EqualTo(startY), "the drill was steered off its line");
    }

    // ---- The other movement tools ---------------------------------------------------

    /// <summary>
    /// Power Claws make digging cheap in the preview, not only in the round.
    /// </summary>
    /// <remarks>
    /// The claws do exactly one thing, and the planning screen is the only place that thing is ever
    /// shown as a number. Without this the gauge quoted the full price for a turn the round would
    /// charge a quarter of, so using them appeared to do nothing and there was no way to learn what
    /// they were for. Measured at 62.6 stamina previewed against 15.0 charged before this.
    /// </remarks>
    [Test]
    public void ClawsMakeDiggingCheapInThePreview()
    {
        MoleMatch match = NewMatch();
        SteeredWalk plain = SteeredWalk.From(FirstActor(match), match.Terrain);
        SteeredWalk clawed = SteeredWalk.From(FirstActor(match), match.Terrain);

        clawed.Claw();

        Push(plain, Vec2.UnitY, 40);
        Push(clawed, Vec2.UnitY, 40);

        Assert.That(
            clawed.StaminaSpent, Is.LessThan(plain.StaminaSpent / Fix64.FromInt(2)),
            "the claws changed nothing the player could see");
    }

    /// <summary>
    /// The claws only apply from the moment they are used, as they do at resolution.
    /// </summary>
    /// <remarks>
    /// Digging done before the claws come out is charged in full. Applying them to the whole turn
    /// would be a preview that flattered the plan, which is worse than one that overcharges it.
    /// </remarks>
    [Test]
    public void ClawsDoNotRefundDiggingAlreadyDone()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.UnitY, 20);
        Fix64 beforeClaws = walk.StaminaSpent;

        walk.Claw();
        Push(walk, Vec2.UnitY, 20);

        Assert.That(
            walk.StaminaSpent, Is.GreaterThan(beforeClaws),
            "the claws refunded the expensive half of the turn");
    }

    /// <summary>
    /// A sandbag dropped this turn exists in the preview's own world.
    /// </summary>
    /// <remarks>
    /// The preview walks a clone of the terrain taken when the turn began, so a bag dropped during
    /// it did not exist and a mole could not be planned onto something it was about to build.
    /// Standing on your own bag is most of the reason to carry one.
    /// </remarks>
    [Test]
    public void ASandbagDroppedInThePreviewIsThereToStandOn()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(FirstActor(match), match.Terrain);

        Push(walk, Vec2.UnitY, 20);
        Fix64 floor = walk.Position.Y;

        walk.DropSandbag();
        Push(walk, Vec2.Zero, 10);

        Assert.That(
            walk.Position.Y, Is.LessThanOrEqualTo(floor),
            "the mole sank through a bag that should have been under its feet");
    }

    // ---- Hazards in the preview -----------------------------------------------------

    /// <summary>
    /// A mole can plant a vent and be thrown into the air by it in the same turn.
    /// </summary>
    /// <remarks>
    /// The last of the invisible tools. A geyser cap arms in the round it is planted, so this was
    /// always possible and never showed: the vent existed only in the match, and the planning screen
    /// moves a ghost. A player could plant one, walk onto it, and see a mole standing calmly on a
    /// vent that would launch it the moment the round ran.
    /// </remarks>
    [Test]
    public void AGhostIsLaunchedByAVentItPlantedItself()
    {
        MoleMatch match = NewMatch();
        SteeredWalk walk = SteeredWalk.From(
            FirstActor(match), match.Terrain, match.Placements, match.Round);

        Fix64 startY = walk.Position.Y;

        walk.Plant(WeaponId.GeyserCap);
        Push(walk, Vec2.Zero, 6);

        Assert.Multiple(() =>
        {
            Assert.That(walk.Position.Y, Is.LessThan(startY), "the vent did not throw it");
            Assert.That(walk.IsFalling, Is.True, "it should be off the ground");
        });
    }

    /// <summary>
    /// A trap laid this turn does not catch the mole that laid it, because it arms later.
    /// </summary>
    /// <remarks>
    /// The other half of the vent case, and the reason the arming rules had to be shared rather than
    /// reimplemented: a preview that sprang your own trap on you would be worse than one that showed
    /// nothing at all.
    /// </remarks>
    [Test]
    public void ATrapLaidThisTurnDoesNotCatchItsOwner()
    {
        MoleMatch match = NewMatch();
        Mole actor = FirstActor(match);
        SteeredWalk walk = SteeredWalk.From(actor, match.Terrain, match.Placements, match.Round);

        walk.Plant(WeaponId.SnapTrap);
        Push(walk, Vec2.Zero, 10);

        Assert.That(walk.Pluck, Is.EqualTo(100), "it sprang its own trap");
    }

    /// <summary>
    /// The preview never spends the real placements.
    /// </summary>
    /// <remarks>
    /// The one way this could have gone badly wrong. A snap trap goes off by marking itself spent, so
    /// handing the ghost the real objects would mean that merely considering a route over a trap
    /// disarmed it for the whole match, and for every other player.
    /// </remarks>
    [Test]
    public void WalkingAGhostOntoATrapDoesNotDisarmTheRealOne()
    {
        MoleMatch match = NewMatch();
        Mole placer = FirstActor(match);

        match.SubmitPlan(new Plan(
            0, placer.Index, WeaponId.SnapTrap, System.Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(3, Vec2.UnitY, 0, WeaponId.SnapTrap) }));
        match.SubmitPlan(Plan.Idle(1, 0));
        match.ResolveRound();

        Assert.That(match.Placements, Is.Not.Empty, "nothing was placed");

        // A round later the trap is armed. Walk a ghost onto it, repeatedly.
        Mole next = FirstActor(match);
        next.Position = match.Placements[0].Position;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            SteeredWalk walk = SteeredWalk.From(
                next, match.Terrain, match.Placements, match.Round);

            Push(walk, Vec2.Zero, 5);
        }

        Assert.That(
            match.Placements[0].Spent, Is.False,
            "thinking about it disarmed the trap for everybody");
    }

    /// <summary>
    /// A girder is a plank a mole can walk out onto, and the mole is not inside it.
    /// </summary>
    /// <remarks>
    /// Both halves matter and the second is the one that is easy to get wrong. A deposit skips ground
    /// that is already solid, so a plank begun at the mole's own centre walls it in at one end of its
    /// own bridge, which is exactly how the sandbag used to behave before it was fixed.
    /// </remarks>
    [Test]
    public void AGirderIsSomethingToWalkOutOnto()
    {
        TerrainGrid grid = FlatField();

        // A chasm to bridge, starting well clear of the mole.
        grid.FillRectangle(360, SurfaceCell, 200, HeightCells - SurfaceCell - 10, Material.Air);

        MoleMatch match = MoleMatch.Create(grid, 2, 20260826UL);
        Mole actor = FirstActor(match);
        actor.Position = new Vec2(
            WorldScale.ToCentreMetres(350),
            WorldScale.ToMetres(SurfaceCell) - MatchSettings.Radius - WorldScale.CellSize);

        SteeredWalk walk = SteeredWalk.From(actor, grid);
        Fix64 startX = walk.Position.X;

        walk.LayGirder(Vec2.UnitX);
        Push(walk, Vec2.UnitX, 20);

        Assert.Multiple(() =>
        {
            Assert.That(
                walk.Position.X, Is.GreaterThan(startX + Fix64.One),
                "the mole could not walk out onto its own girder");
            Assert.That(walk.IsFalling, Is.False, "it fell through the plank");
        });
    }
}
