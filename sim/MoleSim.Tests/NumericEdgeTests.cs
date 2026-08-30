using MoleSim.Numerics;
using NUnit.Framework;

namespace MoleSim.Tests;

/// <summary>
/// The edges of the fixed-point type, where the answers used to be wrong rather than approximate.
/// </summary>
/// <remarks>
/// None of these could fork a match: every device computed the same wrong answer, which is the one
/// property that actually matters here. They are worth fixing anyway, because a distance of zero
/// between two things that are not in the same place is the sort of wrong answer that gets built on.
/// </remarks>
[TestFixture]
public sealed class NumericEdgeTests
{
    /// <summary>
    /// Subtracting past the top saturates at the top. It used to pick the end from the sign of the
    /// left operand, so zero minus a negative overflowed upward and came back as the most negative
    /// value there is: the wrong sign as well as the wrong magnitude.
    /// </summary>
    [Test]
    public void OverflowingUpwardSaturatesUpward()
    {
        Fix64 verySmall = Fix64.FromRaw(long.MinValue + 1);

        Assert.That((Fix64.Zero - verySmall).Raw, Is.EqualTo(Fix64.MaxValue.Raw));
        Assert.That((Fix64.MaxValue - verySmall).Raw, Is.EqualTo(Fix64.MaxValue.Raw));
    }

    [Test]
    public void OverflowingDownwardStillSaturatesDownward()
    {
        Assert.That(
            (Fix64.MinValue - Fix64.MaxValue).Raw, Is.EqualTo(Fix64.MinValue.Raw));
    }

    /// <summary>
    /// Ordinary subtraction is untouched, which is most of what this operator ever does.
    /// </summary>
    [TestCase(5, 3, 2)]
    [TestCase(3, 5, -2)]
    [TestCase(-5, -3, -2)]
    [TestCase(0, 0, 0)]
    public void SubtractionThatDoesNotOverflowIsUnchanged(int left, int right, int expected)
    {
        Assert.That(
            (Fix64.FromInt(left) - Fix64.FromInt(right)).Raw,
            Is.EqualTo(Fix64.FromInt(expected).Raw));
    }

    /// <summary>
    /// Two points a few millimetres apart are a few millimetres apart. Squaring a raw value under
    /// 256 truncates to nothing, so the direct route measured anything shorter than about four
    /// millimetres as no distance at all.
    /// </summary>
    [Test]
    public void AVeryShortDistanceIsNotZero()
    {
        Fix64 tiny = Fix64.FromRaw(255);

        Fix64 measured = Fix64.Hypot(tiny, tiny);

        // Root two times 255 is about 360 raw. Exactness is not the claim; being nonzero and about
        // right is.
        Assert.That(measured.Raw, Is.GreaterThan(300));
        Assert.That(measured.Raw, Is.LessThan(400));
    }

    /// <summary>
    /// And a short vector keeps its direction instead of collapsing. A velocity that normalises to
    /// zero has lost the one thing a normalised vector is for.
    /// </summary>
    [Test]
    public void AShortVectorStillHasADirection()
    {
        Vec2 small = new Vec2(Fix64.FromRaw(255), Fix64.FromRaw(255));

        Assert.That(small.Length().Raw, Is.Not.Zero);

        Vec2 direction = small.Normalised();

        Assert.That(direction.X.Raw, Is.Not.Zero);
        Assert.That(direction.Y.Raw, Is.Not.Zero);

        // Pointing the way it was pointing: equal components, both positive.
        Assert.That(direction.X.Raw, Is.EqualTo(direction.Y.Raw));
    }

    /// <summary>A genuinely zero vector still measures zero, which is the case the floor is for.</summary>
    [Test]
    public void ZeroIsStillZero()
    {
        Assert.That(Fix64.Hypot(Fix64.Zero, Fix64.Zero).Raw, Is.Zero);
        Assert.That(Vec2.Zero.Length().Raw, Is.Zero);
        Assert.That(Vec2.Zero.Normalised(), Is.EqualTo(Vec2.Zero));
    }

    /// <summary>
    /// Ordinary distances are unchanged, which is what stops this being a rewrite of the maths
    /// rather than a floor under it.
    /// </summary>
    [TestCase(3, 4, 5)]
    [TestCase(5, 12, 13)]
    [TestCase(8, 15, 17)]
    public void OrdinaryDistancesAreUntouched(int x, int y, int expected)
    {
        Assert.That(
            Fix64.Hypot(Fix64.FromInt(x), Fix64.FromInt(y)).Raw,
            Is.EqualTo(Fix64.FromInt(expected).Raw));
    }
}
