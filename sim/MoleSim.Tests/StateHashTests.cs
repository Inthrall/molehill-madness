using MoleSim.Match;
using MoleSim.Numerics;
using NUnit.Framework;

namespace MoleSim.Tests;

/// <summary>
/// What the desync detector actually detects.
/// </summary>
/// <remarks>
/// StateHash is the live cross-device check: every client reports one per round, the relay collects
/// them, and two that disagree are a determinism bug caught in the field with a perfect reproduction
/// attached. It is also what the golden corpus pins. Both of those are worth exactly as much as the
/// hash's coverage, and it used to cover the moles and the terrain and little else that survives a
/// round: not the generator, not the traps in the ground, not the crates in the air, not the pacing
/// multiplier, not whose turn it is.
///
/// The generator was the one that mattered most. A single extra or missing draw on one device shifts
/// every draw after it, so two machines could hold identical worlds, hash identically, and be about
/// to diverge on the next thing either of them rolled.
/// </remarks>
[TestFixture]
public sealed class StateHashTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;

    /// <summary>
    /// Reading the state must not change it. Worth asserting rather than assuming, because the hash
    /// now reaches into the random generator, and a snapshot that advanced it would make the act of
    /// checking for divergence the thing that caused one.
    /// </summary>
    [Test]
    public void HashingDoesNotDisturbTheMatch()
    {
        MoleMatch match = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);

        match.ResolveRound();

        ulong once = match.StateHash();
        ulong twice = match.StateHash();

        Assert.That(twice, Is.EqualTo(once));

        // And the match still resolves to the same place as one that was never hashed.
        MoleMatch untouched = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);
        untouched.ResolveRound();

        match.ResolveRound();
        untouched.ResolveRound();

        Assert.That(
            match.StateHash(), Is.EqualTo(untouched.StateHash()),
            "Hashing a match changed how it played.");
    }

    /// <summary>
    /// Two matches whose worlds look the same but whose generators do not. This is the divergence
    /// the old hash could not see: everything visible agrees, and the very next draw differs.
    /// </summary>
    [Test]
    public void TwoMatchesThatWillDivergeNextDrawDoNotHashTheSame()
    {
        MoleMatch steady = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);
        MoleMatch nudged = MoleMatch.Create(2, 4242UL, MapWidthCells, MapHeightCells);

        Assert.That(nudged.StateHash(), Is.EqualTo(steady.StateHash()), "They start identical.");

        // One extra draw on one of them, which is exactly what an accidental extra call looks like.
        nudged.Rng.NextUInt64();

        Assert.That(
            nudged.StateHash(), Is.Not.EqualTo(steady.StateHash()),
            "A generator that has moved on is a match that is about to diverge.");
    }

    /// <summary>
    /// The same match, resolved the same way, hashes the same. The point of widening the hash was to
    /// catch more divergence, not to invent some.
    /// </summary>
    [Test]
    public void TheSameMatchPlayedTwiceStillAgrees()
    {
        Assert.That(Played(4, 777UL, 6), Is.EqualTo(Played(4, 777UL, 6)));
    }

    private static ulong Played(int playerCount, ulong seed, int rounds)
    {
        MoleMatch match = MoleMatch.Create(playerCount, seed, MapWidthCells, MapHeightCells);

        for (int round = 0; round < rounds; round++)
        {
            match.ResolveRound();
        }

        return match.StateHash();
    }
}
