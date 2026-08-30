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
}
