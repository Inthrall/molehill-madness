using System.Linq;
using MoleSim.Match;
using MoleSim.Numerics;

namespace MoleSim.Tests;

/// <summary>
/// The golden corpus: whole matches, played from a script, with their final state hashes
/// pinned.
/// </summary>
/// <remarks>
/// These are not correctness oracles. Nothing here claims the pinned numbers are the
/// <em>right</em> answers, and none of them was derived by hand. What they assert is
/// something else, and something the project cannot do without: that the answer is the
/// <em>same</em> answer, on every platform and after every change.
///
/// That distinction matters when one fails. A failure means either a deliberate rules
/// change, in which case the pin gets updated in the same commit that caused it and the
/// commit message says why, or an accidental divergence, in which case something is
/// broken and the pin must not be touched. A pin quietly refreshed to make a build go
/// green destroys the only net under cross-platform play.
///
/// Because these run under `dotnet test` on every platform in CI, a machine that computes
/// something different fails here directly, naming itself, rather than being caught later
/// by a fingerprint comparison.
///
/// Pin history, so a future reader can tell a deliberate change from an accident:
///
/// Updated when ballistic blasts gained a line-of-sight check and shots fired in mid-air
/// gained the tumble rotation. Both change who takes damage and therefore every match
/// outcome downstream, so all five pins moved together in the commit that caused it.
///
/// Updated again when crates and the knockout reel arrived. Neither changes any existing
/// rule, but both draw from the match generator, and a draw inserted anywhere shifts every
/// draw after it. Worth knowing that this is a whole class of legitimate pin movement:
/// adding a draw is enough on its own, without a single rule having changed.
/// </remarks>
[TestFixture]
public sealed class CorpusTests
{
    /// <summary>
    /// Plays a match from nothing but a seed, so a scenario is reproducible from code
    /// rather than from a stored file.
    /// </summary>
    /// <remarks>
    /// The plan each seat submits is derived from the match seed. This is emphatically not
    /// an opponent: the game has no AI and never will. It is a script, chosen so that
    /// every rule gets exercised, including walking, digging, firing, self-harm and lava.
    /// </remarks>
    private static ulong Play(int playerCount, ulong seed, int rounds, int widthCells = 1200, int heightCells = 480)
    {
        MoleMatch match = MoleMatch.Create(playerCount, seed, widthCells, heightCells);
        MatchRng script = new MatchRng(seed ^ 0xA5A5A5A5UL);

        for (int round = 0; round < rounds; round++)
        {
            for (int seat = 0; seat < playerCount; seat++)
            {
                Mole? actor = match.Eligible(seat).FirstOrDefault();

                if (actor is null)
                {
                    continue;
                }

                int steps = 1 + script.NextInt(3);
                RoutePoint[] route = new RoutePoint[steps];
                int cellX = WorldScale.ToCell(actor.Position.X);
                int cellY = WorldScale.ToCell(actor.Position.Y);

                for (int step = 0; step < steps; step++)
                {
                    cellX += script.NextInt(-90, 91);
                    cellY += script.NextInt(-20, 60);
                    route[step] = new RoutePoint(cellX, cellY);
                }

                WeaponId weapon = script.NextBool() ? WeaponId.ClodLobber : WeaponId.BeetleLauncher;

                PlanAction[] actions =
                {
                    PlanAction.Hop(script.NextInt(10, 40)),
                    PlanAction.Fire(
                        script.NextInt(60, 200),
                        new Vec2(
                            Fix64.FromInt(script.NextInt(-10, 11)),
                            Fix64.FromInt(-script.NextInt(2, 10))),
                        (byte)script.NextInt(90, 256)),
                };

                match.SubmitPlan(new Plan(seat, actor.Index, weapon, route, actions));
            }

            if (match.ResolveRound().MatchOver)
            {
                break;
            }
        }

        return match.StateHash();
    }

    [Test]
    public void TwoPlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 2, seed: 1UL, rounds: 12), Is.EqualTo(0xF8A9B94B539FD4E9UL));
    }

    [Test]
    public void ThreePlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 3, seed: 20260826UL, rounds: 14), Is.EqualTo(0x97FBF45633415553UL));
    }

    [Test]
    public void FourPlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 4, seed: 4242UL, rounds: 16), Is.EqualTo(0x18403F27C0C864C7UL));
    }

    [Test]
    public void AMatchLongEnoughToReachTheLavaIsStable()
    {
        // Past Boiling Point, so the rise, the closing sides and the three-strike rule are
        // all in the hash rather than only the early game.
        Assert.That(Play(playerCount: 4, seed: 777UL, rounds: 26), Is.EqualTo(0xB386A2B208DAF116UL));
    }

    [Test]
    public void ANarrowMapWhereTheSidesCloseEarlyIsStable()
    {
        Assert.That(
            Play(playerCount: 2, seed: 31337UL, rounds: 24, widthCells: 600, heightCells: 320),
            Is.EqualTo(0x585BD4C761D88913UL));
    }

    [Test]
    public void EveryScenarioIsRepeatableWithinASingleRun()
    {
        // Cheap insurance against state leaking between matches through a static
        // somewhere, which a pinned hash on its own would not catch. The two calls per
        // scenario are the point of the test, not a mistake.
        ulong firstTwoPlayer = Play(2, 1UL, 12);
        ulong secondTwoPlayer = Play(2, 1UL, 12);
        ulong firstFourPlayer = Play(4, 4242UL, 16);
        ulong secondFourPlayer = Play(4, 4242UL, 16);

        Assert.Multiple(() =>
        {
            Assert.That(secondTwoPlayer, Is.EqualTo(firstTwoPlayer));
            Assert.That(secondFourPlayer, Is.EqualTo(firstFourPlayer));
        });
    }
}
