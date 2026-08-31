using System;
using System.Linq;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

/// <summary>
/// The recording is what a client watches, and the whole point of it is that it is a
/// faithful account of a round that has already finished. These are the two properties that
/// have to hold for that to be true: it changes nothing, and replaying it lands exactly
/// where the round did.
/// </summary>
[TestFixture]
public sealed class RoundRecordingTests
{
    private const int WidthCells = 900;
    private const int HeightCells = 400;
    private const int SurfaceCell = 100;

    private static TerrainGrid FlatField()
    {
        TerrainGrid grid = new TerrainGrid(WidthCells, HeightCells);
        grid.FillRectangle(0, SurfaceCell, WidthCells, 3, Material.Turf);
        grid.FillRectangle(0, SurfaceCell + 3, WidthCells, 34, Material.LooseSoil);
        grid.FillRectangle(0, SurfaceCell + 37, WidthCells, HeightCells - SurfaceCell - 47, Material.PackedSoil);
        grid.FillRectangle(0, HeightCells - 10, WidthCells, 10, Material.Bedrock);
        return grid;
    }

    private static MoleMatch NewMatch() =>
        MoleMatch.Create(FlatField(), 4, 20260826UL);

    /// <summary>A shot fired into the ground, which is the cheapest way to make craters.</summary>
    private static Plan Shell(int seat, int moleIndex = 0) =>
        new Plan(
            seat,
            moleIndex,
            WeaponId.ClodLobber,
            Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(3, new Vec2(Fix64.One, Fix64.One), 200) });

    /// <summary>Plays a few rounds of everybody shelling the dirt, and hashes the result.</summary>
    private static ulong PlayOut(bool record)
    {
        MoleMatch match = NewMatch();

        // A mole gets one turn a cycle, so each round is a different one. Four rounds is
        // exactly one full cycle of a platoon.
        for (int round = 0; round < MatchSettings.MolesPerPlatoon; round++)
        {
            for (int seat = 0; seat < match.PlayerCount; seat++)
            {
                match.SubmitPlan(Shell(seat, round));
            }

            match.ResolveRound(record);
        }

        return match.StateHash();
    }

    [Test]
    public void RecordingARoundChangesNothingAboutIt()
    {
        // The corpus only ever resolves unrecorded rounds, so if watching a round could
        // nudge it the corpus would never notice. This is the test that says it cannot.
        Assert.That(PlayOut(record: true), Is.EqualTo(PlayOut(record: false)));
    }

    [Test]
    public void ReplayingTheJournalRebuildsTheMapTheRoundLeftBehind()
    {
        MoleMatch match = NewMatch();
        TerrainGrid shadow = match.Terrain.Clone();

        for (int seat = 0; seat < match.PlayerCount; seat++)
        {
            match.SubmitPlan(Shell(seat));
        }

        RoundRecording recording = match.ResolveRound(record: true).Recording!;

        Assume.That(
            recording.TerrainChanges, Is.Not.Empty,
            "the round has to actually dig something for this to prove anything");

        foreach (TerrainChange change in recording.TerrainChanges)
        {
            shadow.Apply(change);
        }

        // A client draws the shadow, so if these two ever disagree the map somebody is
        // looking at is not the map they are playing on.
        Assert.That(shadow.Hash, Is.EqualTo(match.Terrain.Hash));
    }

    [Test]
    public void TheJournalIsInTheOrderThingsHappened()
    {
        MoleMatch match = NewMatch();

        for (int seat = 0; seat < match.PlayerCount; seat++)
        {
            match.SubmitPlan(Shell(seat));
        }

        RoundRecording recording = match.ResolveRound(record: true).Recording!;
        int previous = 0;

        for (int tick = 0; tick < recording.Ticks; tick++)
        {
            int upTo = recording.ChangesUpTo(tick);

            Assert.That(upTo, Is.GreaterThanOrEqualTo(previous), $"tick {tick} went backwards");
            previous = upTo;
        }

        Assert.That(
            previous, Is.EqualTo(recording.TerrainChanges.Count),
            "the last tick has to account for every change");
    }

    [Test]
    public void AnUnrecordedRoundKeepsNoJournal()
    {
        MoleMatch match = NewMatch();
        match.SubmitPlan(Shell(0));

        // Not merely unused: not built. Thousands of corpus rounds should pay nothing for
        // a list nobody reads.
        Assert.That(match.ResolveRound(record: false).Recording, Is.Null);
    }

    [Test]
    public void ADamageNumberPopsAtTheTickItLanded()
    {
        MoleMatch match = NewMatch();
        Mole target = match.Moles.Single(mole => mole.Seat == 1 && mole.Index == 0);
        Mole shooter = match.Moles.Single(mole => mole.Seat == 0 && mole.Index == 0);

        // Point blank, so it cannot miss, and at a known tick.
        target.Position = shooter.Position + new Vec2(Fix64.Ratio(1, 2), Fix64.Zero);
        match.SubmitPlan(new Plan(
            0, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(10, new Vec2(Fix64.One, Fix64.Zero), 255) }));

        RoundResult result = match.ResolveRound(record: true);
        RoundRecording recording = result.Recording!;

        Assume.That(result.Hits, Is.Not.Empty, "the shot has to connect for this to mean anything");

        Assert.Multiple(() =>
        {
            Assert.That(recording.HitsUpTo(9), Is.Zero, "nothing had landed yet");
            Assert.That(
                recording.HitsUpTo(recording.Ticks - 1), Is.EqualTo(result.Hits.Count),
                "every hit is accounted for by the end");
        });
    }

    [Test]
    public void InterpolationStopsAtTheMomentAMoleGoesOffDuty()
    {
        MoleMatch match = NewMatch();
        Mole doomed = match.Moles.Single(mole => mole.Seat == 1 && mole.Index == 0);
        Mole shooter = match.Moles.Single(mole => mole.Seat == 0 && mole.Index == 0);

        doomed.Pluck = 1;
        doomed.Position = shooter.Position + new Vec2(Fix64.Ratio(1, 2), Fix64.Zero);

        match.SubmitPlan(new Plan(
            0, 0, WeaponId.ClodLobber, Array.Empty<RoutePoint>(),
            new[] { PlanAction.Fire(10, new Vec2(Fix64.One, Fix64.Zero), 255) }));

        RoundRecording recording = match.ResolveRound(record: true).Recording!;
        int slot = match.Moles.ToList().IndexOf(doomed);
        int exit = -1;

        for (int tick = 0; tick < recording.Ticks; tick++)
        {
            if (recording.IsOffDutyAt(tick, slot))
            {
                exit = tick;
                break;
            }
        }

        Assume.That(exit, Is.GreaterThan(0), "the mole has to go out for this to mean anything");

        // A mole must not slide away from its own pratfall, so the frame before it goes out
        // holds its position rather than blending toward wherever the corpse was parked.
        Fix64 justBefore = MatchSettings.TickDuration * Fix64.Ratio((exit * 2) - 1, 2);

        Assert.That(
            recording.PositionAt(justBefore, slot),
            Is.EqualTo(recording.PositionOf(exit - 1, slot)));
    }

    // ---- When the round stops being worth watching -----------------------------------

    /// <summary>
    /// A round where nobody does anything settles at once rather than at tick two hundred
    /// and forty.
    /// </summary>
    /// <remarks>
    /// The round is always the full eight seconds, because everybody plans against the same clock
    /// and a shorter one for a quiet turn would give the simultaneous plans away. Watching it is a
    /// different question, and this is what lets the client stop watching.
    /// </remarks>
    [Test]
    public void ARoundWhereNothingHappensSettlesEarly()
    {
        MoleMatch match = NewMatch();

        for (int seat = 0; seat < 4; seat++)
        {
            match.SubmitPlan(Plan.Idle(seat, 0));
        }

        RoundRecording recording = match.ResolveRound(record: true).Recording!;

        Assert.That(
            recording.SettledTick,
            Is.LessThan(MatchSettings.TicksPerSecond),
            "sixteen moles standing still kept the round alive");
    }

    /// <summary>
    /// A round with a shell in it stays alive until the shell has gone off.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule, and the one that matters: cutting a replay short is only
    /// safe if the cut lands after everything anybody wanted to see. Measured against the quiet
    /// round rather than against a tick number, because when a clod goes off is the arsenal's
    /// business and this is only claiming that a round with one in it lasts longer than a round
    /// with nothing in it, and still less than the whole eight seconds.
    /// </remarks>
    [Test]
    public void ARoundWithAShellInItStaysAliveUntilItGoesOff()
    {
        MoleMatch match = NewMatch();

        match.SubmitPlan(Shell(0));

        for (int seat = 1; seat < 4; seat++)
        {
            match.SubmitPlan(Plan.Idle(seat, 0));
        }

        RoundRecording recording = match.ResolveRound(record: true).Recording!;

        Assert.Multiple(() =>
        {
            Assert.That(
                recording.SettledTick,
                Is.GreaterThan(MatchSettings.TicksPerSecond),
                "the round was called quiet while a clod was still in the air");
            Assert.That(
                recording.SettledTick, Is.LessThan(recording.Ticks - 1),
                "one shot kept the whole eight seconds busy, so nothing can ever be trimmed");
        });
    }
}
