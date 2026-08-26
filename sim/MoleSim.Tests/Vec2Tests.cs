using MoleSim.Numerics;

namespace MoleSim.Tests;

[TestFixture]
public sealed class Vec2Tests
{
    private static readonly Fix64 Tolerance = Fix64.FromRaw(8);

    /// <summary>
    /// NUnit cannot apply a numeric tolerance to a type it does not know how to subtract,
    /// so closeness is asserted directly rather than through Within().
    /// </summary>
    private static void AssertClose(Fix64 actual, Fix64 expected, string message) =>
        AssertClose(actual, expected, Tolerance, message);

    private static void AssertClose(Fix64 actual, Fix64 expected, Fix64 tolerance, string message)
    {
        Assert.That(Fix64.Abs(actual - expected), Is.LessThanOrEqualTo(tolerance),
            $"{message}: expected {expected}, was {actual}");
    }

    [Test]
    public void ComponentsRoundTrip()
    {
        Vec2 value = new Vec2(Fix64.Ratio(3, 4), Fix64.FromInt(-2));

        Assert.Multiple(() =>
        {
            Assert.That(value.X.ToDecimal(), Is.EqualTo(0.75m));
            Assert.That(value.Y.ToDecimal(), Is.EqualTo(-2m));
        });
    }

    [Test]
    public void ArithmeticBehavesComponentwise()
    {
        Vec2 left = Vec2.FromInt(3, 4);
        Vec2 right = Vec2.FromInt(-1, 2);

        Assert.Multiple(() =>
        {
            Assert.That(left + right, Is.EqualTo(Vec2.FromInt(2, 6)));
            Assert.That(left - right, Is.EqualTo(Vec2.FromInt(4, 2)));
            Assert.That(-left, Is.EqualTo(Vec2.FromInt(-3, -4)));
            Assert.That(left * Fix64.FromInt(2), Is.EqualTo(Vec2.FromInt(6, 8)));
            Assert.That(Fix64.FromInt(2) * left, Is.EqualTo(Vec2.FromInt(6, 8)));
            Assert.That(left / Fix64.FromInt(2), Is.EqualTo(new Vec2(Fix64.Ratio(3, 2), Fix64.FromInt(2))));
        });
    }

    [Test]
    public void LengthMatchesKnownTriples()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Vec2.FromInt(3, 4).Length(), Is.EqualTo(Fix64.FromInt(5)));
            Assert.That(Vec2.FromInt(-3, -4).Length(), Is.EqualTo(Fix64.FromInt(5)));
            Assert.That(Vec2.FromInt(5, 12).Length(), Is.EqualTo(Fix64.FromInt(13)));
            Assert.That(Vec2.Zero.Length(), Is.EqualTo(Fix64.Zero));
        });
    }

    [Test]
    public void LengthSquaredIsExactWhereLengthIsRounded()
    {
        // The reason range checks should prefer it: no root, so no truncation at all.
        Vec2 value = Vec2.FromInt(3, 4);

        Assert.That(value.LengthSquared(), Is.EqualTo(Fix64.FromInt(25)));
    }

    [Test]
    public void DotAndCrossFollowTheirDefinitions()
    {
        Vec2 right = Vec2.UnitX;
        Vec2 down = Vec2.UnitY;

        Assert.Multiple(() =>
        {
            Assert.That(Vec2.Dot(right, down), Is.EqualTo(Fix64.Zero), "perpendicular");
            Assert.That(Vec2.Dot(right, right), Is.EqualTo(Fix64.One), "parallel");
            Assert.That(Vec2.Dot(right, -right), Is.EqualTo(Fix64.MinusOne), "opposed");
            Assert.That(Vec2.Cross(right, down), Is.EqualTo(Fix64.One));
            Assert.That(Vec2.Cross(down, right), Is.EqualTo(Fix64.MinusOne), "sign flips with order");
            Assert.That(Vec2.Cross(right, right), Is.EqualTo(Fix64.Zero), "parallel has no cross");
        });
    }

    [Test]
    public void NormalisedGivesUnitLength()
    {
        foreach (Vec2 value in new[]
        {
            Vec2.FromInt(3, 4), Vec2.FromInt(-7, 2), Vec2.FromInt(100, -250), Vec2.FromInt(1, 1),
        })
        {
            AssertClose(value.Normalised().Length(), Fix64.One,
                $"{value} did not normalise to unit length");
        }
    }

    [Test]
    public void NormalisingZeroGivesZeroRatherThanThrowing()
    {
        // A mole standing perfectly still has no direction. Every caller would otherwise
        // need the same guard, so the guard lives here.
        Assert.That(Vec2.Zero.Normalised(), Is.EqualTo(Vec2.Zero));
    }

    [Test]
    public void PerpendicularsAreAtRightAnglesAndOpposite()
    {
        Vec2 value = Vec2.FromInt(3, 4);

        Assert.Multiple(() =>
        {
            Assert.That(Vec2.Dot(value, value.PerpendicularLeft()), Is.EqualTo(Fix64.Zero));
            Assert.That(Vec2.Dot(value, value.PerpendicularRight()), Is.EqualTo(Fix64.Zero));
            Assert.That(value.PerpendicularLeft(), Is.EqualTo(-value.PerpendicularRight()));
            Assert.That(value.PerpendicularLeft().Length(), Is.EqualTo(value.Length()));
        });
    }

    [Test]
    public void WithMaxLengthCapsSpeedButKeepsDirection()
    {
        Vec2 fast = Vec2.FromInt(30, 40);
        Fix64 cap = Fix64.FromInt(10);
        Vec2 capped = fast.WithMaxLength(cap);

        Assert.Multiple(() =>
        {
            // Normalising truncates each component by up to a raw unit, and scaling back
            // up multiplies that error by the cap, so this lands about a fifth of a
            // millimetre per second short of exactly ten. That is the precision floor of
            // the operation in Q16, not a defect, and it is far below anything the game
            // can feel.
            AssertClose(capped.Length(), cap, Fix64.FromRaw(32), "speed was not capped");

            // Direction preserved: the capped vector is parallel to the original.
            Assert.That(Fix64.Abs(Vec2.Cross(fast.Normalised(), capped.Normalised())),
                Is.LessThan(Tolerance));
        });
    }

    [Test]
    public void WithMaxLengthLeavesSlowerVectorsAlone()
    {
        Vec2 slow = Vec2.FromInt(3, 4);

        Assert.That(slow.WithMaxLength(Fix64.FromInt(10)), Is.EqualTo(slow));
    }

    [Test]
    public void DistanceIsSymmetric()
    {
        Vec2 a = Vec2.FromInt(1, 2);
        Vec2 b = Vec2.FromInt(4, 6);

        Assert.Multiple(() =>
        {
            Assert.That(Vec2.Distance(a, b), Is.EqualTo(Fix64.FromInt(5)));
            Assert.That(Vec2.Distance(b, a), Is.EqualTo(Fix64.FromInt(5)));
            Assert.That(Vec2.DistanceSquared(a, b), Is.EqualTo(Fix64.FromInt(25)));
        });
    }

    [Test]
    public void LerpHitsBothEndsAndTheMiddle()
    {
        Vec2 from = Vec2.FromInt(0, 10);
        Vec2 to = Vec2.FromInt(20, 30);

        Assert.Multiple(() =>
        {
            Assert.That(Vec2.Lerp(from, to, Fix64.Zero), Is.EqualTo(from));
            Assert.That(Vec2.Lerp(from, to, Fix64.One), Is.EqualTo(to));
            Assert.That(Vec2.Lerp(from, to, Fix64.Half), Is.EqualTo(Vec2.FromInt(10, 20)));
        });
    }

    [Test]
    public void ToCellFloorsSoNegativePositionsDoNotCollapseOntoZero()
    {
        // Truncation would put both -0.4 m and +0.4 m in cell 0, which would let a mole
        // stand in two places at once at the origin.
        Fix64 cellSize = WorldScale.CellSize;

        new Vec2(Fix64.Ratio(1, 4), Fix64.Ratio(-1, 4)).ToCell(cellSize, out int x, out int y);

        Assert.Multiple(() =>
        {
            Assert.That(x, Is.EqualTo(4), "a quarter metre is four cells across");
            Assert.That(y, Is.EqualTo(-4), "and four cells up");
        });

        new Vec2(Fix64.Ratio(-1, 100), Fix64.Ratio(1, 100)).ToCell(cellSize, out int nearX, out int nearY);

        Assert.Multiple(() =>
        {
            Assert.That(nearX, Is.EqualTo(-1), "just left of the origin is cell -1, not 0");
            Assert.That(nearY, Is.EqualTo(0));
        });
    }

    [Test]
    public void EqualityAndHashingAgree()
    {
        Vec2 a = new Vec2(Fix64.Ratio(1, 3), Fix64.Ratio(2, 7));
        Vec2 b = new Vec2(Fix64.Ratio(1, 3), Fix64.Ratio(2, 7));

        Assert.Multiple(() =>
        {
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals((object)b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a.Equals("not a vector"), Is.False);
        });
    }

    [Test]
    public void GravityPointsDownTheYAxis()
    {
        // Documents the sign convention the whole simulation relies on: Y grows downward,
        // matching the terrain grid, so gravity is a positive Y impulse.
        Vec2 position = Vec2.FromInt(10, 5);
        Vec2 gravity = Vec2.UnitY * Fix64.Ratio(98, 10);
        Vec2 afterOneSecond = position + gravity;

        Assert.That(afterOneSecond.Y, Is.GreaterThan(position.Y), "falling should increase Y");
    }
}
