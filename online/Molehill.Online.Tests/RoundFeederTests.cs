using Molehill.Online;
using MoleSim.Match;
using MoleSim.Numerics;

namespace Molehill.Online.Tests;

/// <summary>
/// What happens when a plan arrives that the rules do not allow.
/// </summary>
/// <remarks>
/// The design's whole cheating model rests on this: because a plan is inputs rather than an outcome,
/// "you cannot submit an illegal state, only illegal inputs, which every client's sim rejects
/// identically". Nothing before this point has checked a plan, because the relay never looks at one,
/// so this is the only place that claim is either true or false.
///
/// Two properties have to hold together, and either alone is useless. An illegal plan must not take
/// the game down, and every client must drop exactly the same plans, or the cheat has caused a desync
/// instead of losing a turn.
/// </remarks>
[TestFixture]
public sealed class RoundFeederTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;
    private const ulong Seed = 987654321UL;

    private static MoleMatch World() =>
        MoleMatch.Create(playerCount: 2, Seed, MapWidthCells, MapHeightCells);

    [Test]
    public void AnHonestRoundIsFedWholeWithNothingRefused()
    {
        MoleMatch match = World();

        int refused = RoundFeeder.Feed(match, new[] { Shot(match, 0), Shot(match, 1) });

        Assert.That(refused, Is.EqualTo(0));
    }

    /// <summary>
    /// A plan naming a weapon the platoon does not hold, which is the cheapest cheat to try: the
    /// crate rarities start at zero and end a round on their own.
    /// </summary>
    [Test]
    public void APlanNamingAWeaponThePlatoonDoesNotHoldIsDropped()
    {
        MoleMatch match = World();

        int refused = RoundFeeder.Feed(
            match,
            new[] { Shot(match, 0), Shot(match, 1, WeaponId.MolyHandGrenade) },
            out List<int> seats);

        Assert.That(refused, Is.EqualTo(1));
        Assert.That(seats, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void AnIllegalPlanDoesNotThrow()
    {
        MoleMatch match = World();

        Assert.That(
            () => RoundFeeder.Feed(match, new[] { Shot(match, 0, WeaponId.GnomeMercy) }),
            Throws.Nothing,
            "A stranger's plan must not be able to take the game down.");
    }

    /// <summary>
    /// The property that actually matters. Two clients fed the same round, one plan in it illegal,
    /// have to end up in the same world: the cheat loses its turn and nobody desyncs.
    /// </summary>
    [Test]
    public void TwoClientsDropTheSamePlanAndStayInTheSameWorld()
    {
        MoleMatch mine = World();
        MoleMatch theirs = World();

        // Built once and read twice, exactly as the relay releases the same bytes to everybody.
        byte[][] wire =
        {
            PlanCodec.Write(Shot(mine, 0)),
            PlanCodec.Write(Shot(mine, 1, WeaponId.MolyHandGrenade)),
        };

        RoundFeeder.Feed(mine, wire.Select(PlanCodec.Read).ToArray());
        RoundFeeder.Feed(theirs, wire.Select(PlanCodec.Read).ToArray());

        mine.ResolveRound();
        theirs.ResolveRound();

        Assert.That(theirs.StateHash(), Is.EqualTo(mine.StateHash()));
    }

    /// <summary>
    /// And the cheat is actually punished rather than quietly tolerated: dropping seat one's plan
    /// has to produce a different world from allowing it, or the check is not doing anything.
    /// </summary>
    [Test]
    public void ADroppedPlanCostsThatPlatoonItsTurn()
    {
        MoleMatch cheated = World();
        MoleMatch honest = World();

        RoundFeeder.Feed(cheated, new[] { Shot(cheated, 0), Shot(cheated, 1, WeaponId.GnomeMercy) });
        RoundFeeder.Feed(honest, new[] { Shot(honest, 0), Shot(honest, 1) });

        cheated.ResolveRound();
        honest.ResolveRound();

        Assert.That(
            cheated.StateHash(),
            Is.Not.EqualTo(honest.StateHash()),
            "A refused plan made no difference, so nothing was actually refused.");
    }

    [Test]
    public void ARoundWithNoPlansAtAllIsSurvivable()
    {
        MoleMatch match = World();

        Assert.That(() => RoundFeeder.Feed(match, Array.Empty<Plan>()), Throws.Nothing);
        Assert.That(() => match.ResolveRound(), Throws.Nothing);
    }

    /// <summary>One shot from whichever mole is up, with a weapon the platoon holds by default.</summary>
    private static Plan Shot(MoleMatch match, int seat, WeaponId weapon = WeaponId.ClodLobber)
    {
        Mole actor = match.Eligible(seat).First();

        return new Plan(
            seat,
            actor.Index,
            weapon,
            Array.Empty<RoutePoint>(),
            new[]
            {
                PlanAction.Fire(
                    tick: 12 + seat,
                    aim: new Vec2(Fix64.FromInt(2 - seat), Fix64.FromInt(-3)),
                    power: 180),
            });
    }
}
