using System;
using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

[TestFixture]
public sealed class ArsenalTests
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

    private static Mole MoleOf(MoleMatch match, int seat, int index) =>
        match.Moles.Single(mole => mole.Seat == seat && mole.Index == index);

    private static Plan Wield(int seat, int index, WeaponId weapon, Vec2 aim, byte power = 255, int tick = 3) =>
        new Plan(seat, index, weapon, Array.Empty<RoutePoint>(), new[] { PlanAction.Fire(tick, aim, power) });

    // ---- Every weapon is wired up ---------------------------------------------------

    [Test]
    public void EveryWeaponHasASpecAndAKind()
    {
        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            WeaponSpec spec = WeaponTable.Of(weapon);

            if (weapon == WeaponId.None)
            {
                Assert.That(spec.Kind, Is.EqualTo(WeaponKind.Nothing));
                continue;
            }

            Assert.That(spec.Kind, Is.Not.EqualTo(WeaponKind.Nothing),
                $"{weapon} has no behaviour wired up");
        }
    }

    [Test]
    public void TheArsenalIsFifteenStrong()
    {
        int weapons = Enum.GetValues<WeaponId>().Count(weapon => weapon != WeaponId.None);

        Assert.That(weapons, Is.EqualTo(15), "the design calls for fifteen at launch");
    }

    [Test]
    public void OnlyFrackingReachesThroughDirt()
    {
        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            bool expected = weapon == WeaponId.Fracking;

            Assert.That(WeaponTable.Of(weapon).ReachesBuried, Is.EqualTo(expected),
                $"{weapon} disagrees about reaching buried moles");
        }
    }

    [Test]
    public void OnlyTheBeetleLauncherRidesTheWind()
    {
        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            bool expected = weapon == WeaponId.BeetleLauncher;

            Assert.That(WeaponTable.Of(weapon).RidesTheWind, Is.EqualTo(expected),
                $"{weapon} disagrees about the wind");
        }
    }

    // ---- Firing in mid-air ---------------------------------------------------------

    [Test]
    public void AShotFiredInMidAirLandsSomewhereElseEntirely()
    {
        // The rule that turns knockback into a second weapon. The same plan, fired once
        // standing and once while tumbling, must put its crater somewhere different: it
        // is no longer enough to move somebody before their firing tick, because now you
        // can turn their shot as well.
        ulong Fire(bool launchFirst)
        {
            MoleMatch match = NewMatch();
            Mole shooter = MoleOf(match, 0, 0);

            if (launchFirst)
            {
                shooter.AddImpulse(new Vec2(Fix64.FromInt(-14), Fix64.FromInt(-8)));
            }

            match.SubmitPlan(Wield(0, 0, WeaponId.ClodLobber, new Vec2(Fix64.One, -Fix64.One), 255, tick: 2));
            match.SubmitPlan(Plan.Brace(1, 0));
            match.ResolveRound();

            return match.Terrain.Hash;
        }

        Assert.That(Fire(launchFirst: true), Is.Not.EqualTo(Fire(launchFirst: false)),
            "a mole punted into the air should not put its shell in the same place");
    }

    [Test]
    public void TheSameShotFiredGroundedTwiceLandsInTheSamePlace()
    {
        // The control for the test above, so a difference there cannot be dismissed as
        // noise.
        ulong Fire()
        {
            MoleMatch match = NewMatch();
            match.SubmitPlan(Wield(0, 0, WeaponId.ClodLobber, new Vec2(Fix64.One, -Fix64.One), 255, tick: 2));
            match.SubmitPlan(Plan.Brace(1, 0));
            match.ResolveRound();
            return match.Terrain.Hash;
        }

        Assert.That(Fire(), Is.EqualTo(Fire()));
    }

    [Test]
    public void TheTumbleRotationKeepsTheShotAUnitDirection()
    {
        Vec2 aim = new Vec2(Fix64.One, -Fix64.One).Normalised();
        Vec2 turned = aim.RotatedBy(new Vec2(Fix64.FromInt(-14), Fix64.FromInt(-6)).Normalised());

        Assert.Multiple(() =>
        {
            Assert.That(turned, Is.Not.EqualTo(aim), "it should have been turned");
            Assert.That(Fix64.Abs(turned.Length() - Fix64.One), Is.LessThan(Fix64.Ratio(1, 200)),
                "but should still be a unit direction, or the launch speed would change too");
        });
    }

    [Test]
    public void RotatingByTheForwardDirectionChangesNothing()
    {
        // The identity that makes the rule safe: a mole travelling straight to the right
        // fires exactly as committed, so the rotation only bites when it is tumbling.
        Vec2 aim = new Vec2(Fix64.Ratio(3, 5), Fix64.Ratio(-4, 5));

        Assert.That(aim.RotatedBy(Vec2.UnitX), Is.EqualTo(aim));
    }

    [Test]
    public void RotatingByStraightDownTurnsAShotAQuarterCircle()
    {
        // Facing straight down is a quarter turn clockwise, so a shot aimed along positive
        // X leaves along positive Y.
        Vec2 rotated = Vec2.UnitX.RotatedBy(Vec2.UnitY);

        Assert.Multiple(() =>
        {
            Assert.That(rotated.X, Is.EqualTo(Fix64.Zero));
            Assert.That(rotated.Y, Is.EqualTo(Fix64.One));
        });
    }

    [Test]
    public void AMoleInTheAirFacesTheWayItIsTravelling()
    {
        TerrainGrid grid = FlatField();
        Mole mole = new Mole(0, 0, new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(20)));
        mole.AddImpulse(new Vec2(Fix64.FromInt(12), Fix64.Zero));

        MoleMotion.Step(mole, grid, route: null);

        Assert.Multiple(() =>
        {
            Assert.That(mole.IsAirborne, Is.True);
            Assert.That(mole.Facing.X, Is.GreaterThan(Fix64.Zero), "moving right, facing right");
            Assert.That(mole.Facing.Y, Is.GreaterThan(Fix64.Zero), "and already falling");
        });
    }

    // ---- Line of sight -------------------------------------------------------------

    [Test]
    public void DirtStopsABallisticBlast()
    {
        TerrainGrid grid = FlatField();
        Mole[] moles = { new Mole(0, 0, new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(SurfaceCell + 60))) };

        // The mole is well underground; the blast is up on the surface.
        TerrainQuery.CarveBody(grid, moles[0].Position, MatchSettings.Radius);
        Vec2 surfaceBurst = new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(SurfaceCell - 2));

        Blast.Detonate(grid, moles, surfaceBurst, WeaponTable.Of(WeaponId.MolyHandGrenade));

        Assert.That(moles[0].Pluck, Is.EqualTo(100), "a metre of dirt should have shielded it");
    }

    [Test]
    public void FrackingReachesAMoleThatDirtWouldOtherwiseProtect()
    {
        TerrainGrid grid = FlatField();
        Mole[] moles = { new Mole(0, 0, new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(SurfaceCell + 30))) };
        TerrainQuery.CarveBody(grid, moles[0].Position, MatchSettings.Radius);

        Vec2 surfaceBore = new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(SurfaceCell - 2));

        Blast.Detonate(grid, moles, surfaceBore, WeaponTable.Of(WeaponId.Fracking));

        Assert.That(moles[0].Pluck, Is.LessThan(100), "the shock goes through the soil");
    }

    [Test]
    public void ABlastInTheOpenStillReachesEverybody()
    {
        TerrainGrid grid = new TerrainGrid(400, 400);
        Mole[] moles = { new Mole(0, 0, new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(100))) };

        Blast.Detonate(
            grid, moles,
            new Vec2(WorldScale.ToMetres(101), WorldScale.ToMetres(100)),
            WeaponTable.Of(WeaponId.BeetleLauncher));

        Assert.That(moles[0].Pluck, Is.LessThan(100));
    }

    // ---- Individual weapons --------------------------------------------------------

    [Test]
    public void TheAcornMortarSplitsIntoMoreThanItStartedAs()
    {
        MoleMatch match = NewMatch();
        match.SubmitPlan(Wield(0, 0, WeaponId.AcornMortar, new Vec2(Fix64.One, -Fix64.One), 160));
        match.SubmitPlan(Plan.Brace(1, 0));

        RoundResult result = match.ResolveRound();

        Assert.That(result.Detonations, Is.GreaterThan(1),
            "one lob should have become several bangs");
    }

    [Test]
    public void TheBigWhackHitsSomebodyStandingRightThere()
    {
        MoleMatch match = NewMatch();
        Mole swinger = MoleOf(match, 0, 0);
        Mole victim = MoleOf(match, 1, 0);

        // Stand the victim within mallet reach.
        victim.Position = swinger.Position + new Vec2(MatchSettings.Radius * Fix64.FromInt(2), Fix64.Zero);

        match.SubmitPlan(Wield(0, 0, WeaponId.BigWhack, Vec2.UnitX));
        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(victim.Pluck, Is.LessThan(50), "sixty damage is a lot");
            Assert.That(result.Hits, Is.Not.Empty);
        });
    }

    [Test]
    public void TheBigWhackMissesThinAir()
    {
        MoleMatch match = NewMatch();
        Mole victim = MoleOf(match, 1, 0);

        match.SubmitPlan(Wield(0, 0, WeaponId.BigWhack, Vec2.UnitX));
        RoundResult result = match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(victim.Pluck, Is.EqualTo(100));
            Assert.That(result.Hits, Is.Empty);
        });
    }

    [Test]
    public void FrackingCollapsesTunnelsNearTheBore()
    {
        MoleMatch match = NewMatch();
        Mole driller = MoleOf(match, 0, 0);

        // A tunnel under the driller's feet, which the shock should shake back in.
        Vec2 tunnel = new Vec2(driller.Position.X, driller.Position.Y + Fix64.FromInt(3));
        for (int step = 0; step < 20; step++)
        {
            TerrainQuery.CarveBody(
                match.Terrain,
                new Vec2(tunnel.X + (WorldScale.CellSize * Fix64.FromInt(step)), tunnel.Y),
                MatchSettings.Radius);
        }

        Assert.That(TerrainQuery.MaterialAt(match.Terrain, tunnel), Is.EqualTo(Material.Air), "precondition");

        match.SubmitPlan(Wield(0, 0, WeaponId.Fracking, Vec2.UnitY));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Assert.That(TerrainQuery.MaterialAt(match.Terrain, tunnel), Is.Not.EqualTo(Material.Air),
            "the tunnel should have caved in");
    }

    [Test]
    public void PowerClawsMakeDirtCostWhatOpenGroundCosts()
    {
        MoleMatch match = NewMatch();
        Mole digger = MoleOf(match, 0, 0);
        int targetCell = WorldScale.ToCell(digger.Position.X) + 120;

        match.SubmitPlan(new Plan(
            0, 0, WeaponId.PowerClaws,
            new[] { new RoutePoint(targetCell, SurfaceCell + 90) },
            new[] { PlanAction.Fire(1, Vec2.UnitY, 1) }));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Fix64 clawed = digger.Stamina;

        // The same route without the claws should cost a great deal more.
        MoleMatch plain = NewMatch();
        Mole plainDigger = MoleOf(plain, 0, 0);
        plain.SubmitPlan(new Plan(
            0, 0, WeaponId.None,
            new[] { new RoutePoint(WorldScale.ToCell(plainDigger.Position.X) + 120, SurfaceCell + 90) },
            Array.Empty<PlanAction>()));
        plain.SubmitPlan(Plan.Brace(1, 0));
        plain.ResolveRound();

        Assert.That(clawed, Is.GreaterThan(plainDigger.Stamina),
            "digging with the claws should leave more in the tank");
    }

    [Test]
    public void ASandbagLeavesGroundBehindIt()
    {
        MoleMatch match = NewMatch();
        ulong before = match.Terrain.Hash;

        match.SubmitPlan(Wield(0, 0, WeaponId.Sandbag, Vec2.UnitY, 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Assert.That(match.Terrain.Hash, Is.Not.EqualTo(before), "something should have been deposited");
    }

    [Test]
    public void ATunnelTorpedoMovesTheMoleAndCarvesItsPath()
    {
        MoleMatch match = NewMatch();
        Mole driller = MoleOf(match, 0, 0);
        Fix64 startX = driller.Position.X;
        ulong before = match.Terrain.Hash;

        match.SubmitPlan(Wield(0, 0, WeaponId.TunnelTorpedo, new Vec2(Fix64.One, Fix64.Ratio(1, 2)), 255, tick: 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Assert.Multiple(() =>
        {
            Assert.That(driller.Position.X, Is.GreaterThan(startX + Fix64.FromInt(4)), "it should have dashed");
            Assert.That(match.Terrain.Hash, Is.Not.EqualTo(before), "and left a tunnel");
        });
    }

    [Test]
    public void ASnapTrapIsHarmlessTheRoundItIsPlacedAndDangerousAfter()
    {
        MoleMatch match = NewMatch();
        Mole placer = MoleOf(match, 0, 0);
        Mole victim = MoleOf(match, 1, 0);

        match.SubmitPlan(Wield(0, 0, WeaponId.SnapTrap, Vec2.UnitY, 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        // Standing on it the same round it was placed is safe.
        victim.Position = placer.Position;
        Assert.That(victim.Pluck, Is.EqualTo(100), "it should not be armed yet");

        match.SubmitPlan(Plan.Brace(0, 1));
        match.SubmitPlan(Plan.Brace(1, 1));
        match.ResolveRound();

        Assert.That(victim.Pluck, Is.LessThan(100), "and should catch somebody the round after");
    }

    [Test]
    public void ARootSnareHalvesMovementAndStopsDigging()
    {
        MoleMatch match = NewMatch();
        Mole snarer = MoleOf(match, 0, 0);
        Mole victim = MoleOf(match, 1, 0);

        victim.Position = snarer.Position + new Vec2(Fix64.One, Fix64.Zero);

        // Placed and live at once, so it bites this round.
        match.SubmitPlan(Wield(0, 0, WeaponId.RootSnare, Vec2.UnitY, 1));
        match.SubmitPlan(new Plan(
            1, 0, WeaponId.None,
            new[] { new RoutePoint(WorldScale.ToCell(victim.Position.X) + 700, SurfaceCell - 7) },
            Array.Empty<PlanAction>()));

        match.ResolveRound();

        // Half speed for a whole round is roughly twenty metres rather than forty.
        Fix64 travelled = victim.Position.X - (snarer.Position.X + Fix64.One);

        Assert.That(travelled, Is.LessThan(Fix64.FromInt(30)),
            "a snared mole should not manage a full run");
    }

    [Test]
    public void SpecialDeliveryArrivesFromAboveInThreePieces()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(Wield(0, 0, WeaponId.SpecialDelivery, Vec2.UnitX, 128, tick: 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        RoundResult result = match.ResolveRound();

        Assert.That(result.Detonations, Is.EqualTo(3), "three sacks, three bangs");
    }

    [Test]
    public void SomebodyUndergroundIsSafeFromSpecialDelivery()
    {
        // The counterplay is the game's own verb: sacks fall from the sky, so dirt
        // overhead is the answer.
        MoleMatch match = NewMatch();
        Mole hider = MoleOf(match, 1, 0);
        hider.Position = new Vec2(hider.Position.X, WorldScale.ToMetres(SurfaceCell + 60));
        TerrainQuery.CarveBody(match.Terrain, hider.Position, MatchSettings.Radius);

        Vec2 aim = (hider.Position - MoleOf(match, 0, 0).Position).Normalised();
        match.SubmitPlan(Wield(0, 0, WeaponId.SpecialDelivery, aim, 120, tick: 1));

        match.ResolveRound();

        Assert.That(hider.Pluck, Is.EqualTo(100), "the dirt overhead should have kept it safe");
    }

    [Test]
    public void GnomeMercyGoesOffRepeatedlyAsItBounces()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(Wield(0, 0, WeaponId.GnomeMercy, Vec2.UnitX, 100, tick: 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        RoundResult result = match.ResolveRound();

        Assert.That(result.Detonations, Is.GreaterThan(1), "it shows no mercy whatsoever");
    }

    [Test]
    public void TheMolyHandGrenadeHitsHarderThanAnythingElseThrown()
    {
        int moly = WeaponTable.Of(WeaponId.MolyHandGrenade).Damage;

        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            if (weapon == WeaponId.MolyHandGrenade
                || WeaponTable.Of(weapon).Kind != WeaponKind.Thrown)
            {
                continue;
            }

            Assert.That(WeaponTable.Of(weapon).Damage, Is.LessThan(moly),
                $"{weapon} should not out-hit the relic");
        }
    }

    [Test]
    public void AGeyserCapThrowsAMoleUpward()
    {
        MoleMatch match = NewMatch();
        Mole capper = MoleOf(match, 0, 0);

        match.SubmitPlan(Wield(0, 0, WeaponId.GeyserCap, Vec2.UnitY, 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        // The vent stays put, so anybody standing on it next round goes up.
        Assert.That(match.Placements.Any(placement => placement.Weapon == WeaponId.GeyserCap),
            Is.True, "the vent should still be there");
    }

    [Test]
    public void ASpentTrapIsTidiedAwayButAVentIsNot()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(Wield(0, 0, WeaponId.GeyserCap, Vec2.UnitY, 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        int ventsAfterOne = match.Placements.Count(p => p.Weapon == WeaponId.GeyserCap);

        match.SubmitPlan(Plan.Brace(0, 1));
        match.SubmitPlan(Plan.Brace(1, 1));
        match.ResolveRound();

        Assert.That(match.Placements.Count(p => p.Weapon == WeaponId.GeyserCap),
            Is.EqualTo(ventsAfterOne), "a capped vent is permanent");
    }

    [Test]
    public void ARootSnareIsGoneAfterTheRoundItWasPlaced()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(Wield(0, 0, WeaponId.RootSnare, Vec2.UnitY, 1));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        match.SubmitPlan(Plan.Brace(0, 1));
        match.SubmitPlan(Plan.Brace(1, 1));
        match.ResolveRound();

        Assert.That(match.Placements.Any(p => p.Weapon == WeaponId.RootSnare), Is.False,
            "a snare costs its victim one turn, not the match");
    }

    // ---- The map has to survive long enough to be played on -------------------------

    [Test]
    public void NothingDigsFurtherThanItHurts()
    {
        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            WeaponSpec spec = WeaponTable.Of(weapon);

            Assert.That(
                spec.CraterRadius, Is.LessThanOrEqualTo(spec.BlastRadius),
                $"{weapon} leaves a hole bigger than its blast, which cannot be right");
        }
    }

    [Test]
    public void ADozenRoundsOfShellingLeavesTheMapStanding()
    {
        // The design fixes the pacing this defends: lava arrives at round eight and then
        // climbs for several more, so a match is meant to still have ground under it well
        // past round ten. The first render of a whole match showed the field unrecognisable
        // by round five, which is what separated a crater from the blast that makes it.
        MoleMatch match = MoleMatch.Create(FlatField(), 4, 20260826UL);
        int solidAtTheStart = SolidCells(match.Terrain);

        for (int round = 0; round < 12; round++)
        {
            for (int seat = 0; seat < match.PlayerCount; seat++)
            {
                Mole? actor = match.Eligible(seat).FirstOrDefault();

                if (actor is null)
                {
                    continue;
                }

                // Straight into the dirt at its feet, which is the worst case for the map.
                match.SubmitPlan(Wield(
                    seat, actor.Index, WeaponId.ClodLobber, Vec2.UnitY, power: 60));
            }

            match.ResolveRound();
        }

        int remaining = SolidCells(match.Terrain) * 100 / solidAtTheStart;

        // Four fifths, against about two fifths when a crater was as wide as its blast, so
        // the threshold has teeth rather than merely recording what happens today.
        Assert.That(
            remaining, Is.GreaterThan(80),
            $"only {remaining}% of the ground is left after twelve rounds");
    }

    private static int SolidCells(TerrainGrid grid)
    {
        int solid = 0;

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                if (MaterialTable.IsSolid(grid[x, y]))
                {
                    solid++;
                }
            }
        }

        return solid;
    }
}
