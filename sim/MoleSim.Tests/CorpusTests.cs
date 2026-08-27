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
///
/// Updated a third time when a crater stopped being as wide as the blast that made it.
/// Watching whole rounds render for the first time showed the map unrecognisable by round
/// five, which contradicts pacing already fixed in the design: lava arrives at round eight
/// and climbs for several more. Craters were roughly halved and damage left alone, so every
/// shot hurts exactly as much as before and eats about a quarter as much ground. Different
/// terrain from round one onwards means different everything after it.
///
/// Updated a fourth time when the arsenal stopped being unlimited and bracing started doing
/// something. Every weapon but the Clod Lobber now runs out and comes back only from crates,
/// which changes which plans are legal, adds holdings to the hash, and made this script fall
/// back to the Clod Lobber once its Beetle Launchers are gone. Bracing now takes a third off
/// what a blast does, where before it only stopped a mole walking, which is what planning
/// nothing already did. Three deliberate rule changes in one commit, hence one pin move.
///
/// Updated a fifth time when bracing was removed again. It was a button whose only content was
/// the damage bonus, since holding still is what a player who plans nothing already does, and a
/// bonus for staying put pulls against a design that fights bunkering with the stalemate nudge.
/// Planning nothing still braces in place; there is simply nothing to press for it. No behavioural
/// test moved, which is the interesting part: every one of these five pins shifted purely because
/// the braced flag came out of the state hash, and nothing else about any match changed.
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

                // Beetle Launchers run out, and a plan naming something the platoon has none
                // of is refused. A real client would never offer it, so the script falls back
                // the same way, and the draw above still happens either way so the generator's
                // sequence does not depend on how much ammunition is left.
                if (!match.CanUse(seat, weapon))
                {
                    weapon = WeaponId.ClodLobber;
                }

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
        Assert.That(Play(playerCount: 2, seed: 1UL, rounds: 12), Is.EqualTo(0x41C7A60C89A49FD4UL));
    }

    [Test]
    public void ThreePlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 3, seed: 20260826UL, rounds: 14), Is.EqualTo(0x82C51E45A0684DC5UL));
    }

    [Test]
    public void FourPlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 4, seed: 4242UL, rounds: 16), Is.EqualTo(0xAB46AF8FFC2ADD05UL));
    }

    [Test]
    public void AMatchLongEnoughToReachTheLavaIsStable()
    {
        // Past Boiling Point, so the rise, the closing sides and the three-strike rule are
        // all in the hash rather than only the early game.
        Assert.That(Play(playerCount: 4, seed: 777UL, rounds: 26), Is.EqualTo(0x18693C47D80B1221UL));
    }

    [Test]
    public void ANarrowMapWhereTheSidesCloseEarlyIsStable()
    {
        Assert.That(
            Play(playerCount: 2, seed: 31337UL, rounds: 24, widthCells: 600, heightCells: 320),
            Is.EqualTo(0x50B9D4464F73FAE8UL));
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
