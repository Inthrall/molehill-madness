using Molehill.Clip;
using MoleSim.Match;
using MoleSim.Numerics;

namespace Molehill.Clip.Tests;

/// <summary>
/// The drama scorer: which moment out of a match is the one worth showing somebody.
/// </summary>
/// <remarks>
/// Two kinds of test here, and the split is deliberate. The ordering claims are checked directly
/// against the scoring function, because those are the opinions this file is asserting about the game
/// and they should fail loudly if somebody retunes a weight and breaks one by accident. The rest run
/// against real resolved rounds, because a scorer that agrees with itself about a fabricated
/// RoundResult has proved nothing about a match.
/// </remarks>
[TestFixture]
public sealed class DramaTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;
    private const ulong Seed = 424242UL;

    // ---- The opinions -------------------------------------------------------------------

    /// <summary>
    /// A knockout beats any hit, however hard. A round where somebody went off duty is a better clip
    /// than one where somebody merely took a lot of damage, and no amount of damage should close that
    /// gap.
    /// </summary>
    [Test]
    public void AnyKnockoutBeatsTheHardestPossibleHit()
    {
        Assert.That(
            Drama.Score(MomentKind.Knockout),
            Is.GreaterThan(Drama.Score(MomentKind.Hit, damage: 100)),
            "The hardest weapon in the game does 75, so 100 is past the ceiling.");
    }

    /// <summary>One shot catching two platoons is the story of the whole match.</summary>
    [Test]
    public void CatchingTwoBeatsCatchingOne()
    {
        Assert.That(
            Drama.Score(MomentKind.TwoAtOnce, caughtTogether: 2),
            Is.GreaterThan(Drama.Score(MomentKind.Knockout)));

        Assert.That(
            Drama.Score(MomentKind.TwoAtOnce, caughtTogether: 3),
            Is.GreaterThan(Drama.Score(MomentKind.TwoAtOnce, caughtTogether: 2)));
    }

    [Test]
    public void TheShotThatEndsTheMatchBeatsEverything()
    {
        Assert.That(
            Drama.Score(MomentKind.Winner),
            Is.GreaterThan(Drama.Score(MomentKind.TwoAtOnce, caughtTogether: 2)));
    }

    [Test]
    public void NothingHappeningIsWorthNothing()
    {
        Assert.That(Drama.Score(MomentKind.Nothing), Is.Zero);
        Assert.That(Drama.Score(MomentKind.Nothing, caughtTogether: 4, damage: 90), Is.Zero);
    }

    /// <summary>A harder hit is a better hit, which is the only thing damage decides.</summary>
    [Test]
    public void MoreDamageIsWorthMore()
    {
        Assert.That(
            Drama.Score(MomentKind.Hit, damage: 60),
            Is.GreaterThan(Drama.Score(MomentKind.Hit, damage: 30)));
    }

    // ---- Against real rounds ------------------------------------------------------------

    [Test]
    public void ARoundWhereNothingHappenedOffersNothing()
    {
        MoleMatch match = World();

        // Everybody stands still: no plans at all, which is what a fully forfeited round is.
        RoundResult result = match.ResolveRound(record: true);

        Assert.That(Drama.Best(result).Exists, Is.False);
    }

    /// <summary>
    /// A clip has to be cut around a tick, so a round resolved without a recording scores nothing
    /// rather than guessing one. A clip starting in the wrong place is worse than no clip.
    /// </summary>
    [Test]
    public void ARoundWithNoRecordingOffersNothingEvenIfSomethingHappened()
    {
        MoleMatch match = Frail();

        Shoot(match, 0);
        Shoot(match, 1);

        RoundResult result = match.ResolveRound(record: false);

        Assert.That(result.Recording, Is.Null);
        Assert.That(Drama.Best(result).Exists, Is.False);
    }

    [Test]
    public void ARoundWithAKnockoutInItOffersOne()
    {
        Moment found = FirstMomentOfAFrailMatch(out RoundResult _);

        Assert.That(found.Exists, Is.True);
        Assert.That(
            found.Kind,
            Is.EqualTo(MomentKind.Knockout)
                .Or.EqualTo(MomentKind.TwoAtOnce)
                .Or.EqualTo(MomentKind.Winner));
        Assert.That(found.Tick, Is.GreaterThanOrEqualTo(0), "A moment with no tick cannot be cut.");
    }

    /// <summary>
    /// The moment is inside the round it came from, which is the one thing a renderer needs to be
    /// able to trust before it starts replaying anything.
    /// </summary>
    [Test]
    public void TheMomentsTickIsInsideTheRecording()
    {
        Moment found = FirstMomentOfAFrailMatch(out RoundResult result);

        Assert.That(found.Tick, Is.LessThan(result.Recording!.Ticks));
        Assert.That(found.Round, Is.EqualTo(result.Round));
    }

    /// <summary>
    /// Two builds have to pick the same moment out of the same match, which is what makes "the clip
    /// it chose was wrong" a thing anybody can investigate on a different machine.
    /// </summary>
    [Test]
    public void ScoringTheSameMatchTwiceChoosesTheSameMoment()
    {
        List<RoundResult> first = Play();
        List<RoundResult> again = Play();

        Moment one = Drama.Best(first);
        Moment two = Drama.Best(again);

        Assert.That(one.Exists, Is.True, "The driven match produced nothing to score.");
        Assert.That(two.Round, Is.EqualTo(one.Round));
        Assert.That(two.Tick, Is.EqualTo(one.Tick));
        Assert.That(two.Score, Is.EqualTo(one.Score));
        Assert.That(two.Kind, Is.EqualTo(one.Kind));
    }

    [Test]
    public void TheBestMomentOfAMatchIsTheBestOfItsRounds()
    {
        List<RoundResult> rounds = Play();

        int best = rounds.Select(round => Drama.Best(round).Score).Max();

        Assert.That(Drama.Best(rounds).Score, Is.EqualTo(best));
    }

    [Test]
    public void AMatchWithNoRoundsInItOffersNothing()
    {
        Assert.That(Drama.Best(Array.Empty<RoundResult>()).Exists, Is.False);
        Assert.That(Drama.Best((IEnumerable<RoundResult>)null!).Exists, Is.False);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static MoleMatch World() =>
        MoleMatch.Create(playerCount: 2, Seed, MapWidthCells, MapHeightCells);

    /// <summary>
    /// A match rigged so knockouts happen straight away.
    /// </summary>
    /// <remarks>
    /// The same trick the client's own driver uses under a flag. Waiting for a fair match to produce
    /// a knockout takes rounds and depends on the seed, and what is under test here is the scoring
    /// rather than the ballistics.
    /// </remarks>
    private static MoleMatch Frail()
    {
        MoleMatch match = World();

        foreach (Mole mole in match.Moles)
        {
            mole.Pluck = 12;
        }

        return match;
    }

    private static void Shoot(MoleMatch match, int seat)
    {
        Mole? actor = match.Eligible(seat).FirstOrDefault();

        if (actor is null)
        {
            return;
        }

        match.SubmitPlan(new Plan(
            seat,
            actor.Index,
            WeaponId.ClodLobber,
            Array.Empty<RoutePoint>(),
            new[]
            {
                PlanAction.Fire(
                    tick: 12 + seat,
                    aim: new Vec2(Fix64.FromInt(seat == 0 ? 3 : -3), Fix64.FromInt(-1)),
                    power: 210),
            }));
    }

    /// <summary>Plays a frail match out until somebody wins or it runs long.</summary>
    private static List<RoundResult> Play()
    {
        MoleMatch match = Frail();
        List<RoundResult> rounds = new List<RoundResult>();

        for (int round = 0; round < 12; round++)
        {
            Shoot(match, 0);
            Shoot(match, 1);

            RoundResult result = match.ResolveRound(record: true);
            rounds.Add(result);

            if (result.MatchOver)
            {
                break;
            }
        }

        return rounds;
    }

    private static Moment FirstMomentOfAFrailMatch(out RoundResult scored)
    {
        foreach (RoundResult result in Play())
        {
            if (Drama.Best(result) is Moment found && found.Exists
                && found.Kind != MomentKind.Hit)
            {
                scored = result;

                return found;
            }
        }

        Assert.Fail("A frail match produced no knockout to score.");
        scored = null!;

        return Moment.Nothing;
    }
}
