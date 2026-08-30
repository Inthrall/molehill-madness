using System;
using MoleSim.Match;
using MoleSim.Numerics;
using NUnit.Framework;

namespace MoleSim.Tests;

/// <summary>
/// What the simulation does with bytes an opponent made up.
/// </summary>
/// <remarks>
/// The design's anti-cheat argument is that a plan is inputs rather than an outcome, so "you cannot
/// submit an illegal state, only illegal inputs, which every client's sim rejects identically". That
/// only holds if rejecting is what actually happens. An exception nobody catches is not a rejection,
/// it is a crash, and a crash triggered by a payload the relay is required to pass along untouched
/// is a crash any player can cause in everybody else's game.
///
/// So these are adversarial rather than exhaustive: each one is a payload a hostile client could
/// send, and the assertion is that it costs the sender its turn and nobody else anything.
/// </remarks>
[TestFixture]
public sealed class HostilePlanTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;

    /// <summary>
    /// A weapon byte naming no weapon. This used to reach an array index and throw
    /// IndexOutOfRangeException out of the simulation, on every honest client in the match.
    /// </summary>
    [TestCase((byte)17)]
    [TestCase((byte)99)]
    [TestCase((byte)255)]
    public void AWeaponByteThatNamesNothingIsRefusedRatherThanThrown(byte weapon)
    {
        byte[] wire = PlanCodec.Write(
            new Plan(0, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>()));

        wire[WeaponByte] = weapon;

        Assert.That(
            () => PlanCodec.Read(wire),
            Throws.TypeOf<PlanFormatException>(),
            "A weapon that does not exist has to be a format error, not an index error.");
    }

    /// <summary>Every real weapon still decodes, so the check did not overshoot.</summary>
    [Test]
    public void EveryWeaponThereActuallyIsStillDecodes()
    {
        for (WeaponId weapon = WeaponId.None; weapon <= WeaponTable.Last; weapon++)
        {
            byte[] wire = PlanCodec.Write(
                new Plan(0, 0, weapon, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>()));

            Assert.That(PlanCodec.Read(wire).Weapon, Is.EqualTo(weapon));
        }
    }

    /// <summary>
    /// And the lookup underneath stops trusting its caller, so a plan built in code rather than
    /// decoded cannot reach the same index either.
    /// </summary>
    [Test]
    public void StockOfAWeaponThatDoesNotExistIsNothingRatherThanACrash()
    {
        MoleMatch match = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);

        Assert.That(match.Stock(0, (WeaponId)200), Is.Zero);
        Assert.That(match.Stock(0, (WeaponId)255), Is.Zero);
    }

    /// <summary>
    /// The whole way through, as a client would meet it: the feeder drops the bad plan, the honest
    /// plans still go in, and nothing escapes to take the round down.
    /// </summary>
    [Test]
    public void AHostilePlanCostsItsSenderTheTurnAndNobodyElseAnything()
    {
        MoleMatch match = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);

        byte[] hostile = PlanCodec.Write(
            new Plan(1, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>()));

        hostile[WeaponByte] = 200;

        // The decode is where it stops. A client feeding a round has to be able to ask that question
        // per plan, so that one bad payload is one lost turn rather than everybody's match.
        Assert.That(() => PlanCodec.Read(hostile), Throws.TypeOf<PlanFormatException>());

        Plan honest = new Plan(
            0, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>());

        Assert.That(() => match.SubmitPlan(honest), Throws.Nothing);
        Assert.That(() => match.ResolveRound(), Throws.Nothing);
    }

    /// <summary>
    /// Where the weapon sits in the wire format: the version, the seat and the mole index come
    /// first. Written out rather than hunted for, so a format change breaks this loudly.
    /// </summary>
    private const int WeaponByte = 3;
}
