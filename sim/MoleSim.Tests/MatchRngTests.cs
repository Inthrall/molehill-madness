using MoleSim.Numerics;

namespace MoleSim.Tests;

/// <summary>
/// The generator is checked against the published xoshiro256** algorithm by hand-working
/// its first few outputs from a known state, rather than by pinning whatever this
/// implementation happens to produce. A snapshot of our own output would only prove the
/// code has not changed; these prove it is the right algorithm.
/// </summary>
[TestFixture]
public sealed class MatchRngTests
{
    [Test]
    public void FirstOutputsFromKnownStateMatchTheAlgorithmByHand()
    {
        // Starting state s = {1, 2, 3, 4}.
        //
        // Draw 1: result = rotl(s1 * 5, 7) * 9 = rotl(10, 7) * 9 = 1280 * 9 = 11520.
        //
        // The state then advances: t = s1 << 17 = 262144; s2 ^= s0 -> 2; s3 ^= s1 -> 6;
        // s1 ^= s2 -> 0; s0 ^= s3 -> 7; s2 ^= t -> 262146; s3 = rotl(6, 45).
        //
        // Draw 2: s1 is now 0, so result = rotl(0, 7) * 9 = 0.
        //
        // Advancing again gives s1 = 262149, so
        // draw 3: result = rotl(262149 * 5, 7) * 9 = (1310745 << 7) * 9
        //                = 167775360 * 9 = 1509978240.
        MatchRng rng = MatchRng.FromState(1, 2, 3, 4);

        Assert.Multiple(() =>
        {
            Assert.That(rng.NextUInt64(), Is.EqualTo(11520UL), "first draw");
            Assert.That(rng.NextUInt64(), Is.EqualTo(0UL), "second draw");
            Assert.That(rng.NextUInt64(), Is.EqualTo(1509978240UL), "third draw");
        });
    }

    [Test]
    public void AllZeroStateIsRejected()
    {
        // xoshiro is stuck forever at all-zero state, so it must never be constructible.
        Assert.Throws<ArgumentException>(() => MatchRng.FromState(0, 0, 0, 0));
    }

    [Test]
    public void SameSeedGivesTheSameSequence()
    {
        MatchRng first = new MatchRng(20260826UL);
        MatchRng second = new MatchRng(20260826UL);

        for (int draw = 0; draw < 1000; draw++)
        {
            Assert.That(second.NextUInt64(), Is.EqualTo(first.NextUInt64()),
                $"sequences diverged at draw {draw}");
        }
    }

    [Test]
    public void NeighbouringSeedsDoNotProduceNeighbouringSequences()
    {
        // A seed of 1 and a seed of 2 must not share a prefix: this is what the SplitMix64
        // seeding step is for, and skipping it is a classic way to get correlated matches.
        MatchRng first = new MatchRng(1UL);
        MatchRng second = new MatchRng(2UL);

        int shared = 0;
        for (int draw = 0; draw < 32; draw++)
        {
            if (first.NextUInt64() == second.NextUInt64())
            {
                shared++;
            }
        }

        Assert.That(shared, Is.Zero, "adjacent seeds produced identical draws");
    }

    [Test]
    public void SnapshotAndRestoreResumeTheSameSequence()
    {
        MatchRng rng = new MatchRng(4242UL);

        for (int draw = 0; draw < 17; draw++)
        {
            rng.NextUInt64();
        }

        rng.Snapshot(out ulong s0, out ulong s1, out ulong s2, out ulong s3);
        MatchRng restored = MatchRng.FromState(s0, s1, s2, s3);

        for (int draw = 0; draw < 100; draw++)
        {
            Assert.That(restored.NextUInt64(), Is.EqualTo(rng.NextUInt64()),
                $"restored generator diverged at draw {draw}");
        }
    }

    [Test]
    public void NextIntStaysInsideItsBound()
    {
        MatchRng rng = new MatchRng(7UL);

        for (int draw = 0; draw < 20_000; draw++)
        {
            int value = rng.NextInt(6);
            Assert.That(value, Is.InRange(0, 5));
        }
    }

    [Test]
    public void NextIntWithBoundOfOneIsAlwaysZero()
    {
        MatchRng rng = new MatchRng(99UL);

        for (int draw = 0; draw < 100; draw++)
        {
            Assert.That(rng.NextInt(1), Is.Zero);
        }
    }

    [Test]
    public void NextIntCoversItsWholeRangeRoughlyEvenly()
    {
        // Not a statistical proof, just a guard against an off-by-one that silently never
        // returns the top value, or a modulo bias bad enough to see.
        const int Sides = 6;
        const int Draws = 60_000;

        MatchRng rng = new MatchRng(20260826UL);
        int[] counts = new int[Sides];

        for (int draw = 0; draw < Draws; draw++)
        {
            counts[rng.NextInt(Sides)]++;
        }

        int expected = Draws / Sides;
        foreach (int count in counts)
        {
            Assert.That(count, Is.GreaterThan(0), "a result never came up at all");
            Assert.That(Math.Abs(count - expected), Is.LessThan(expected / 5),
                "distribution is further off than sampling noise explains");
        }
    }

    [Test]
    public void NextIntRangeRespectsBothEnds()
    {
        MatchRng rng = new MatchRng(11UL);

        for (int draw = 0; draw < 5000; draw++)
        {
            Assert.That(rng.NextInt(-3, 4), Is.InRange(-3, 3));
        }
    }

    [Test]
    public void NextIntRejectsAnEmptyRange()
    {
        MatchRng rng = new MatchRng(1UL);

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        });
    }

    [Test]
    public void NextFractionStaysBelowOne()
    {
        MatchRng rng = new MatchRng(5150UL);

        for (int draw = 0; draw < 20_000; draw++)
        {
            Fix64 value = rng.NextFraction();
            Assert.That(value, Is.GreaterThanOrEqualTo(Fix64.Zero));
            Assert.That(value, Is.LessThan(Fix64.One));
        }
    }

    [Test]
    public void NextFix64StaysInsideItsRange()
    {
        MatchRng rng = new MatchRng(31337UL);
        Fix64 low = Fix64.FromInt(-8);
        Fix64 high = Fix64.FromInt(8);

        for (int draw = 0; draw < 20_000; draw++)
        {
            Fix64 value = rng.NextFix64(low, high);
            Assert.That(value, Is.GreaterThanOrEqualTo(low));
            Assert.That(value, Is.LessThan(high));
        }
    }

    [Test]
    public void NextBoolProducesBothOutcomes()
    {
        MatchRng rng = new MatchRng(2024UL);
        int trues = 0;

        for (int draw = 0; draw < 10_000; draw++)
        {
            if (rng.NextBool())
            {
                trues++;
            }
        }

        Assert.That(trues, Is.InRange(4500, 5500));
    }
}
