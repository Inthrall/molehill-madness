using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

[TestFixture]
public sealed class MoleMatchTests
{
    private const int WidthCells = 900;
    private const int HeightCells = 400;
    private const int SurfaceCell = 100;

    /// <summary>Flat ground, so positions in these tests are predictable.</summary>
    private static TerrainGrid FlatField()
    {
        TerrainGrid grid = new TerrainGrid(WidthCells, HeightCells);
        grid.FillRectangle(0, SurfaceCell, WidthCells, 3, Material.Turf);
        grid.FillRectangle(0, SurfaceCell + 3, WidthCells, 34, Material.LooseSoil);
        grid.FillRectangle(0, SurfaceCell + 37, WidthCells, HeightCells - SurfaceCell - 47, Material.PackedSoil);
        grid.FillRectangle(0, HeightCells - 10, WidthCells, 10, Material.Bedrock);
        return grid;
    }

    private static MoleMatch NewMatch(int playerCount = 2, ulong seed = 20260826UL) =>
        MoleMatch.Create(FlatField(), playerCount, seed);

    private static Mole MoleOf(MoleMatch match, int seat, int index) =>
        match.Moles.Single(mole => mole.Seat == seat && mole.Index == index);

    /// <summary>
    /// Throws one mole straight up on a chosen tick, and does nothing else.
    /// </summary>
    /// <remarks>
    /// The tick watcher is the only seam that reaches inside a round while it is running, which is
    /// what this needs. A round is eight seconds and nothing the physics can do keeps a mole up for
    /// that long, so the only way to have one still in the air at the end of a round is to launch
    /// it near the end of one.
    /// </remarks>
    private sealed class LateLauncher : MoleMatch.ITickWatcher
    {
        private readonly Mole _mole;
        private readonly int _at;

        public LateLauncher(Mole mole, int at)
        {
            _mole = mole;
            _at = at;
        }

        public bool Launched { get; private set; }

        public void Ticked(int round, int tick, MoleMatch match)
        {
            if (tick != _at)
            {
                return;
            }

            // Up is negative, and twenty rather than the speed cap so the mole does not reach the
            // sky's ceiling and make this a test about bouncing off it.
            _mole.AddImpulse(new Vec2(Fix64.Zero, -Fix64.FromInt(20)));
            Launched = true;
        }
    }

    [Test]
    public void AMoleFlungAtTheEndOfARoundComesDownBeforeTheNextOneStarts()
    {
        MoleMatch match = NewMatch();
        Mole flung = MoleOf(match, 0, 0);

        LateLauncher launcher = new LateLauncher(flung, MatchSettings.TicksPerRound - 2);
        match.ResolveRound(watching: launcher);

        Assert.Multiple(() =>
        {
            Assert.That(launcher.Launched, Is.True, "the watcher never reached its tick");
            Assert.That(flung.IsAirborne, Is.False, "left hanging in the air between rounds");
            Assert.That(
                TerrainQuery.IsBlocked(match.Terrain, flung.Position, MatchSettings.Radius),
                Is.False,
                "came to rest inside the ground");
        });
    }

    /// <summary>
    /// The settling is watched as well as simulated, so it has to fit in the recording. The arrays
    /// are cut to the round's own length, and every tick past it used to run off the end of them.
    /// </summary>
    [Test]
    public void ARecordedRoundCarriesTheSettlingAsWell()
    {
        MoleMatch match = NewMatch();
        Mole flung = MoleOf(match, 0, 0);

        LateLauncher launcher = new LateLauncher(flung, MatchSettings.TicksPerRound - 2);
        RoundResult result = match.ResolveRound(record: true, watching: launcher);
        RoundRecording recording = result.Recording!;

        Assert.Multiple(() =>
        {
            Assert.That(
                recording.Ticks, Is.GreaterThan(MatchSettings.TicksPerRound),
                "the settling was simulated but not recorded, so a replay stops mid-flight");
            Assert.That(
                recording.Ticks,
                Is.LessThanOrEqualTo(MatchSettings.TicksPerRound + MatchSettings.MaxSettleTicks));

            // The mole is at rest on the recording's own last tick, not just in the live match.
            Assert.That(
                recording.VelocityOf(recording.Ticks - 1, match.Moles.ToList().IndexOf(flung)),
                Is.EqualTo(Vec2.Zero));
        });
    }

    [Test]
    public void ARoundWithNobodyLeftInTheAirIsExactlyAsLongAsItAlwaysWas()
    {
        MoleMatch match = NewMatch();
        RoundResult result = match.ResolveRound(record: true);

        Assert.That(result.Recording!.Ticks, Is.EqualTo(MatchSettings.TicksPerRound));
    }

    [Test]
    public void SettlingStopsAtItsCapRatherThanRunningForever()
    {
        MoleMatch match = NewMatch();
        Mole flung = MoleOf(match, 0, 0);

        // Parked well above the ground with no way down inside the cap would hang the round if the
        // settle loop trusted the moles to land. It is bounded instead, so this returns.
        StuckLauncher launcher = new StuckLauncher(flung, MatchSettings.TicksPerRound - 2);
        match.ResolveRound(watching: launcher);

        Assert.That(launcher.Launched, Is.True);
    }

    /// <summary>Holds a mole airborne for longer than the settle cap allows.</summary>
    private sealed class StuckLauncher : MoleMatch.ITickWatcher
    {
        private readonly Mole _mole;
        private readonly int _from;

        public StuckLauncher(Mole mole, int from)
        {
            _mole = mole;
            _from = from;
        }

        public bool Launched { get; private set; }

        public void Ticked(int round, int tick, MoleMatch match)
        {
            if (tick < _from)
            {
                return;
            }

            // Re-flung every tick, so it never comes down and the cap is the only thing that ends
            // the round.
            _mole.AddImpulse(new Vec2(Fix64.Zero, -Fix64.FromInt(20)));
            Launched = true;
        }
    }

    [Test]
    public void AMatchStartsWithAFullPlatoonEach()
    {
        MoleMatch match = NewMatch(playerCount: 4);

        Assert.Multiple(() =>
        {
            Assert.That(match.Moles, Has.Count.EqualTo(16));
            Assert.That(match.Moles.Count(mole => mole.Seat == 0), Is.EqualTo(4));
            Assert.That(match.Moles.All(mole => mole.Pluck == 100), Is.True);
            Assert.That(match.Round, Is.Zero);
        });
    }

    [Test]
    public void MolesStartStandingOnTheGroundRatherThanInsideIt()
    {
        MoleMatch match = NewMatch(playerCount: 4);

        foreach (Mole mole in match.Moles)
        {
            Assert.That(
                TerrainQuery.IsBlocked(match.Terrain, mole.Position, MatchSettings.Radius),
                Is.False,
                $"seat {mole.Seat} mole {mole.Index} spawned inside the ground");
        }
    }

    [Test]
    public void PlatoonsDoNotAllSpawnOnTopOfEachOther()
    {
        MoleMatch match = NewMatch(playerCount: 4);

        for (int left = 0; left < match.Moles.Count; left++)
        {
            for (int right = left + 1; right < match.Moles.Count; right++)
            {
                Fix64 gap = Vec2.Distance(match.Moles[left].Position, match.Moles[right].Position);

                Assert.That(gap, Is.GreaterThan(MatchSettings.Radius * Fix64.FromInt(2)),
                    "two moles spawned overlapping");
            }
        }
    }

    [Test]
    public void OnlyTwoToFourPlayersAreAllowed()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MoleMatch.Create(FlatField(), 1, 1UL));
            Assert.Throws<ArgumentOutOfRangeException>(() => MoleMatch.Create(FlatField(), 5, 1UL));
        });
    }

    [Test]
    public void ARoundResolvesAndAdvancesTheClock()
    {
        MoleMatch match = NewMatch();
        match.SubmitPlan(Plan.Idle(0, 0));
        match.SubmitPlan(Plan.Idle(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(result.Round, Is.EqualTo(1));
            Assert.That(match.Round, Is.EqualTo(1));
            Assert.That(result.MatchOver, Is.False);
            Assert.That(result.TotalDamage, Is.Zero, "bracing hurts nobody");
        });
    }

    [Test]
    public void APlanForAMoleThatHasAlreadyActedIsRefused()
    {
        MoleMatch match = NewMatch();
        match.SubmitPlan(Plan.Idle(0, 0));
        match.SubmitPlan(Plan.Idle(1, 0));
        match.ResolveRound();

        Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(Plan.Idle(0, 0)));
        Assert.DoesNotThrow(() => match.SubmitPlan(Plan.Idle(0, 1)));
    }

    [Test]
    public void TheRotationResetsOnceEveryMoleHasHadATurn()
    {
        MoleMatch match = NewMatch();

        for (int index = 0; index < MatchSettings.MolesPerPlatoon; index++)
        {
            match.SubmitPlan(Plan.Idle(0, index));
            match.SubmitPlan(Plan.Idle(1, index));
            match.ResolveRound();
        }

        // Everybody has been, so mole zero comes round again.
        Assert.DoesNotThrow(() => match.SubmitPlan(Plan.Idle(0, 0)));
    }

    [Test]
    public void APlanNamingAMoleThatDoesNotExistIsRefused()
    {
        MoleMatch match = NewMatch();

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(Plan.Idle(0, 99)));
            Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(Plan.Idle(7, 0)));
        });
    }

    [Test]
    public void MoreThanOneShotInATurnIsRefused()
    {
        MoleMatch match = NewMatch();
        Plan greedy = new Plan(0, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(10, Vec2.UnitX, 200),
            PlanAction.Fire(80, Vec2.UnitX, 200),
        });

        InvalidPlanException error = Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(greedy))!;
        Assert.That(error.Message, Does.Contain("may be used 1 time(s) per turn"));
    }

    /// <summary>
    /// A turn may spend both its allowances: one attack and one movement ability.
    /// </summary>
    /// <remarks>
    /// The point of the whole arrangement. A mole choosing between getting somewhere and hurting
    /// somebody does the same dull sum every turn; one that may do both has to decide the order,
    /// which is where the interesting decisions were hiding.
    /// </remarks>
    [Test]
    public void AShotAndAMovementAbilityFitInOneTurn()
    {
        MoleMatch match = NewMatch();
        Plan both = new Plan(0, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(10, Vec2.UnitX, 200),
            PlanAction.Fire(20, Vec2.UnitX, 0, WeaponId.PowerClaws),
        });

        Assert.DoesNotThrow(() => match.SubmitPlan(both));
    }

    [Test]
    public void TwoMovementAbilitiesInOneTurnAreRefused()
    {
        MoleMatch match = NewMatch();
        Plan greedy = new Plan(0, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(10, Vec2.UnitX, 0, WeaponId.PowerClaws),
            PlanAction.Fire(40, Vec2.UnitX, 0, WeaponId.TunnelTorpedo),
        });

        InvalidPlanException error = Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(greedy))!;
        Assert.That(error.Message, Does.Contain("one Movement weapon"));
    }

    /// <summary>
    /// The two things you build with may be used more than once, and not more than their allowance.
    /// </summary>
    /// <remarks>
    /// A single sandbag is a bump in the ground; three are a step worth crossing the map for. The
    /// fourth is refused, so the allowance is a rule rather than a suggestion.
    /// </remarks>
    [TestCase(WeaponId.Sandbag, 3, true)]
    [TestCase(WeaponId.Sandbag, 4, false)]
    [TestCase(WeaponId.BoomBeets, 2, true)]
    [TestCase(WeaponId.BoomBeets, 3, false)]
    [TestCase(WeaponId.ClodLobber, 2, false)]
    public void ThingsYouBuildWithGetMoreThanOneUse(WeaponId weapon, int uses, bool allowed)
    {
        MoleMatch match = NewMatch();
        PlanAction[] actions = new PlanAction[uses];

        for (int use = 0; use < uses; use++)
        {
            actions[use] = PlanAction.Fire(10 + (use * 10), Vec2.UnitX, 0, weapon);
        }

        Plan plan = new Plan(0, 0, weapon, System.Array.Empty<RoutePoint>(), actions);

        if (allowed)
        {
            Assert.DoesNotThrow(() => match.SubmitPlan(plan));
        }
        else
        {
            Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(plan));
        }
    }

    [Test]
    public void AnActionScheduledPastTheEndOfTheRoundIsRefused()
    {
        MoleMatch match = NewMatch();
        Plan late = new Plan(0, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(MatchSettings.TicksPerRound, Vec2.UnitX, 200),
        });

        Assert.Throws<InvalidPlanException>(() => match.SubmitPlan(late));
    }

    [Test]
    public void AShotThatLandsOnSomebodyHurtsThemAndCratersTheGround()
    {
        MoleMatch match = NewMatch();
        Mole target = MoleOf(match, 1, 0);
        Mole shooter = MoleOf(match, 0, 0);

        // Straight at the target, close range, full power.
        Vec2 aim = (target.Position - shooter.Position).Normalised();
        ulong terrainBefore = match.Terrain.Hash;

        match.SubmitPlan(new Plan(0, 0, WeaponId.BeetleLauncher, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, aim, 255),
        }));
        match.SubmitPlan(Plan.Idle(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(result.Detonations, Is.GreaterThan(0), "the shot should have gone off");
            Assert.That(match.Terrain.Hash, Is.Not.EqualTo(terrainBefore), "and left a crater");
            Assert.That(result.TotalDamage, Is.GreaterThan(0));
        });
    }

    [Test]
    public void HittingSomebodyBeforeTheirFiringTickDeletesTheirShot()
    {
        // The deepest read in the game, and it falls out of two rules rather than a
        // system: damage tears up a recording, and a recording holds the shot.
        MoleMatch match = NewMatch();
        Mole shooter = MoleOf(match, 0, 0);
        Mole victim = MoleOf(match, 1, 0);
        Vec2 aim = (victim.Position - shooter.Position).Normalised();

        match.SubmitPlan(new Plan(0, 0, WeaponId.BeetleLauncher, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, aim, 255),
        }));

        // The victim was going to shoot back much later in the round.
        match.SubmitPlan(new Plan(1, 0, WeaponId.BeetleLauncher, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(200, -aim, 255),
        }));

        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(victim.InputCancelled, Is.True, "being hit ends the go");
            Assert.That(result.Detonations, Is.EqualTo(1),
                "only the first shot should ever have existed");
        });
    }

    [Test]
    public void AShotStillFiresIfItsOwnerWasNeverTouched()
    {
        // The control for the test above: without the hit, both shots happen.
        MoleMatch match = NewMatch();

        match.SubmitPlan(new Plan(0, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, new Vec2(Fix64.One, -Fix64.One), 120),
        }));
        match.SubmitPlan(new Plan(1, 0, WeaponId.ClodLobber, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(4, new Vec2(-Fix64.One, -Fix64.One), 120),
        }));

        RoundResult result = match.ResolveRound();

        Assert.That(result.Detonations, Is.EqualTo(2));
    }

    [Test]
    public void FriendlyFireIsOnAndThatIncludesYourself()
    {
        // No toggle, no owner check. A mole that plants dynamite at its own feet and does
        // not move takes the consequences, and so does the rest of its platoon nearby.
        MoleMatch match = NewMatch();
        Mole planter = MoleOf(match, 0, 0);

        match.SubmitPlan(new Plan(0, 0, WeaponId.BoomBeets, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, Vec2.UnitX, 0),
        }));
        match.SubmitPlan(Plan.Idle(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(planter.Pluck, Is.LessThan(100), "planting it under yourself hurts");
            Assert.That(result.Hits.Any(hit => hit.Seat == 0), Is.True);
        });
    }

    [Test]
    public void LavaStaysAwayUntilBoilingPoint()
    {
        MoleMatch match = NewMatch();

        for (int round = 0; round < MatchSettings.BoilingPointRound - 1; round++)
        {
            match.SubmitPlan(Plan.Idle(0, round % MatchSettings.MolesPerPlatoon));
            match.SubmitPlan(Plan.Idle(1, round % MatchSettings.MolesPerPlatoon));
            match.ResolveRound();
        }

        Assert.That(match.LavaLine, Is.EqualTo(Fix64.MaxValue), "no lava before round eight");
    }

    [Test]
    public void LavaArrivesAtRoundEightAndClimbs()
    {
        MoleMatch match = NewMatch();
        Fix64 previous = Fix64.MaxValue;

        for (int round = 0; round < 12; round++)
        {
            match.SubmitPlan(Plan.Idle(0, round % MatchSettings.MolesPerPlatoon));
            match.SubmitPlan(Plan.Idle(1, round % MatchSettings.MolesPerPlatoon));
            match.ResolveRound();

            if (match.Round < MatchSettings.BoilingPointRound)
            {
                continue;
            }

            Assert.That(match.LavaLine, Is.LessThan(previous),
                $"the lava should be higher at round {match.Round} than at the one before");
            previous = match.LavaLine;
        }
    }

    [Test]
    public void ThreeLavaTouchesEndsAMolesMatch()
    {
        MoleMatch match = NewMatch();
        Mole victim = MoleOf(match, 0, 0);

        // Two bounces are survivable, and each costs ten pluck.
        victim.LavaStrikes = 0;

        for (int strike = 1; strike <= 2; strike++)
        {
            victim.LavaStrikes = strike;
            victim.TakeDamage(MatchSettings.LavaBounceDamage);
        }

        Assert.Multiple(() =>
        {
            Assert.That(victim.Pluck, Is.EqualTo(80));
            Assert.That(victim.IsOffDuty, Is.False, "two touches are survivable");
        });
    }

    [Test]
    public void AMatchEndsWhenOneSeatIsLeftStanding()
    {
        MoleMatch match = NewMatch();

        foreach (Mole mole in match.Moles.Where(mole => mole.Seat == 1))
        {
            mole.TakeDamage(200);
        }

        match.SubmitPlan(Plan.Idle(0, 0));
        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(result.MatchOver, Is.True);
            Assert.That(result.WinningSeat, Is.EqualTo(0));
        });
    }

    [Test]
    public void ATotalWipeoutIsADrawRatherThanAWin()
    {
        // Four-way mutual knockouts stand, and they are glorious.
        MoleMatch match = NewMatch();

        foreach (Mole mole in match.Moles)
        {
            mole.TakeDamage(200);
        }

        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(result.MatchOver, Is.True);
            Assert.That(result.WinningSeat, Is.EqualTo(-1), "nobody won that");
        });
    }

    [Test]
    public void WindIsRolledEachRoundAndStaysInRange()
    {
        MoleMatch match = NewMatch(playerCount: 2);

        for (int round = 0; round < 8; round++)
        {
            match.SubmitPlan(Plan.Idle(0, round % MatchSettings.MolesPerPlatoon));
            match.SubmitPlan(Plan.Idle(1, round % MatchSettings.MolesPerPlatoon));
            match.ResolveRound();

            Assert.That(Fix64.Abs(match.Wind), Is.LessThanOrEqualTo(MatchSettings.MaxWindSpeed));
        }
    }

    [Test]
    public void TheSameSeedAndTheSamePlansGiveTheSameMatch()
    {
        // The property every other promise in the project depends on.
        ulong Play()
        {
            MoleMatch match = NewMatch(playerCount: 4, seed: 99UL);

            for (int round = 0; round < 6; round++)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    Mole actor = match.Moles.First(
                        mole => mole.Seat == seat && !mole.IsOffDuty && !mole.HasActedThisCycle);

                    match.SubmitPlan(new Plan(
                        seat,
                        actor.Index,
                        WeaponId.ClodLobber,
                        new[] { new RoutePoint(200 + (seat * 90) + (round * 12), SurfaceCell - 4) },
                        new[] { PlanAction.Fire(40 + (seat * 9), new Vec2(Fix64.One, -Fix64.One), 150) }));
                }

                match.ResolveRound();
            }

            return match.StateHash();
        }

        ulong first = Play();

        Assert.Multiple(() =>
        {
            Assert.That(Play(), Is.EqualTo(first), "second run diverged");
            Assert.That(Play(), Is.EqualTo(first), "third run diverged");
        });
    }

    [Test]
    public void SeatOrderDoesNotDecideAnything()
    {
        // "No initiative, ever" as a testable claim: submitting the same plans in the
        // opposite order must produce an identical match.
        Plan[] BuildPlans(MoleMatch match)
        {
            Plan[] plans = new Plan[4];

            for (int seat = 0; seat < 4; seat++)
            {
                Mole actor = match.Moles.First(mole => mole.Seat == seat && !mole.HasActedThisCycle);
                plans[seat] = new Plan(
                    seat,
                    actor.Index,
                    WeaponId.ClodLobber,
                    new[] { new RoutePoint(240 + (seat * 80), SurfaceCell - 4) },
                    new[] { PlanAction.Fire(30, new Vec2(Fix64.One, -Fix64.One), 180) });
            }

            return plans;
        }

        MoleMatch forwards = NewMatch(playerCount: 4, seed: 4242UL);
        Plan[] plans = BuildPlans(forwards);
        foreach (Plan plan in plans)
        {
            forwards.SubmitPlan(plan);
        }

        forwards.ResolveRound();

        MoleMatch backwards = NewMatch(playerCount: 4, seed: 4242UL);
        for (int seat = 3; seat >= 0; seat--)
        {
            backwards.SubmitPlan(plans[seat]);
        }

        backwards.ResolveRound();

        Assert.That(backwards.StateHash(), Is.EqualTo(forwards.StateHash()));
    }

    [Test]
    public void ASeatThatSubmitsNothingHoldsItsGround()
    {
        MoleMatch match = NewMatch();
        Mole idle = MoleOf(match, 1, 0);
        Vec2 before = idle.Position;

        match.SubmitPlan(Plan.Idle(0, 0));
        match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(idle.Position, Is.EqualTo(before), "it should not have wandered off");
            Assert.That(idle.IsOffDuty, Is.False);
        });
    }

    [Test]
    public void AMoleWalksItsRouteAcrossARound()
    {
        MoleMatch match = NewMatch();
        Mole walker = MoleOf(match, 0, 0);
        Fix64 startX = walker.Position.X;

        int targetCell = WorldScale.ToCell(startX) + 200;
        match.SubmitPlan(new Plan(
            0, 0, WeaponId.None,
            new[] { new RoutePoint(targetCell, SurfaceCell - 7) },
            System.Array.Empty<PlanAction>()));
        match.SubmitPlan(Plan.Idle(1, 0));

        match.ResolveRound();

        Assert.That(walker.Position.X, Is.GreaterThan(startX + Fix64.FromInt(8)),
            "it should have covered real ground");
    }

    [Test]
    public void StaminaIsRefilledEveryRound()
    {
        MoleMatch match = NewMatch();
        Mole walker = MoleOf(match, 0, 0);

        // Further than the eight-second clock allows, so the mole walks the whole round
        // rather than arriving early and stopping with stamina in hand.
        int targetCell = WorldScale.ToCell(walker.Position.X) + 700;
        match.SubmitPlan(new Plan(
            0, 0, WeaponId.None,
            new[] { new RoutePoint(targetCell, SurfaceCell - 7) },
            System.Array.Empty<PlanAction>()));
        match.SubmitPlan(Plan.Idle(1, 0));
        match.ResolveRound();

        Assert.That(walker.Stamina.ToDecimal(), Is.EqualTo(40m).Within(2m),
            "a full round of surface walking costs sixty of the hundred");

        match.SubmitPlan(Plan.Idle(0, 1));
        match.SubmitPlan(Plan.Idle(1, 1));
        match.ResolveRound();

        Assert.That(walker.Stamina, Is.EqualTo(Fix64.FromInt(MatchSettings.StartingStamina)),
            "and started the next round fresh");
    }

    /// <summary>
    /// A laid girder is written down, because the ground it lays cannot say it was one.
    /// </summary>
    /// <remarks>
    /// The note exists purely so the client has a beam to draw. Worth a test all the same: the
    /// deposit is ordinary loose soil and looks like every other piece of soil, so nothing else in
    /// the simulation would ever notice this going missing, and the only symptom would be girders
    /// that quietly stopped being drawn.
    /// </remarks>
    [Test]
    public void LayingAGirderIsRecordedForTheClientToDraw()
    {
        MoleMatch match = NewMatch();
        Mole builder = MoleOf(match, 0, 0);
        Vec2 where = builder.Position;

        match.SubmitPlan(new Plan(0, 0, WeaponId.None, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, Vec2.UnitX, 0, WeaponId.Girder),
        }));
        match.SubmitPlan(Plan.Idle(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.That(match.Girders, Has.Count.EqualTo(1));

        Girder laid = match.Girders[0];

        Assert.Multiple(() =>
        {
            Assert.That(laid.At, Is.EqualTo(where), "laid from where the mole was standing");
            Assert.That(laid.Along, Is.EqualTo(Vec2.UnitX));

            // Against the result's own round rather than a number, because this pair is what the
            // client compares to decide whether a beam belongs on screen yet, and a placement
            // records its round the same way.
            Assert.That(laid.LaidOnRound, Is.EqualTo(result.Round));
            Assert.That(laid.LaidOnTick, Is.EqualTo(2), "the tick it went down on, so a replay can wait for it");
        });
    }

    /// <summary>
    /// An aim of nothing lays no girder, so it leaves no note of one either.
    /// </summary>
    /// <remarks>
    /// LayGirder returns without touching the ground when the aim is zero, and a note written
    /// regardless would put a four metre beam on the screen with nothing underneath it. The two
    /// have to agree about whether anything happened.
    /// </remarks>
    [Test]
    public void AGirderAimedNowhereIsNotRecorded()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(new Plan(0, 0, WeaponId.None, System.Array.Empty<RoutePoint>(), new[]
        {
            PlanAction.Fire(2, Vec2.Zero, 0, WeaponId.Girder),
        }));
        match.SubmitPlan(Plan.Idle(1, 0));

        match.ResolveRound();

        Assert.That(match.Girders, Is.Empty);
    }
}
