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
///
/// Updated a sixth time when the map became a cave system. The surface moved from a quarter of the
/// way down to a third, so two thirds of the map is now underground rather than half of it being
/// strata nobody reached, and the underground is hollowed out into caves by interpolated noise
/// instead of being solid. Every route used to be over the top; now most of the map is somewhere to
/// go. Different ground from the first tick means different everything after it, which is the same
/// class of movement as the crater change: no rule about what a mole or a shell does was touched,
/// only the field they do it on. The caves stay buried under a roof of solid ground, which is what
/// keeps the spawn points where they were, and <see cref="MapMakerTests"/> defends that rather than
/// leaving it to these pins to notice.
///
/// Updated a seventh time when garden clutter arrived on the surface. Pots, mushrooms, logs,
/// fences, gnomes and bird baths are built out of terrain rather than drawn over it, so they are
/// cover you can hide behind and blow apart on the same terms as everything else, and the ground a
/// match is played on is different from the first tick again. Same class of movement as before: the
/// field changed, no rule about what a mole or a shell does was touched.
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

    // Repinned 2026-08-28, fourth commit: a jump keeps its momentum through a dig, and landing
    // snaps down. Hitting a ceiling used to stop a mole dead, so a hop into a roof bought one body
    // length of tunnel however hard it was going; the rise is spent going through it now and is
    // charged by the substep. And an airborne mole that has stopped rising with something under it
    // lands and sits on it, rather than sailing to the apex and falling back down its own shaft.
    //
    // Repinned earlier the same day: being off the ground stopped meaning being a passenger.
    // A mole can steer sideways in mid-air, capped at walking pace, and one pushed into a ceiling or
    // a wall while airborne digs into it instead of bouncing off, which is the design's seamless
    // move finally applied to the one case that never obeyed it. Landing changed with it: it is
    // decided by how fast a body is closing on the surface rather than how fast it is going, because
    // air control by itself exceeds the settle speed and a steered fall never landed at all.
    //
    // Every route through the air is different, so all five moved. Four rule tests went in with it.
    //
    // Repinned earlier the same day: a crate no longer carves a hole when it lands. It used
    // to punch one of the claim radius so the last stretch had to be dug, which was right while a
    // crate buried itself and became actively wrong once one rested on a ledge: the hole was
    // centred on the crate, six cells above its floor, so a landing crate removed the ground it was
    // sitting on and dropped whoever was waiting there out of claim range. Terrain changed, so all
    // five moved; the rule test added with it says the floor survives.
    //
    // Repinned earlier the same day, and five rule changes were in it. Listed because between them they touch
    // the ground, the crates and the moles, which is about as wide as a single repin should ever be.
    //
    // The caves keep their roof per column instead of measuring it from the deepest point on the
    // map, so they reach the ground near the surface everywhere rather than only under the valleys,
    // and the roof came down from ten cells to four. A chamber now promises sixteen cells of
    // headroom rather than fourteen, which qualifies fewer ledges. Crates rest on a ledge instead of
    // burying themselves a metre in, and choose between the open air and the burrows on a coin
    // rather than picking uniformly from a column's ledges, which is what makes the split even
    // instead of three quarters underground. A mole braces in a shaft it has dug, so digging
    // straight up works at last and every route that goes through one ends somewhere else. And
    // everybody surfaces facing whichever way the terrain hash says rather than all facing right,
    // which changes where the first shot of every match leaves from.
    //
    // Every rule test passed throughout, which is what says these were changes to the rules and not
    // damage to them. Three new ones went in with the digging.
    //
    // Repinned earlier the same day: half the spawns start on a cave floor now
    // instead of everybody starting on the surface. Where sixteen moles stand at tick zero decides
    // every route, shot and crater after it, so all five pins moved and no rule test did. Chosen off
    // the terrain hash rather than a generator, so the map's own random sequence is untouched.
    //
    // Repinned earlier the same day: the caves changed shape on purpose. They now run all the way down to
    // the bedrock instead of stopping a fifth of the map short of it, and the noise block that sets
    // their scale went from twelve cells to thirty-six, so a chamber is three moles across rather
    // than one. Different ground means different everything downstream of it, and all five pins
    // moved together while every rule test kept passing, which is what a map change looks like as
    // opposed to a rules change. No RNG draw was added or removed.
    [Test]
    public void TwoPlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 2, seed: 1UL, rounds: 12), Is.EqualTo(0xBEAB24433AAE0D53UL));
    }

    [Test]
    public void ThreePlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 3, seed: 20260826UL, rounds: 14), Is.EqualTo(0xFC88D6765B472E21UL));
    }

    [Test]
    public void FourPlayerMatchIsStable()
    {
        Assert.That(Play(playerCount: 4, seed: 4242UL, rounds: 16), Is.EqualTo(0x2E52E2EF2C5106B8UL));
    }

    [Test]
    public void AMatchLongEnoughToReachTheLavaIsStable()
    {
        // Past Boiling Point, so the rise, the closing sides and the three-strike rule are
        // all in the hash rather than only the early game.
        Assert.That(Play(playerCount: 4, seed: 777UL, rounds: 26), Is.EqualTo(0x75C730FA81EDF322UL));
    }

    [Test]
    public void ANarrowMapWhereTheSidesCloseEarlyIsStable()
    {
        Assert.That(
            Play(playerCount: 2, seed: 31337UL, rounds: 24, widthCells: 600, heightCells: 320),
            Is.EqualTo(0x3F36D54DC1B33AE8UL));
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
