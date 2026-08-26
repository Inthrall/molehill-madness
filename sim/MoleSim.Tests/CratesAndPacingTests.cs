using System;
using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

[TestFixture]
public sealed class CratesAndPacingTests
{
    private const int WidthCells = 1200;
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

    private static MoleMatch NewMatch(int playerCount = 2, ulong seed = 20260826UL) =>
        MoleMatch.Create(FlatField(), playerCount, seed);

    private static Mole MoleOf(MoleMatch match, int seat, int index) =>
        match.Moles.Single(mole => mole.Seat == seat && mole.Index == index);

    private static RoundResult BraceRound(MoleMatch match, int moleIndex = 0)
    {
        for (int seat = 0; seat < match.PlayerCount; seat++)
        {
            if (match.Eligible(seat).Any(mole => mole.Index == moleIndex))
            {
                match.SubmitPlan(Plan.Brace(seat, moleIndex));
            }
        }

        return match.ResolveRound();
    }

    // ---- Crates ---------------------------------------------------------------------

    [Test]
    public void ADuelGetsOneCrateAndACrowdGetsTwo()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CrateSpawner.CountFor(2), Is.EqualTo(1));
            Assert.That(CrateSpawner.CountFor(3), Is.EqualTo(2));
            Assert.That(CrateSpawner.CountFor(4), Is.EqualTo(2),
                "more flashpoints, so the table cannot pile onto one");
        });
    }

    [Test]
    public void TheNextCratesAreAnnouncedInTheAftermath()
    {
        MoleMatch match = NewMatch(playerCount: 4);
        RoundResult result = BraceRound(match);

        Assert.Multiple(() =>
        {
            Assert.That(result.NextCrates, Has.Count.EqualTo(2));
            Assert.That(match.Crates, Has.Count.EqualTo(2));
            Assert.That(match.Crates.All(crate => !crate.HasLanded), Is.True,
                "telegraphed, not landed");
        });
    }

    [Test]
    public void ACrateLandsAboutEquallyFarFromEveryPlatoon()
    {
        // The whole point of the spawn: nobody gets a crate in their own trench.
        MoleMatch match = NewMatch(playerCount: 4);
        RoundResult result = BraceRound(match);

        Vec2 crate = result.NextCrates[0].Position;

        Fix64 nearest = Fix64.MaxValue;
        Fix64 furthest = Fix64.Zero;

        for (int seat = 0; seat < 4; seat++)
        {
            Fix64 closest = match.Moles
                .Where(mole => mole.Seat == seat && !mole.IsOffDuty)
                .Select(mole => Vec2.Distance(mole.Position, crate))
                .Aggregate(Fix64.MaxValue, Fix64.Min);

            nearest = Fix64.Min(nearest, closest);
            furthest = Fix64.Max(furthest, closest);
        }

        // Perfect fairness is impossible on real terrain; a large gap would mean the
        // solver was not trying.
        Assert.That((furthest - nearest).ToDecimal(), Is.LessThan(12m),
            $"nearest platoon {nearest}, furthest {furthest}");
    }

    [Test]
    public void CratesLandTowardTheMiddleRatherThanTheEdges()
    {
        MoleMatch match = NewMatch(playerCount: 4);
        RoundResult result = BraceRound(match);

        Fix64 mapWidth = WorldScale.ToMetres(WidthCells);

        foreach (CrateTelegraph telegraph in result.NextCrates)
        {
            Assert.That(telegraph.Position.X, Is.GreaterThan(mapWidth / Fix64.FromInt(5)));
            Assert.That(telegraph.Position.X, Is.LessThan(mapWidth - (mapWidth / Fix64.FromInt(5))));
        }
    }

    [Test]
    public void TwoCratesInARoundAreNotOnTopOfEachOther()
    {
        MoleMatch match = NewMatch(playerCount: 4);
        RoundResult result = BraceRound(match);

        Fix64 gap = Vec2.Distance(result.NextCrates[0].Position, result.NextCrates[1].Position);

        Assert.That(gap, Is.GreaterThan(Fix64.FromInt(15)));
    }

    [Test]
    public void ACrateEmbedsItselfSoTheLastStretchHasToBeDug()
    {
        MoleMatch match = NewMatch();
        RoundResult result = BraceRound(match);

        Vec2 crate = result.NextCrates[0].Position;
        int surfaceCell = 0;

        for (int cellY = 0; cellY < HeightCells; cellY++)
        {
            if (MaterialTable.IsSolid(match.Terrain[WorldScale.ToCell(crate.X), cellY]))
            {
                surfaceCell = cellY;
                break;
            }
        }

        Assert.That(crate.Y, Is.GreaterThan(WorldScale.ToMetres(surfaceCell)),
            "it should be below the surface, not sitting on it");
    }

    [Test]
    public void AMoleThatWalksOntoACrateGetsIt()
    {
        MoleMatch match = NewMatch();
        RoundResult telegraphed = BraceRound(match);
        Vec2 where = telegraphed.NextCrates[0].Position;

        // Put a mole right on the spot and let the round run.
        Mole taker = MoleOf(match, 0, 1);
        taker.Position = where;

        RoundResult result = BraceRound(match, moleIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.CrateClaims, Is.Not.Empty, "somebody should have claimed it");
            Assert.That(result.CrateClaims[0].Shattered, Is.False);
            Assert.That(result.CrateClaims[0].Seat, Is.EqualTo(0));
        });
    }

    [Test]
    public void TwoMolesArrivingTogetherSplitTheCrate()
    {
        MoleMatch match = NewMatch();
        RoundResult telegraphed = BraceRound(match);
        Vec2 where = telegraphed.NextCrates[0].Position;

        MoleOf(match, 0, 1).Position = where;
        MoleOf(match, 1, 1).Position = where;

        RoundResult result = BraceRound(match, moleIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.CrateClaims, Has.Count.EqualTo(2), "one share each");
            Assert.That(result.CrateClaims.All(claim => !claim.Shattered), Is.True);
        });
    }

    [Test]
    public void ThreeMolesArrivingTogetherTearItApartAndNobodyGetsAnything()
    {
        MoleMatch match = NewMatch(playerCount: 3);
        RoundResult telegraphed = BraceRound(match);
        Vec2 where = telegraphed.NextCrates[0].Position;

        for (int seat = 0; seat < 3; seat++)
        {
            MoleOf(match, seat, 1).Position = where;
        }

        RoundResult result = BraceRound(match, moleIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.CrateClaims, Has.Count.EqualTo(1));
            Assert.That(result.CrateClaims[0].Shattered, Is.True);
            Assert.That(result.CrateClaims[0].Seat, Is.EqualTo(-1), "nobody");
        });
    }

    [Test]
    public void AGrubPutsPluckBackButNeverAboveFull()
    {
        MoleMatch match = NewMatch();
        Mole taker = MoleOf(match, 0, 1);
        taker.Pluck = 90;

        // Force the contents rather than fishing for a grub from the generator.
        RoundResult telegraphed = BraceRound(match);
        taker.Position = telegraphed.NextCrates[0].Position;

        BraceRound(match, moleIndex: 1);

        Assert.That(taker.Pluck, Is.LessThanOrEqualTo(MatchSettings.StartingPluck),
            "a grub must never overfill a mole");
    }

    [Test]
    public void ACrateIsGoneOnceItHasBeenClaimed()
    {
        MoleMatch match = NewMatch();
        RoundResult telegraphed = BraceRound(match);
        MoleOf(match, 0, 1).Position = telegraphed.NextCrates[0].Position;

        BraceRound(match, moleIndex: 1);

        // A fresh telegraph replaces the list each round, so the claimed one is not
        // hanging about to be claimed twice.
        Assert.That(match.Crates.Count(crate => crate.Gone), Is.Zero);
    }

    [Test]
    public void CrateContentsAreTheSameForTheSameSeed()
    {
        CrateKind First(ulong seed)
        {
            MoleMatch match = NewMatch(seed: seed);
            BraceRound(match);
            return match.Crates[0].Contents.Kind;
        }

        Assert.That(First(555UL), Is.EqualTo(First(555UL)));
    }

    // ---- The stalemate nudge --------------------------------------------------------

    [Test]
    public void ThreeQuietRoundsCostEverybodySomeStamina()
    {
        MoleMatch match = NewMatch();

        Assert.That(match.StaminaScale, Is.EqualTo(Fix64.One), "nothing to answer for yet");

        RoundResult first = BraceRound(match, 0);
        RoundResult second = BraceRound(match, 1);
        RoundResult third = BraceRound(match, 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.StalemateNudged, Is.False);
            Assert.That(second.StalemateNudged, Is.False, "two quiet rounds are tolerated");
            Assert.That(third.StalemateNudged, Is.True, "the third is not");
            Assert.That(match.StaminaScale.ToDecimal(), Is.EqualTo(0.9m).Within(0.001m));
        });
    }

    [Test]
    public void TheNudgeActuallyShortensEverybodysLegs()
    {
        MoleMatch match = NewMatch();
        BraceRound(match, 0);
        BraceRound(match, 1);
        BraceRound(match, 2);

        // Fourth round: everybody starts with nine tenths of a tank.
        BraceRound(match, 3);

        Assert.That(
            MoleOf(match, 0, 3).Stamina.ToDecimal(),
            Is.LessThan(MatchSettings.StartingStamina),
            "a nudged round should not start full");
    }

    [Test]
    public void OneShotFiredAnywhereResetsThePatienceCounter()
    {
        MoleMatch match = NewMatch();
        BraceRound(match, 0);
        BraceRound(match, 1);

        // Somebody finally does something, which should clear the count.
        //
        // Point blank on purpose. Aiming flat at a mole nine metres away misses: the shell
        // drops half a metre on the way and lands short of its own blast radius, which is
        // correct artillery behaviour and not what this test is about.
        Mole shooter = MoleOf(match, 0, 2);
        Mole target = MoleOf(match, 1, 2);
        target.Position = shooter.Position + new Vec2(Fix64.FromInt(2), Fix64.Zero);
        Vec2 aim = Vec2.UnitX;

        match.SubmitPlan(new Plan(
            0, 2, WeaponId.BeetleLauncher, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(2, aim, 255) }));
        match.SubmitPlan(Plan.Brace(1, 2));
        RoundResult noisy = match.ResolveRound();

        Assert.That(noisy.TotalDamage, Is.GreaterThan(0), "precondition: something happened");

        RoundResult after = BraceRound(match, 3);

        Assert.Multiple(() =>
        {
            Assert.That(after.StalemateNudged, Is.False, "the count should have restarted");
            Assert.That(match.StaminaScale, Is.EqualTo(Fix64.One), "and nothing should be owed");
        });
    }

    // ---- The knockout reel ---------------------------------------------------------

    [Test]
    public void LavaAlwaysGivesTheSteamPop()
    {
        MatchRng rng = new MatchRng(1UL);

        Assert.That(
            KnockoutReel.Choose(KnockoutCause.Lava, 10, Fix64.Zero, false, rng),
            Is.EqualTo(KnockoutExit.SteamPop));
    }

    [Test]
    public void TheBigWhackAlwaysGivesTheHelmetSpin()
    {
        MatchRng rng = new MatchRng(1UL);

        Assert.That(
            KnockoutReel.Choose(KnockoutCause.Melee, 60, Fix64.FromInt(38), false, rng),
            Is.EqualTo(KnockoutExit.HelmetSpin));
    }

    [Test]
    public void ASeismicKnockoutUndergroundDropsThroughTheFloor()
    {
        MatchRng rng = new MatchRng(1UL);

        Assert.Multiple(() =>
        {
            Assert.That(
                KnockoutReel.Choose(KnockoutCause.Seismic, 15, Fix64.FromInt(10), true, rng),
                Is.EqualTo(KnockoutExit.UndergroundExpress),
                "the floor gave way");
            Assert.That(
                KnockoutReel.Choose(KnockoutCause.Seismic, 15, Fix64.FromInt(10), false, rng),
                Is.EqualTo(KnockoutExit.DizzyBirds),
                "on the surface it is stars and birds instead");
        });
    }

    [Test]
    public void AHardShoveGoesThroughSomethingRatherThanOverIt()
    {
        MatchRng rng = new MatchRng(1UL);

        Assert.Multiple(() =>
        {
            Assert.That(
                KnockoutReel.Choose(KnockoutCause.Explosion, 45, Fix64.FromInt(30), false, rng),
                Is.EqualTo(KnockoutExit.MoleShapedHole));
            Assert.That(
                KnockoutReel.Choose(KnockoutCause.Explosion, 45, Fix64.FromInt(15), false, rng),
                Is.EqualTo(KnockoutExit.BalloonExit),
                "a proper wallop without the shove inflates instead");
            Assert.That(
                KnockoutReel.Choose(KnockoutCause.Explosion, 5, Fix64.FromInt(2), false, rng),
                Is.EqualTo(KnockoutExit.SpinAndPoof),
                "and a nudge is a puff of dust");
        });
    }

    [Test]
    public void BeingWornDownGivesTheStretcherOrThePoofAndNothingElse()
    {
        MatchRng rng = new MatchRng(20260826UL);
        bool sawStretcher = false;
        bool sawPoof = false;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            KnockoutExit exit = KnockoutReel.Choose(
                KnockoutCause.Attrition, 4, Fix64.Zero, false, rng);

            sawStretcher |= exit == KnockoutExit.StretcherSquad;
            sawPoof |= exit == KnockoutExit.SpinAndPoof;

            Assert.That(
                exit,
                Is.EqualTo(KnockoutExit.StretcherSquad).Or.EqualTo(KnockoutExit.SpinAndPoof));
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawStretcher, Is.True, "the medics should turn up sometimes");
            Assert.That(sawPoof, Is.True, "and sometimes it is just dust");
        });
    }

    [Test]
    public void TheReelIsDecidedInTheSimSoAReplayShowsTheSamePratfall()
    {
        KnockoutExit Draw()
        {
            MatchRng rng = new MatchRng(99UL);
            return KnockoutReel.Choose(KnockoutCause.Attrition, 3, Fix64.Zero, false, rng);
        }

        Assert.That(Draw(), Is.EqualTo(Draw()));
    }

    [Test]
    public void AKnockoutInAMatchCarriesBothItsCauseAndItsExit()
    {
        MoleMatch match = NewMatch();
        Mole shooter = MoleOf(match, 0, 0);
        Mole victim = MoleOf(match, 1, 0);
        victim.Pluck = 5;
        victim.Position = shooter.Position + new Vec2(Fix64.FromInt(2), Fix64.Zero);

        Vec2 aim = Vec2.UnitX;
        match.SubmitPlan(new Plan(
            0, 0, WeaponId.BeetleLauncher, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(2, aim, 255) }));
        match.SubmitPlan(Plan.Brace(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.That(result.Knockouts, Is.Not.Empty);
        Assert.That(result.Knockouts[0].Cause, Is.EqualTo(KnockoutCause.Explosion));
        Assert.That(
            result.Knockouts[0].Exit,
            Is.EqualTo(KnockoutExit.MoleShapedHole).Or.EqualTo(KnockoutExit.BalloonExit)
                .Or.EqualTo(KnockoutExit.SpinAndPoof),
            "an explosion should pick one of the explosive exits");
    }
}
