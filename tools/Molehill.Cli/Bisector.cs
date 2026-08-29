using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace Molehill.Cli;

/// <summary>
/// Finds the first tick on which two machines stopped agreeing, and says what about.
/// </summary>
/// <remarks>
/// The plan asks for this in Phase 1 and calls determinism debugging without it "despair". The
/// reason is arithmetic: a round is atomic from the outside, so the finest thing anything could
/// compare was a whole match hash, and a mismatch meant somewhere in sixteen rounds of two hundred
/// and forty ticks with sixteen moles in each. That is not a bug report, it is a shrug.
///
/// Two commands, because the two halves happen on different machines and cannot be one command.
/// <c>trace</c> plays a fixed scripted match and writes a line per tick. <c>bisect</c> takes two
/// such files, finds the first line whose hash differs, and then reads the fields on that line to
/// say which mole and which quantity went first.
///
/// A trace is deliberately fat. Hashes alone would find the tick and leave the actual question
/// unanswered, and the answer is nearly always a single field of a single mole: this position and
/// not that one, a stamina a raw unit apart. So every field that feeds the state hash is written
/// out, in raw fixed-point units rather than as decimals, because a decimal is a rendering of the
/// number and what has diverged is the number.
///
/// The scripted match has to be identical on both machines and must not read a clock, a file or an
/// argument, which is why it is built here rather than taken from the corpus files: a corpus replay
/// would make "the two machines read the same bytes" part of what is being tested, and that is a
/// different question from whether they compute the same answers.
///
/// It fires one weapon, and that is a real limit rather than laziness. Only the Clod Lobber has
/// infinite ammo, so it is the only thing a script can fire every round without becoming illegal
/// part way through, and a tool that throws on round three is worse than no tool at all on precisely
/// the machine somebody is trying to debug. What that leaves uncovered is the seismic and drilling
/// paths and both crate rarities: a divergence in one of those would show up as a disagreeing
/// fingerprint and then hide from this trace, and the answer then is a second scripted match rather
/// than a cleverer one.
/// </remarks>
internal static class Bisector
{
    /// <summary>One tick, as it will be written and read back.</summary>
    private readonly struct Snapshot
    {
        public Snapshot(int round, int tick, ulong hash, ulong terrain, long[] fields)
        {
            Round = round;
            Tick = tick;
            Hash = hash;
            Terrain = terrain;
            Fields = fields;
        }

        public int Round { get; }

        public int Tick { get; }

        public ulong Hash { get; }

        public ulong Terrain { get; }

        /// <summary>Every mole's numbers, flattened, in the order <see cref="Names"/> gives.</summary>
        public long[] Fields { get; }
    }

    /// <summary>What each of a mole's numbers is called, for naming the one that differs.</summary>
    private static readonly string[] Names =
    {
        "position.x", "position.y", "velocity.x", "velocity.y",
        "pluck", "stamina", "lava-strikes", "off-duty", "airborne", "waypoint",
    };

    private const int Rounds = 8;

    // ---- Tracing --------------------------------------------------------------------

    /// <summary>Plays the scripted match and writes a line per tick.</summary>
    public static int Trace(string path)
    {
        Writer writer = new Writer();
        Play(writer);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, writer.Text.ToString());

        Console.WriteLine($"runtime   {Environment.Version}");
        Console.WriteLine($"os        {Environment.OSVersion}");
        Console.WriteLine($"arch      {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        Console.WriteLine();
        Console.WriteLine($"Wrote {writer.Lines} ticks to {path}");
        Console.WriteLine("Run the same command on the other machine, then:");
        Console.WriteLine($"  molehill bisect {path} theirs.tsv");

        return 0;
    }

    private sealed class Writer : MoleMatch.ITickWatcher
    {
        public StringBuilder Text { get; } = new StringBuilder();

        public int Lines { get; private set; }

        public void Ticked(int round, int tick, MoleMatch match)
        {
            Text.Append(round.ToString(CultureInfo.InvariantCulture));
            Text.Append('\t');
            Text.Append(tick.ToString(CultureInfo.InvariantCulture));
            Text.Append('\t');
            Text.Append(match.StateHash().ToString("X16", CultureInfo.InvariantCulture));
            Text.Append('\t');
            Text.Append(match.Terrain.Hash.ToString("X16", CultureInfo.InvariantCulture));

            foreach (Mole mole in match.Moles)
            {
                foreach (long field in Fields(mole))
                {
                    Text.Append('\t');
                    Text.Append(field.ToString(CultureInfo.InvariantCulture));
                }
            }

            Text.Append('\n');
            Lines++;
        }
    }

    private static long[] Fields(Mole mole) => new[]
    {
        mole.Position.X.Raw,
        mole.Position.Y.Raw,
        mole.Velocity.X.Raw,
        mole.Velocity.Y.Raw,
        mole.Pluck,
        mole.Stamina.Raw,
        mole.LavaStrikes,
        mole.IsOffDuty ? 1L : 0L,
        mole.IsAirborne ? 1L : 0L,
        mole.WaypointIndex,
    };

    /// <summary>
    /// A fixed match, scripted the same way everywhere.
    /// </summary>
    /// <remarks>
    /// Every platoon fires at a fixed angle and power on a fixed tick and walks a fixed way, so the
    /// only thing that can differ between two runs is the arithmetic. Deliberately long enough to
    /// reach the lava, because the lava is a whole system that only starts at round eight and would
    /// otherwise never be compared at all.
    /// </remarks>
    private static void Play(MoleMatch.ITickWatcher watching)
    {
        TerrainGrid ground = MapMaker.Field(1000, 480, 20260830UL);
        MoleMatch match = MoleMatch.Create(ground, 4, 20260830UL);

        for (int round = 0; round < Rounds; round++)
        {
            for (int seat = 0; seat < match.PlayerCount; seat++)
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

                int lean = seat % 2 == 0 ? 1 : -1;

                match.SubmitPlan(new Plan(
                    seat,
                    actor.Index,
                    // The Clod Lobber for everybody, because it is the only weapon with infinite
                    // ammo and the script has to stay legal for the whole match. Alternating with
                    // the Beetle Launcher threw InvalidPlanException on round three when seat one
                    // ran out, which would have made the tool unusable on exactly the machine
                    // somebody was trying to debug.
                    WeaponId.ClodLobber,
                    new[]
                    {
                        // A route point is a cell, which is also what keeps this reproducible: no
                        // rounding of a metre happens on one machine and not the other.
                        new RoutePoint(
                            WorldScale.ToCell(actor.Position.X) + (6 * WorldScale.CellsPerMetre * lean),
                            WorldScale.ToCell(actor.Position.Y)),
                    },
                    new[]
                    {
                        PlanAction.Fire(
                            tick: 40 + (seat * 5),
                            aim: new Vec2(Fix64.FromInt(3 * lean), Fix64.FromInt(-2)),
                            power: (byte)(160 + (seat * 20))),
                    }));
            }

            if (match.ResolveRound(record: false, watching).MatchOver)
            {
                break;
            }
        }
    }

    // ---- Bisecting ------------------------------------------------------------------

    /// <summary>Compares two traces and reports the first tick they disagree on.</summary>
    public static int Bisect(string minePath, string theirsPath)
    {
        if (!File.Exists(minePath) || !File.Exists(theirsPath))
        {
            Console.Error.WriteLine("Both traces have to exist. Run 'trace' on each machine first.");

            return 2;
        }

        List<Snapshot> mine = Read(minePath);
        List<Snapshot> theirs = Read(theirsPath);

        Console.WriteLine($"mine    {mine.Count} ticks from {minePath}");
        Console.WriteLine($"theirs  {theirs.Count} ticks from {theirsPath}");
        Console.WriteLine();

        int shared = Math.Min(mine.Count, theirs.Count);

        for (int index = 0; index < shared; index++)
        {
            if (mine[index].Hash == theirs[index].Hash)
            {
                continue;
            }

            Report(mine[index], theirs[index], index);

            return 1;
        }

        if (mine.Count != theirs.Count)
        {
            Console.WriteLine(
                $"Agreed for all {shared} shared ticks, then one run stopped early. A match that "
                + "ends on a different round has diverged in its win condition rather than its "
                + "arithmetic.");

            return 1;
        }

        Console.WriteLine($"Identical for all {shared} ticks. These two machines agree.");

        return 0;
    }

    /// <summary>Says what diverged, at the finest grain the trace holds.</summary>
    private static void Report(Snapshot mine, Snapshot theirs, int index)
    {
        Console.WriteLine(
            $"FIRST DIVERGENCE at round {mine.Round}, tick {mine.Tick} (line {index + 1})");
        Console.WriteLine();
        Console.WriteLine($"  state hash   mine {mine.Hash:X16}   theirs {theirs.Hash:X16}");
        Console.WriteLine(
            $"  terrain      mine {mine.Terrain:X16}   theirs {theirs.Terrain:X16}"
            + (mine.Terrain == theirs.Terrain ? "   (agree)" : "   <- differs"));
        Console.WriteLine();

        int fields = Math.Min(mine.Fields.Length, theirs.Fields.Length);
        int found = 0;

        for (int field = 0; field < fields; field++)
        {
            if (mine.Fields[field] == theirs.Fields[field])
            {
                continue;
            }

            int slot = field / Names.Length;
            string name = Names[field % Names.Length];
            long a = mine.Fields[field];
            long b = theirs.Fields[field];

            Console.WriteLine(
                $"  mole {slot,2}  {name,-13} mine {a,14}   theirs {b,14}   apart {a - b}");

            found++;
        }

        if (found == 0)
        {
            Console.WriteLine(
                "  No mole field differs, so what diverged is in the terrain or the holdings. The "
                + "terrain line above says which.");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Numbers are raw fixed-point units, so 'apart 1' is one sixty-five-thousandth of a "
            + "metre and still fatal: it is a different number, and every tick after this one "
            + "computes from it.");

        if (mine.Tick == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "Diverged on the first tick of a round, so suspect what happens before a tick "
                + "rather than the movement solver: the plan decode, the round setup, or the "
                + "stamina scale.");
        }
    }

    private static List<Snapshot> Read(string path)
    {
        List<Snapshot> read = new List<Snapshot>();

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split('\t');

            if (parts.Length < 4)
            {
                continue;
            }

            long[] fields = new long[parts.Length - 4];

            for (int field = 0; field < fields.Length; field++)
            {
                fields[field] = long.Parse(parts[field + 4], CultureInfo.InvariantCulture);
            }

            read.Add(new Snapshot(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                ulong.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ulong.Parse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                fields));
        }

        return read;
    }
}
