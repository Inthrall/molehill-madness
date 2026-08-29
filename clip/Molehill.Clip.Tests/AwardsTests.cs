using System.Collections.Generic;
using Molehill.Clip;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace Molehill.Clip.Tests;

/// <summary>
/// The mole of the match, which is a reading of what happened rather than a rule about it.
/// </summary>
[TestFixture]
public sealed class AwardsTests
{
    private const int Seats = 4;
    private const int Moles = 4;

    private static readonly Knockout[] NobodyOut = System.Array.Empty<Knockout>();
    private static readonly CrateClaim[] NoCrates = System.Array.Empty<CrateClaim>();
    private static readonly BlastHit[] NoHits = System.Array.Empty<BlastHit>();

    private static Award Read(
        BlastHit[]? hits = null, Knockout[]? out_ = null, CrateClaim[]? crates = null) =>
        Awards.From(hits ?? NoHits, out_ ?? NobodyOut, crates ?? NoCrates, Seats, Moles);

    /// <summary>A hit on somebody, by somebody.</summary>
    private static BlastHit Hit(
        int seat, int mole, int damage, int bySeat = -1, int byMole = -1, bool out_ = false) =>
        new BlastHit(seat, mole, damage, out_, bySeat, byMole);

    [Test]
    public void NothingHappenedSoNobodyIsHonoured()
    {
        Award award = Read();

        Assert.Multiple(() =>
        {
            Assert.That(award.Exists, Is.False);
            Assert.That(award.Feat, Is.EqualTo(Feat.None));
        });
    }

    /// <summary>
    /// Damage dealt to other platoons is the headline feat.
    /// </summary>
    [Test]
    public void TheHardestHitterTakesIt()
    {
        Award award = Read(new[]
        {
            Hit(seat: 1, mole: 0, damage: 30, bySeat: 0, byMole: 2),
            Hit(seat: 2, mole: 1, damage: 45, bySeat: 0, byMole: 2),
            Hit(seat: 3, mole: 0, damage: 20, bySeat: 1, byMole: 0),
        });

        Assert.Multiple(() =>
        {
            Assert.That(award.Feat, Is.EqualTo(Feat.Ordnance));
            Assert.That(award.Seat, Is.EqualTo(0));
            Assert.That(award.MoleIndex, Is.EqualTo(2));
            Assert.That(award.Score, Is.EqualTo(75), "both its hits should count");
        });
    }

    /// <summary>
    /// Damage totals accumulate across the whole match rather than the best round.
    /// </summary>
    [Test]
    public void DamageAddsUpAcrossRounds()
    {
        Award award = Read(new[]
        {
            Hit(seat: 1, mole: 0, damage: 20, bySeat: 0, byMole: 0),
            Hit(seat: 1, mole: 1, damage: 20, bySeat: 0, byMole: 0),
            Hit(seat: 1, mole: 2, damage: 35, bySeat: 0, byMole: 3),
        });

        Assert.Multiple(() =>
        {
            Assert.That(award.Seat, Is.EqualTo(0));
            Assert.That(award.MoleIndex, Is.EqualTo(0), "forty across two rounds beats thirty-five in one");
            Assert.That(award.Score, Is.EqualTo(40));
        });
    }

    /// <summary>
    /// Hitting your own platoon is not ordnance, and it has a feat of its own.
    /// </summary>
    /// <remarks>
    /// Friendly fire is always on with no toggle, so this is a thing that genuinely happens rather
    /// than a curiosity. It must not read as marksmanship.
    /// </remarks>
    [Test]
    public void ShootingYourOwnPlatoonIsNotMarksmanship()
    {
        Award award = Read(new[] { Hit(seat: 0, mole: 1, damage: 60, bySeat: 0, byMole: 0) });

        Assert.Multiple(() =>
        {
            Assert.That(award.Feat, Is.EqualTo(Feat.OwnGoal));
            Assert.That(award.Seat, Is.EqualTo(0));
            Assert.That(award.MoleIndex, Is.EqualTo(0), "the one who fired, not the one who wore it");
            Assert.That(award.Score, Is.EqualTo(60));
        });
    }

    /// <summary>Blowing yourself up counts as an own goal, being the purest form of one.</summary>
    [Test]
    public void BlowingYourselfUpCounts()
    {
        Award award = Read(new[] { Hit(seat: 2, mole: 3, damage: 50, bySeat: 2, byMole: 3) });

        Assert.Multiple(() =>
        {
            Assert.That(award.Feat, Is.EqualTo(Feat.OwnGoal));
            Assert.That(award.Seat, Is.EqualTo(2));
            Assert.That(award.MoleIndex, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// An own goal outranks surviving, because in this game it is the better story.
    /// </summary>
    [Test]
    public void AnOwnGoalBeatsMerelySurviving()
    {
        Award award = Read(new[]
        {
            // Somebody took a beating from an unattributable source and lived.
            Hit(seat: 3, mole: 0, damage: 90),

            // And somebody else shot their own.
            Hit(seat: 1, mole: 1, damage: 15, bySeat: 1, byMole: 2),
        });

        Assert.That(award.Feat, Is.EqualTo(Feat.OwnGoal));
    }

    /// <summary>
    /// The survivor has to have survived. A mole that took the most and went out is not one.
    /// </summary>
    [Test]
    public void TheSurvivorHasToStillBeStanding()
    {
        Award award = Read(
            new[]
            {
                Hit(seat: 0, mole: 0, damage: 100, out_: true),
                Hit(seat: 1, mole: 1, damage: 40),
            },
            new[] { new Knockout(0, 0, KnockoutCause.Explosion, KnockoutExit.SpinAndPoof) });

        Assert.Multiple(() =>
        {
            Assert.That(award.Feat, Is.EqualTo(Feat.Survivor));
            Assert.That(award.Seat, Is.EqualTo(1), "the one who took a hundred did not survive it");
            Assert.That(award.MoleIndex, Is.EqualTo(1));
            Assert.That(award.Score, Is.EqualTo(40));
        });
    }

    /// <summary>Nobody to credit is a real answer, and it must not credit seat zero by accident.</summary>
    [Test]
    public void LavaCreditsNobodyForTheDamage()
    {
        Award award = Read(new[]
        {
            // Lava, and a trap that remembers only the seat that laid it.
            Hit(seat: 0, mole: 0, damage: 10),
            new BlastHit(1, 1, 35, false, bySeat: 2, byMoleIndex: -1),
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                award.Feat, Is.EqualTo(Feat.Survivor),
                "neither hit is attributable to a mole, so nobody dealt anything");

            Assert.That(award.Seat, Is.EqualTo(1), "the one who took thirty-five and lived");
        });
    }

    /// <summary>With no damage anywhere, the crate hoarder gets it.</summary>
    [Test]
    public void FailingEverythingElseTheScavengerTakesIt()
    {
        Award award = Read(crates: new[]
        {
            new CrateClaim(2, 1, default, shattered: false),
            new CrateClaim(2, 1, default, shattered: false),
            new CrateClaim(0, 0, default, shattered: false),

            // A shattered crate went to nobody and must not be credited.
            new CrateClaim(-1, -1, default, shattered: true),
        });

        Assert.Multiple(() =>
        {
            Assert.That(award.Feat, Is.EqualTo(Feat.Scavenger));
            Assert.That(award.Seat, Is.EqualTo(2));
            Assert.That(award.MoleIndex, Is.EqualTo(1));
            Assert.That(award.Score, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// A real match produces an award, and two readings of it agree.
    /// </summary>
    /// <remarks>
    /// The other half of the split. Everything above states an opinion about precedence against
    /// tallies chosen to isolate it; this one proves the reading survives contact with rounds the
    /// simulation actually produced, which is the only way to find out that, say, nothing ever
    /// populates the attribution in practice.
    /// </remarks>
    [Test]
    public void ARealMatchHasAMoleOfTheMatch()
    {
        List<RoundResult> rounds = Played();

        Award first = Awards.MoleOfTheMatch(rounds, 2, MatchSettings.MolesPerPlatoon);
        Award again = Awards.MoleOfTheMatch(rounds, 2, MatchSettings.MolesPerPlatoon);

        Assert.Multiple(() =>
        {
            Assert.That(first.Exists, Is.True, "a match with shots fired honoured nobody");
            Assert.That(first.Score, Is.GreaterThan(0));
            Assert.That(again.Seat, Is.EqualTo(first.Seat));
            Assert.That(again.MoleIndex, Is.EqualTo(first.MoleIndex));
            Assert.That(again.Feat, Is.EqualTo(first.Feat));
            Assert.That(again.Score, Is.EqualTo(first.Score));
        });
    }

    /// <summary>
    /// And the attribution it rests on is actually populated by a real round.
    /// </summary>
    /// <remarks>
    /// The claim worth defending on its own, because everything above would pass just as happily
    /// against a simulation that recorded -1 for every attacker. This is the test that would have
    /// caught the gap that made the award impossible in the first place.
    /// </remarks>
    [Test]
    public void ARealRoundSaysWhoCausedItsHits()
    {
        int attributed = 0;

        foreach (RoundResult round in Played())
        {
            foreach (BlastHit hit in round.Hits)
            {
                if (hit.BySeat >= 0 && hit.ByMoleIndex >= 0)
                {
                    attributed++;
                }
            }
        }

        Assert.That(attributed, Is.GreaterThan(0), "no hit in a whole match knew who caused it");
    }

    /// <summary>Two platoons lobbing at each other until somebody wins or it runs long.</summary>
    private static List<RoundResult> Played()
    {
        TerrainGrid ground = MapMaker.Field(400, 300, 20260828UL);
        MoleMatch match = MoleMatch.Create(ground, 2, 20260828UL);
        List<RoundResult> rounds = new List<RoundResult>();

        for (int round = 0; round < 12; round++)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                Mole? actor = null;

                foreach (Mole candidate in match.Eligible(seat))
                {
                    actor = candidate;
                    break;
                }

                if (actor is null)
                {
                    continue;
                }

                match.SubmitPlan(new Plan(
                    seat,
                    actor.Index,
                    WeaponId.ClodLobber,
                    System.Array.Empty<RoutePoint>(),
                    new[]
                    {
                        PlanAction.Fire(
                            tick: 10,
                            aim: new Vec2(Fix64.FromInt(seat == 0 ? 3 : -3), Fix64.FromInt(-1)),
                            power: 210),
                    }));
            }

            RoundResult result = match.ResolveRound();
            rounds.Add(result);

            if (result.MatchOver)
            {
                break;
            }
        }

        return rounds;
    }
}
