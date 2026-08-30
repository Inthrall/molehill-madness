using MoleSim.Match;
using MoleSim.Numerics;

namespace MoleSim.Tests;

[TestFixture]
public sealed class PlanCodecTests
{
    private static Plan SamplePlan(int routePoints = 40)
    {
        RoutePoint[] route = new RoutePoint[routePoints];

        for (int index = 0; index < routePoints; index++)
        {
            route[index] = new RoutePoint(300 + (index * 7), 220 - (index % 11));
        }

        PlanAction[] actions =
        {
            PlanAction.Hop(18),
            PlanAction.Fire(96, new Vec2(Fix64.FromInt(3), -Fix64.FromInt(2)), 200),

            // A use that names its own weapon rather than the plan's, so the round trip is
            // exercised on the field that carries it.
            PlanAction.Fire(
                150, new Vec2(-Fix64.One, Fix64.Zero), 40, WeaponId.PowerClaws),
            PlanAction.Hop(200),
        };

        return new Plan(seat: 2, moleIndex: 3, WeaponId.BeetleLauncher, route, actions);
    }

    [Test]
    public void APlanSurvivesTheRoundTripExactly()
    {
        Plan original = SamplePlan();
        Plan restored = PlanCodec.Read(PlanCodec.Write(original));

        Assert.Multiple(() =>
        {
            Assert.That(restored.Seat, Is.EqualTo(original.Seat));
            Assert.That(restored.MoleIndex, Is.EqualTo(original.MoleIndex));
            Assert.That(restored.Weapon, Is.EqualTo(original.Weapon));
            Assert.That(restored.Route, Is.EqualTo(original.Route));
            Assert.That(restored.Actions, Is.EqualTo(original.Actions));
        });
    }

    [Test]
    public void EncodingIsStableSoTheSamePlanIsAlwaysTheSameBytes()
    {
        // Two clients building the same plan must produce identical payloads, or a relay
        // cannot treat them as interchangeable and replays cannot be compared.
        Assert.That(PlanCodec.Write(SamplePlan()), Is.EqualTo(PlanCodec.Write(SamplePlan())));
    }

    [Test]
    public void ARealisticPlanFitsWellInsideAKilobyte()
    {
        // The design's claim, which the whole relay design rests on: a round is a handful
        // of kilobytes, so store-and-forward is all the server ever has to do.
        byte[] wire = PlanCodec.Write(SamplePlan());

        Assert.That(wire.Length, Is.LessThan(1024),
            $"a forty-point plan came to {wire.Length} bytes");
    }

    [Test]
    public void EvenAnAbsurdlyDetailedPlanStaysUnderAKilobyte()
    {
        // Two hundred route points is far more than dragging a finger across a phone can
        // produce in forty-five seconds.
        byte[] wire = PlanCodec.Write(SamplePlan(routePoints: 200));

        Assert.That(wire.Length, Is.LessThan(1024), $"came to {wire.Length} bytes");
    }

    [Test]
    public void AFourPlayerRoundIsAFewKilobytes()
    {
        int total = 0;

        for (int seat = 0; seat < 4; seat++)
        {
            total += PlanCodec.Write(SamplePlan(routePoints: 60)).Length;
        }

        Assert.That(total, Is.LessThan(4096), $"four plans came to {total} bytes");
    }

    [Test]
    public void AnIdlePlanIsAlmostNothing()
    {
        byte[] wire = PlanCodec.Write(Plan.Idle(seat: 0, moleIndex: 1));

        Assert.That(wire.Length, Is.LessThan(16));
        Assert.That(PlanCodec.Read(wire).Route, Is.Empty);
    }

    [Test]
    public void AimSurvivesEncodingWellEnoughToShootStraight()
    {
        Vec2 aim = new Vec2(Fix64.FromInt(4), -Fix64.FromInt(3));
        PlanAction action = PlanAction.Fire(50, aim, 255);

        Vec2 decoded = PlanCodec.Read(PlanCodec.Write(
            new Plan(0, 0, WeaponId.ClodLobber, new RoutePoint[0], new[] { action })))
            .Actions[0].AimDirection();

        Vec2 expected = aim.Normalised();

        Assert.Multiple(() =>
        {
            Assert.That(Fix64.Abs(decoded.X - expected.X), Is.LessThan(Fix64.Ratio(1, 500)));
            Assert.That(Fix64.Abs(decoded.Y - expected.Y), Is.LessThan(Fix64.Ratio(1, 500)));
            Assert.That(Fix64.Abs(decoded.Length() - Fix64.One), Is.LessThan(Fix64.Ratio(1, 500)),
                "and should still be a unit direction");
        });
    }

    [Test]
    public void PowerScalesFromNothingToFull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlanAction.Fire(0, Vec2.UnitX, 0).PowerFraction(), Is.EqualTo(Fix64.Zero));
            Assert.That(PlanAction.Fire(0, Vec2.UnitX, 255).PowerFraction(), Is.EqualTo(Fix64.One));
            Assert.That(PlanAction.Fire(0, Vec2.UnitX, 128).PowerFraction().ToDecimal(),
                Is.EqualTo(0.5m).Within(0.01m));
        });
    }

    [Test]
    public void ANewerFormatVersionIsRefusedRatherThanMisread()
    {
        byte[] wire = PlanCodec.Write(SamplePlan());
        wire[0] = (byte)(Plan.FormatVersion + 1);

        PlanFormatException error = Assert.Throws<PlanFormatException>(() => PlanCodec.Read(wire))!;

        Assert.That(error.Message, Does.Contain("format version"));
    }

    [Test]
    public void ATruncatedPlanIsRefused()
    {
        byte[] wire = PlanCodec.Write(SamplePlan());
        byte[] truncated = new byte[wire.Length - 10];
        System.Array.Copy(wire, truncated, truncated.Length);

        Assert.Throws<PlanFormatException>(() => PlanCodec.Read(truncated));
    }

    [Test]
    public void AHeaderClaimingMoreThanIsPossibleIsRefused()
    {
        byte[] wire = PlanCodec.Write(SamplePlan());

        // Route count of 60000, which is beyond any sane plan.
        wire[4] = 0x60;
        wire[5] = 0xEA;

        Assert.Throws<PlanFormatException>(() => PlanCodec.Read(wire));
    }

    [Test]
    public void AnEmptyBufferIsRefused()
    {
        Assert.Throws<PlanFormatException>(() => PlanCodec.Read(System.Array.Empty<byte>()));
    }

    [Test]
    public void RoutePointsRoundTripThroughWorldSpace()
    {
        RoutePoint point = new RoutePoint(1234, 567);
        RoutePoint again = RoutePoint.FromWorld(point.ToWorld());

        Assert.That(again, Is.EqualTo(point));
    }
}
