using System;
using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

/// <summary>
/// What a platoon is holding, and what runs out.
/// </summary>
/// <remarks>
/// The arsenal was unlimited to begin with, which meant the wheel offered the Moly Hand
/// Grenade every single turn and nothing else was ever worth choosing. It also meant weapon
/// crates, which are most of what a crate can contain, were silently thrown away. These are the
/// rules that make a crate worth crossing the map for.
/// </remarks>
[TestFixture]
public sealed class HoldingsTests
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

    private static Plan Wield(int seat, int index, WeaponId weapon, int tick = 3) =>
        new Plan(
            seat, index, weapon, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(tick, Vec2.UnitX, 200) });

    // ---- What everybody starts with -------------------------------------------------

    [Test]
    public void ExactlyOneWeaponIsUnlimited()
    {
        WeaponId[] unlimited = Enum.GetValues<WeaponId>()
            .Where(weapon => weapon != WeaponId.None && WeaponTable.IsUnlimited(weapon))
            .ToArray();

        // More than one and the weakest of them is dead weight; none at all and a platoon
        // that has spent everything cannot act, which is worse than any balance problem.
        Assert.That(unlimited, Is.EquivalentTo(new[] { WeaponId.ClodLobber }));
    }

    [Test]
    public void TheCrateRaritiesCannotBeStartedWith()
    {
        MoleMatch match = NewMatch();

        Assert.Multiple(() =>
        {
            Assert.That(match.CanUse(0, WeaponId.MolyHandGrenade), Is.False);
            Assert.That(match.CanUse(0, WeaponId.GnomeMercy), Is.False);
        });
    }

    [Test]
    public void EveryLimitedWeaponCanBeReplacedByACrate()
    {
        // A weapon a platoon can run out of and never get back would be a one-match
        // curiosity, so anything finite has to be reachable from a crate somewhere. Boom Beets
        // arrive as their own kind of crate rather than through the weapon table.
        WeaponId[] reachable = CrateSpawner.Restockable
            .Concat(CrateSpawner.Rarities)
            .Append(WeaponId.BoomBeets)
            .ToArray();

        foreach (WeaponId weapon in Enum.GetValues<WeaponId>())
        {
            if (weapon == WeaponId.None || WeaponTable.IsUnlimited(weapon))
            {
                continue;
            }

            Assert.That(reachable, Does.Contain(weapon), $"{weapon} can never be replaced");
        }
    }

    // ---- Spending -------------------------------------------------------------------

    [Test]
    public void FiringSomethingLimitedUsesOneUp()
    {
        MoleMatch match = NewMatch();
        int before = match.Stock(0, WeaponId.BeetleLauncher);

        match.SubmitPlan(Wield(0, 0, WeaponId.BeetleLauncher));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Assert.That(match.Stock(0, WeaponId.BeetleLauncher), Is.EqualTo(before - 1));
    }

    [Test]
    public void TheClodLobberNeverRunsOut()
    {
        MoleMatch match = NewMatch();

        for (int round = 0; round < MatchSettings.MolesPerPlatoon; round++)
        {
            match.SubmitPlan(Wield(0, round, WeaponId.ClodLobber));
            match.SubmitPlan(Plan.Brace(1, round));
            match.ResolveRound();
        }

        Assert.That(match.Stock(0, WeaponId.ClodLobber), Is.EqualTo(WeaponTable.Unlimited));
    }

    [Test]
    public void APlanNamingSomethingTheresNoneOfIsRefused()
    {
        MoleMatch match = NewMatch();

        Assert.Throws<InvalidPlanException>(
            () => match.SubmitPlan(Wield(0, 0, WeaponId.MolyHandGrenade)));
    }

    [Test]
    public void RunningOutTakesUntilItIsActuallySpent()
    {
        MoleMatch match = NewMatch();
        int stock = match.Stock(0, WeaponId.BeetleLauncher);

        for (int round = 0; round < stock; round++)
        {
            match.SubmitPlan(Wield(0, round, WeaponId.BeetleLauncher));
            match.SubmitPlan(Plan.Brace(1, round));
            match.ResolveRound();
        }

        Assert.Multiple(() =>
        {
            Assert.That(match.Stock(0, WeaponId.BeetleLauncher), Is.Zero);
            Assert.That(match.CanUse(0, WeaponId.BeetleLauncher), Is.False);
        });
    }

    [Test]
    public void PlantingACharaeUsesUpABoomBeet()
    {
        MoleMatch match = NewMatch();
        int before = match.Stock(0, WeaponId.BoomBeets);

        match.SubmitPlan(new Plan(
            0, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Dynamite(4) }));
        match.SubmitPlan(Plan.Brace(1, 0));
        match.ResolveRound();

        Assert.That(match.Stock(0, WeaponId.BoomBeets), Is.EqualTo(before - 1));
    }

    [Test]
    public void ACancelledShotCostsNoAmmunition()
    {
        // Damage ends a mole's input and deletes its unfired shot. Charging it for ammunition
        // it never got to throw would punish the same hit twice, which is why the stock comes
        // off at the moment of use rather than at the moment of committing.
        MoleMatch match = NewMatch();
        Mole shooter = match.Moles.Single(mole => mole.Seat == 0 && mole.Index == 0);
        Mole attacker = match.Moles.Single(mole => mole.Seat == 1 && mole.Index == 0);

        attacker.Position = shooter.Position + new Vec2(Fix64.Ratio(1, 2), Fix64.Zero);
        int before = match.Stock(0, WeaponId.BeetleLauncher);

        // The attacker fires first, point blank, well before the victim's own firing tick.
        match.SubmitPlan(Wield(0, 0, WeaponId.BeetleLauncher, tick: 120));
        match.SubmitPlan(new Plan(
            1, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(2, new Vec2(-Fix64.One, Fix64.Zero), 255) }));

        RoundResult result = match.ResolveRound();

        Assume.That(
            result.Hits.Any(hit => hit.Seat == 0 && hit.MoleIndex == 0), Is.True,
            "the victim has to actually be hit for this to mean anything");

        Assert.That(match.Stock(0, WeaponId.BeetleLauncher), Is.EqualTo(before));
    }

    // ---- Crates ---------------------------------------------------------------------

    [Test]
    public void AWeaponCrateGoesIntoThePlatoonsHoldings()
    {
        MoleMatch match = NewMatch();
        int before = match.Stock(0, WeaponId.MolyHandGrenade);

        match.Restock(0, WeaponId.MolyHandGrenade, 1);

        Assert.Multiple(() =>
        {
            Assert.That(match.Stock(0, WeaponId.MolyHandGrenade), Is.EqualTo(before + 1));
            Assert.That(match.CanUse(0, WeaponId.MolyHandGrenade), Is.True);
        });
    }

    [Test]
    public void RestockingSomethingUnlimitedChangesNothing()
    {
        MoleMatch match = NewMatch();

        match.Restock(0, WeaponId.ClodLobber, 5);

        Assert.That(match.Stock(0, WeaponId.ClodLobber), Is.EqualTo(WeaponTable.Unlimited));
    }

    [Test]
    public void HoldingsBelongToOnePlatoonOnly()
    {
        MoleMatch match = NewMatch();

        match.Restock(0, WeaponId.GnomeMercy, 1);

        Assert.Multiple(() =>
        {
            Assert.That(match.CanUse(0, WeaponId.GnomeMercy), Is.True);
            Assert.That(match.CanUse(1, WeaponId.GnomeMercy), Is.False);
        });
    }

    [Test]
    public void HoldingsAreInTheStateHash()
    {
        // Two machines that disagree about what a platoon is holding would disagree about
        // which plans are legal, which is a divergence like any other.
        MoleMatch match = NewMatch();
        ulong before = match.StateHash();

        match.Restock(0, WeaponId.GnomeMercy, 1);

        Assert.That(match.StateHash(), Is.Not.EqualTo(before));
    }
}
