using Molehill.Clip;
using MoleSim.Match;

namespace Molehill.Clip.Tests;

/// <summary>
/// That a moment points at the mole it is about.
/// </summary>
/// <remarks>
/// Its own fixture because the existing drama tests are about scoring, and a moment can be scored
/// perfectly while naming the wrong mole. That is precisely what happened: the slot was computed as
/// the seat times the platoon size plus the index, which is the transpose of the interleaved order
/// the moles are actually built in, and every test passed because none of them asked who the moment
/// was about.
///
/// A four-player match has sixteen slots and the two formulas agree on four of them, the ones where
/// the seat and the index are equal. So a test that used two players and looked at one knockout had
/// a decent chance of passing on a wrong answer. These check every slot.
/// </remarks>
[TestFixture]
public sealed class SlotTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;

    /// <summary>
    /// The mapping, stated against the simulation's own construction rather than against a formula
    /// copied from it. If MoleMatch ever changes how it lays moles out, this fails rather than
    /// quietly agreeing with the old order.
    /// </summary>
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void EverySlotRoundTripsThroughSeatAndIndex(int playerCount)
    {
        MoleMatch match = MoleMatch.Create(playerCount, 4242UL, MapWidthCells, MapHeightCells);

        for (int slot = 0; slot < match.Moles.Count; slot++)
        {
            Mole mole = match.Moles[slot];

            // What Drama has to compute to get from a knockout back to a slot.
            int worked = (mole.Index * playerCount) + mole.Seat;

            Assert.That(
                worked, Is.EqualTo(slot),
                $"Seat {mole.Seat} mole {mole.Index} is at slot {slot}, not {worked}.");
        }
    }

    /// <summary>
    /// And the whole way through: a real round, a real knockout, and the moment naming the mole that
    /// actually went off duty. This is the one that would have caught it.
    /// </summary>
    [Test]
    public void TheMomentNamesTheMoleThatWentOff()
    {
        MoleMatch match = MoleMatch.Create(
            playerCount: 4, 424242UL, MapWidthCells, MapHeightCells);

        foreach (Mole mole in match.Moles)
        {
            mole.Pluck = 12;
        }

        RoundResult result = ResolveUntilSomebodyGoesOff(match);

        Assert.That(result.Knockouts, Is.Not.Empty, "Nobody went off, so there is nothing to check.");

        Moment moment = Drama.Best(result);

        Assert.That(moment.Exists, Is.True);
        Assert.That(moment.Slot, Is.InRange(0, match.Moles.Count - 1));

        Mole subject = match.Moles[moment.Slot];

        // The moment is about one of the round's knockouts, so the mole at its slot has to be one of
        // the moles that went off. Transposed, the slot lands on a mole that was still standing.
        Assert.That(
            result.Knockouts.Any(gone => gone.Seat == subject.Seat && gone.MoleIndex == subject.Index),
            Is.True,
            $"The moment points at seat {subject.Seat} mole {subject.Index}, who was not knocked out.");
    }

    private static RoundResult ResolveUntilSomebodyGoesOff(MoleMatch match)
    {
        RoundResult result = match.ResolveRound(record: true);

        for (int round = 0; round < 12 && result.Knockouts.Count == 0; round++)
        {
            foreach (Mole mole in match.Moles)
            {
                if (!mole.IsOffDuty)
                {
                    mole.Pluck = 8;
                }
            }

            result = match.ResolveRound(record: true);
        }

        return result;
    }
}
