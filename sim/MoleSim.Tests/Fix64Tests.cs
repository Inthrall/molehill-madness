using MoleSim.Numerics;

namespace MoleSim.Tests;

/// <summary>
/// Fix64 is the foundation of every number in the game, so these tests check it against
/// an independent reference rather than against itself. The reference is <c>decimal</c>,
/// which is exact for the values used here and is not part of the simulation.
/// </summary>
[TestFixture]
public sealed class Fix64Tests
{
    private const decimal Tolerance = 1m / 65536m;

    private static readonly decimal[] SampleValues =
    {
        0m, 1m, -1m, 0.5m, -0.5m, 2m, -2m, 3.25m, -3.25m,
        7m, 12m, 1.5m, -1.5m, 100m, -100m, 1234.5m, -1234.5m,
        0.015625m, 65535.5m, -65535.5m,
    };

    private static Fix64 From(decimal value) => Fix64.FromRaw((long)(value * 65536m));

    [Test]
    public void OneIsExactlyOne()
    {
        Assert.That(Fix64.One.Raw, Is.EqualTo(65536L));
        Assert.That(Fix64.One.ToDecimal(), Is.EqualTo(1m));
    }

    [Test]
    public void RatioBuildsExactFractions()
    {
        Assert.That(Fix64.Ratio(3, 2).ToDecimal(), Is.EqualTo(1.5m));
        Assert.That(Fix64.Ratio(1, 4).ToDecimal(), Is.EqualTo(0.25m));
        Assert.That(Fix64.Ratio(-1, 2).ToDecimal(), Is.EqualTo(-0.5m));
    }

    [Test]
    public void RatioByZeroThrows()
    {
        Assert.Throws<DivideByZeroException>(() => Fix64.Ratio(1, 0));
    }

    [Test]
    public void AdditionMatchesDecimalReference()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                Fix64 actual = From(left) + From(right);
                Assert.That(actual.ToDecimal(), Is.EqualTo(left + right),
                    $"{left} + {right}");
            }
        }
    }

    [Test]
    public void SubtractionMatchesDecimalReference()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                Fix64 actual = From(left) - From(right);
                Assert.That(actual.ToDecimal(), Is.EqualTo(left - right),
                    $"{left} - {right}");
            }
        }
    }

    [Test]
    public void MultiplicationMatchesDecimalReferenceWithinOneRawUnit()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                Fix64 actual = From(left) * From(right);
                decimal expected = left * right;

                // Truncation toward zero costs at most one raw unit.
                Assert.That(Math.Abs(actual.ToDecimal() - expected), Is.LessThanOrEqualTo(Tolerance),
                    $"{left} * {right} gave {actual.ToDecimal()}, expected {expected}");
            }
        }
    }

    [Test]
    public void DivisionMatchesDecimalReferenceWithinOneRawUnit()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                if (right == 0m)
                {
                    continue;
                }

                Fix64 actual = From(left) / From(right);
                decimal expected = left / right;

                // Only compare where the true quotient is representable.
                if (Math.Abs(expected) > 100_000m)
                {
                    continue;
                }

                Assert.That(Math.Abs(actual.ToDecimal() - expected), Is.LessThanOrEqualTo(Tolerance),
                    $"{left} / {right} gave {actual.ToDecimal()}, expected {expected}");
            }
        }
    }

    [Test]
    public void DivisionByZeroThrows()
    {
        Assert.Throws<DivideByZeroException>(() => _ = Fix64.One / Fix64.Zero);
    }

    [Test]
    public void MultiplicationIsCorrectWhenTheRawIntermediateExceedsSixtyFourBits()
    {
        // 3,000,000 squared is 9e12, comfortably representable. Getting there is not:
        // the raw operands multiply to about 3.9e22, which overflows a 64-bit product
        // nearly 4,200 times over. A naive (a * b) >> 16 would wrap and hand back
        // rubbish, often with the wrong sign. This is the 128-bit path earning its keep.
        Fix64 large = Fix64.FromInt(3_000_000);

        Assert.That((large * large).ToDecimal(), Is.EqualTo(9_000_000_000_000m));
        Assert.That((large * -large).ToDecimal(), Is.EqualTo(-9_000_000_000_000m));
    }

    [Test]
    public void MultiplicationSaturatesRatherThanWrappingWhenTheResultCannotFit()
    {
        // The representable ceiling is about 1.4e14, so this product genuinely has nowhere
        // to go. It must pin at the extreme rather than wrap into a small or negative value.
        Fix64 enormous = Fix64.FromInt(20_000_000);

        Assert.Multiple(() =>
        {
            Assert.That(enormous * enormous, Is.EqualTo(Fix64.MaxValue));
            Assert.That(enormous * -enormous, Is.EqualTo(Fix64.MinValue));
            Assert.That(-enormous * -enormous, Is.EqualTo(Fix64.MaxValue));
        });
    }

    [Test]
    public void AdditionSaturatesInsteadOfWrapping()
    {
        Assert.That(Fix64.MaxValue + Fix64.One, Is.EqualTo(Fix64.MaxValue));
        Assert.That(Fix64.MinValue - Fix64.One, Is.EqualTo(Fix64.MinValue));
    }

    [Test]
    public void NegationIsTotal()
    {
        // MinValue is deliberately -MaxValue so that negating any value stays in range.
        Assert.That(-Fix64.MinValue, Is.EqualTo(Fix64.MaxValue));
        Assert.That(-Fix64.MaxValue, Is.EqualTo(Fix64.MinValue));
    }

    [Test]
    public void MultiplicationIsCommutative()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                Assert.That(From(left) * From(right), Is.EqualTo(From(right) * From(left)),
                    $"{left} * {right}");
            }
        }
    }

    [Test]
    public void ComparisonsAgreeWithDecimalReference()
    {
        foreach (decimal left in SampleValues)
        {
            foreach (decimal right in SampleValues)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(From(left) < From(right), Is.EqualTo(left < right));
                    Assert.That(From(left) > From(right), Is.EqualTo(left > right));
                    Assert.That(From(left) <= From(right), Is.EqualTo(left <= right));
                    Assert.That(From(left) >= From(right), Is.EqualTo(left >= right));
                    Assert.That(From(left) == From(right), Is.EqualTo(left == right));
                });
            }
        }
    }

    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(4, 2)]
    [TestCase(9, 3)]
    [TestCase(144, 12)]
    [TestCase(1_000_000, 1000)]
    public void SqrtOfPerfectSquaresIsExact(int input, int expected)
    {
        Assert.That(Fix64.Sqrt(Fix64.FromInt(input)), Is.EqualTo(Fix64.FromInt(expected)));
    }

    [Test]
    public void SqrtIsTheExactTruncatedRootOfItsRawValue()
    {
        // The defining property, stated where it is actually exact: on the raw integers.
        //
        // Sqrt promises root = floor(sqrt(rawValue * 2^16)), so root squared never exceeds
        // the scaled input and the next step up always does. Checking that with Fix64
        // multiplication instead would prove nothing, because the multiply truncates too
        // and the two truncations mask each other: sqrt(4.5) squared back up lands exactly
        // on 4.5 either side of the true root.
        //
        // Int128 gives an exact reference here. The simulation may not use it; a test may.
        for (int whole = 0; whole < 500; whole++)
        {
            Fix64 value = Fix64.FromInt(whole) + Fix64.Ratio(whole % 7, 8);
            Int128 root = Fix64.Sqrt(value).Raw;
            Int128 scaled = (Int128)value.Raw << Fix64.FractionalBits;

            Assert.That(root * root, Is.LessThanOrEqualTo(scaled), $"sqrt({value}) is too large");
            Assert.That((root + 1) * (root + 1), Is.GreaterThan(scaled), $"sqrt({value}) is too small");
        }
    }

    [Test]
    public void SqrtAgreesWithADecimalReference()
    {
        foreach (decimal value in new[] { 0.25m, 1m, 2m, 4.5m, 10m, 99m, 1000m, 12345.75m })
        {
            Fix64 root = Fix64.Sqrt(From(value));
            decimal expected = (decimal)Math.Sqrt((double)value);

            Assert.That(root.ToDecimal(), Is.EqualTo(expected).Within(Tolerance * 2),
                $"sqrt({value})");
        }
    }

    [Test]
    public void SqrtOfSmallValuesIsStillTheBestAvailableAnswer()
    {
        // Documents the bottom of the useful range rather than pretending it is not there.
        Assert.Multiple(() =>
        {
            Assert.That(Fix64.Sqrt(Fix64.Zero), Is.EqualTo(Fix64.Zero));
            Assert.That(Fix64.Sqrt(Fix64.Ratio(1, 4)).ToDecimal(), Is.EqualTo(0.5m));
            Assert.That(Fix64.Sqrt(Fix64.Ratio(1, 16)).ToDecimal(), Is.EqualTo(0.25m));

            // Root of one raw step is 1/256, which is exactly representable.
            Assert.That(Fix64.Sqrt(Fix64.Epsilon).Raw, Is.EqualTo(256L));
        });
    }

    [Test]
    public void SqrtOfNegativeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fix64.Sqrt(Fix64.MinusOne));
    }

    [Test]
    public void HypotMatchesKnownTriples()
    {
        Assert.That(Fix64.Hypot(Fix64.FromInt(3), Fix64.FromInt(4)).ToDecimal(),
            Is.EqualTo(5m).Within(Tolerance * 4));
        Assert.That(Fix64.Hypot(Fix64.FromInt(-3), Fix64.FromInt(4)).ToDecimal(),
            Is.EqualTo(5m).Within(Tolerance * 4));
        Assert.That(Fix64.Hypot(Fix64.FromInt(5), Fix64.FromInt(12)).ToDecimal(),
            Is.EqualTo(13m).Within(Tolerance * 4));
    }

    [Test]
    public void HypotHandlesZeroComponents()
    {
        Assert.That(Fix64.Hypot(Fix64.Zero, Fix64.FromInt(7)), Is.EqualTo(Fix64.FromInt(7)));
        Assert.That(Fix64.Hypot(Fix64.FromInt(-7), Fix64.Zero), Is.EqualTo(Fix64.FromInt(7)));
        Assert.That(Fix64.Hypot(Fix64.Zero, Fix64.Zero), Is.EqualTo(Fix64.Zero));
    }

    [Test]
    public void HypotSurvivesComponentsWhoseSquaresWouldOverflow()
    {
        // Squaring 5,000,000 exceeds the range, so a naive sqrt(x*x + y*y) would saturate
        // and return nonsense. Scaling by the larger component avoids that.
        Fix64 result = Fix64.Hypot(Fix64.FromInt(3_000_000), Fix64.FromInt(4_000_000));

        Assert.That(result.ToDecimal(), Is.EqualTo(5_000_000m).Within(2m));
    }

    [TestCase(2.5, 2)]
    [TestCase(-2.5, -3)]
    [TestCase(3.0, 3)]
    [TestCase(-3.0, -3)]
    public void FloorRoundsTowardNegativeInfinity(double input, int expected)
    {
        Fix64 value = From((decimal)input);
        Assert.That(Fix64.Floor(value), Is.EqualTo(Fix64.FromInt(expected)));
    }

    [TestCase(2.5, 3)]
    [TestCase(-2.5, -2)]
    [TestCase(3.0, 3)]
    public void CeilingRoundsTowardPositiveInfinity(double input, int expected)
    {
        Fix64 value = From((decimal)input);
        Assert.That(Fix64.Ceiling(value), Is.EqualTo(Fix64.FromInt(expected)));
    }

    [TestCase(2.5, 3)]
    [TestCase(2.4, 2)]
    [TestCase(-2.5, -3)]
    [TestCase(-2.4, -2)]
    public void RoundTakesHalvesAwayFromZero(double input, int expected)
    {
        Fix64 value = From((decimal)input);
        Assert.That(Fix64.Round(value), Is.EqualTo(Fix64.FromInt(expected)));
    }

    [TestCase(2.9, 2)]
    [TestCase(-2.9, -3)]
    [TestCase(0.5, 0)]
    [TestCase(-0.5, -1)]
    public void FloorToIntMatchesFloor(double input, int expected)
    {
        Assert.That(Fix64.FloorToInt(From((decimal)input)), Is.EqualTo(expected));
    }

    [Test]
    public void AbsMinMaxClampAndSignBehave()
    {
        Fix64 negative = Fix64.FromInt(-5);
        Fix64 positive = Fix64.FromInt(5);

        Assert.Multiple(() =>
        {
            Assert.That(Fix64.Abs(negative), Is.EqualTo(positive));
            Assert.That(Fix64.Min(negative, positive), Is.EqualTo(negative));
            Assert.That(Fix64.Max(negative, positive), Is.EqualTo(positive));
            Assert.That(Fix64.Sign(negative), Is.EqualTo(-1));
            Assert.That(Fix64.Sign(positive), Is.EqualTo(1));
            Assert.That(Fix64.Sign(Fix64.Zero), Is.EqualTo(0));
            Assert.That(Fix64.Clamp(Fix64.FromInt(10), negative, positive), Is.EqualTo(positive));
            Assert.That(Fix64.Clamp(Fix64.FromInt(-10), negative, positive), Is.EqualTo(negative));
            Assert.That(Fix64.Clamp(Fix64.One, negative, positive), Is.EqualTo(Fix64.One));
        });
    }

    [Test]
    public void EqualityAndHashingAgree()
    {
        Fix64 a = Fix64.Ratio(7, 3);
        Fix64 b = Fix64.Ratio(7, 3);

        Assert.Multiple(() =>
        {
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals((object)b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a.CompareTo(b), Is.EqualTo(0));
            Assert.That(Fix64.Zero.Equals("not a number"), Is.False);
        });
    }
}
