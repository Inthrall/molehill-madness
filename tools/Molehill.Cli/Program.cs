using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MoleSim;
using MoleSim.Diagnostics;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace Molehill.Cli;

/// <summary>
/// The headless workbench. Iterating on the simulation through the game client is far too
/// slow, so everything that can be exercised without pixels lives here.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Every number this tool prints gets compared against another machine's output, so
        // the formatting must not depend on the machine's locale.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        return command switch
        {
            "selftest" => SelfTest(),
            "dump" => DumpTerrain(args.Length > 1 ? args[1] : "dumps/terrain.bmp"),
            "walk" => WalkRound(args.Length > 1 ? args[1] : "dumps/walk.bmp"),
            "match" => PlayMatch(args.Length > 1 ? args[1] : "dumps/match.bmp"),
            "costs" => PrintCostTable(),
            "help" or "--help" or "-h" => PrintUsage(0),
            _ => PrintUnknown(command),
        };
    }

    /// <summary>
    /// Prints a fingerprint of a scripted run. Two machines that print different
    /// fingerprints do not agree about the rules of the game, and online play between
    /// them is impossible. This is the check that Phase 0 turns on before anything else
    /// gets built, and it wants running on a desktop and a phone.
    /// </summary>
    private static int SelfTest()
    {
        DeterminismFingerprint print = DeterminismProbe.Run();

        Console.WriteLine("Molehill determinism fingerprint");
        Console.WriteLine("--------------------------------");
        Console.WriteLine($"runtime      {Environment.Version}");
        Console.WriteLine($"os           {Environment.OSVersion}");
        Console.WriteLine($"64-bit       {Environment.Is64BitProcess}");
        Console.WriteLine();
        Console.WriteLine($"fix64        {print.Arithmetic:X16}");
        Console.WriteLine($"vec2         {print.DriftX:X16} {print.DriftY:X16}  cell {print.CellX},{print.CellY}");
        Console.WriteLine($"rng          {print.Randomness:X16}");
        Console.WriteLine($"terrain      {print.TerrainRolling:X16}");
        Console.WriteLine($"terrain full {print.TerrainFull:X16}");
        Console.WriteLine();
        Console.WriteLine($"COMBINED     {print.Combined:X16}");
        Console.WriteLine();

        if (!print.TerrainHashesAgree)
        {
            Console.Error.WriteLine("FAIL: the rolling terrain hash has drifted from a full recompute.");
            return 1;
        }

        Console.WriteLine("Rolling and full terrain hashes agree.");
        Console.WriteLine("The COMBINED line is the one to compare against another device.");
        return 0;
    }

    private static int DumpTerrain(string path)
    {
        const int Width = 900;
        const int Height = 480;

        TerrainGrid grid = DeterminismProbe.BuildProbeMap(Width, Height);

        // A few craters and a tunnel, so the dump shows carving as well as strata.
        grid.CarveCircle(200, 150, 34);
        grid.CarveCircle(250, 168, 26);
        grid.CarveCircle(640, 140, 44);

        for (int x = 300; x < 620; x += 4)
        {
            int y = 210 + (int)(40 * Math.Sin(x / 60.0));
            grid.CarveCircle(x, y, 7);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] pixels = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (byte red, byte green, byte blue) = ColourOf(grid[x, y]);
                int offset = ((y * Width) + x) * 3;
                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
            }
        }

        BmpWriter.Write(path, Width, Height, pixels);

        Console.WriteLine($"Wrote {Width}x{Height} terrain to {Path.GetFullPath(path)}");
        Console.WriteLine($"Grid hash {grid.Hash:X16}");
        return 0;
    }

    /// <summary>
    /// Runs one mole through one round along a scripted route and draws where it went.
    /// The numbers in the tests say the movement is right; this says whether it looks it.
    /// </summary>
    private static int WalkRound(string path)
    {
        const int Width = 900;
        const int Height = 420;
        const int SurfaceCell = 120;

        TerrainGrid grid = new TerrainGrid(Width, Height);
        grid.FillRectangle(0, SurfaceCell, Width, 3, Material.Turf);
        grid.FillRectangle(0, SurfaceCell + 3, Width, 40, Material.LooseSoil);
        grid.FillRectangle(0, SurfaceCell + 43, Width, Height - SurfaceCell - 51, Material.PackedSoil);
        grid.FillRectangle(0, Height - 8, Width, 8, Material.Bedrock);

        // A hill to walk over, and a slab of bedrock to be stopped by.
        for (int step = 0; step < 220; step++)
        {
            int rise = step < 110 ? step / 6 : (219 - step) / 6;
            int top = SurfaceCell - rise - 1;
            grid.FillRectangle(240 + step, top, 1, SurfaceCell - top + 3, Material.Turf);
        }

        grid.FillRectangle(700, SurfaceCell + 60, 8, 90, Material.Bedrock);

        Mole mole = new Mole(
            seat: 0,
            index: 0,
            new Vec2(
                WorldScale.ToCentreMetres(40),
                WorldScale.ToMetres(SurfaceCell) - MatchSettings.Radius - WorldScale.CellSize));

        for (int settle = 0; settle < 10; settle++)
        {
            MoleMotion.Step(mole, grid, route: null);
        }

        mole.BeginRound();

        // Over the hill, then dive underground and head for the bedrock slab.
        Vec2[] route =
        {
            new Vec2(WorldScale.ToMetres(300), WorldScale.ToMetres(SurfaceCell - 20)),
            new Vec2(WorldScale.ToMetres(520), WorldScale.ToMetres(SurfaceCell - 4)),
            new Vec2(WorldScale.ToMetres(600), WorldScale.ToMetres(SurfaceCell + 90)),
            new Vec2(WorldScale.ToMetres(880), WorldScale.ToMetres(SurfaceCell + 100)),
        };

        List<Vec2> trail = new List<Vec2>();
        Fix64 startStamina = mole.Stamina;

        for (int tick = 0; tick < MatchSettings.TicksPerRound; tick++)
        {
            MoleMotion.Step(mole, grid, route);
            trail.Add(mole.Position);
        }

        Console.WriteLine("One round, one mole");
        Console.WriteLine("-------------------");
        Console.WriteLine($"waypoints reached  {mole.WaypointIndex} of {route.Length}");
        Console.WriteLine($"stamina spent      {startStamina - mole.Stamina} of {startStamina}");
        Console.WriteLine($"ended at           {mole.Position}");
        Console.WriteLine($"airborne           {mole.IsAirborne}");
        Console.WriteLine($"terrain hash       {grid.Hash:X16}");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] pixels = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (byte red, byte green, byte blue) = ColourOf(grid[x, y]);
                int offset = ((y * Width) + x) * 3;
                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
            }
        }

        // The route the player drew, then the path the mole actually took on top of it.
        foreach (Vec2 waypoint in route)
        {
            Plot(pixels, Width, Height, waypoint, 3, 0x4E, 0x82, 0xA6);
        }

        foreach (Vec2 step in trail)
        {
            Plot(pixels, Width, Height, step, 1, 0xC4, 0x2A, 0x0C);
        }

        BmpWriter.Write(path, Width, Height, pixels);
        Console.WriteLine($"wrote              {Path.GetFullPath(path)}");
        return 0;
    }

    private static void Plot(
        byte[] pixels, int width, int height, Vec2 position, int size, byte red, byte green, byte blue)
    {
        int centreX = WorldScale.ToCell(position.X);
        int centreY = WorldScale.ToCell(position.Y);

        for (int y = centreY - size; y <= centreY + size; y++)
        {
            for (int x = centreX - size; x <= centreX + size; x++)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    continue;
                }

                int offset = ((y * width) + x) * 3;
                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
            }
        }
    }

    /// <summary>
    /// Plays a whole four-player match headlessly and reports it round by round.
    /// </summary>
    /// <remarks>
    /// The plans come from a script derived from the seed. It is not an opponent and never
    /// will be: the game has no AI. It exists so that a complete match can be exercised
    /// thousands of times a minute without a single pixel being drawn, which is the whole
    /// argument for building the simulation before the client.
    /// </remarks>
    private static int PlayMatch(string path)
    {
        const int Width = 1200;
        const int Height = 480;
        const ulong Seed = 20260826UL;

        MoleMatch match = MoleMatch.Create(playerCount: 4, Seed, Width, Height);
        MatchRng script = new MatchRng(Seed ^ 0xA5A5A5A5UL);

        Console.WriteLine("Molehill headless match");
        Console.WriteLine("-----------------------");
        Console.WriteLine($"seed {Seed}   map {Width}x{Height} cells   4 players, 16 moles");
        Console.WriteLine();
        Console.WriteLine("rnd  standing      dmg  KOs  wind   lava        hash");

        RoundResult result;
        int round = 0;

        do
        {
            round++;

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

                match.SubmitPlan(new Plan(
                    seat,
                    actor.Index,
                    script.NextBool() ? WeaponId.ClodLobber : WeaponId.BeetleLauncher,
                    route,
                    new[]
                    {
                        PlanAction.Hop(script.NextInt(10, 40)),
                        PlanAction.Fire(
                            script.NextInt(60, 200),
                            new Vec2(
                                Fix64.FromInt(script.NextInt(-10, 11)),
                                Fix64.FromInt(-script.NextInt(2, 10))),
                            (byte)script.NextInt(90, 256)),
                    }));
            }

            result = match.ResolveRound();

            string standing = string.Empty;
            for (int seat = 0; seat < match.PlayerCount; seat++)
            {
                int alive = 0;
                foreach (Mole mole in match.Moles)
                {
                    if (mole.Seat == seat && !mole.IsOffDuty)
                    {
                        alive++;
                    }
                }

                standing += alive.ToString(CultureInfo.InvariantCulture);
            }

            string lava = match.LavaLine == Fix64.MaxValue ? "  -  " : $"{match.LavaLine,5}";

            Console.WriteLine(
                $"{result.Round,3}  {standing,-12} {result.TotalDamage,4} {result.Knockouts.Count,4}"
                + $"  {match.Wind,5}  {lava}  {match.StateHash():X16}");
        }
        while (!result.MatchOver && round < 40);

        Console.WriteLine();

        if (result.MatchOver)
        {
            Console.WriteLine(result.WinningSeat >= 0
                ? $"Seat {result.WinningSeat} takes the flowerbed."
                : "Everybody went out together. Glorious.");
        }
        else
        {
            Console.WriteLine("Called on round count with the match still going.");
        }

        Console.WriteLine($"final hash {match.StateHash():X16}");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] pixels = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (byte red, byte green, byte blue) = ColourOf(match.Terrain[x, y]);
                int offset = ((y * Width) + x) * 3;
                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
            }
        }

        // The lava, and everybody still standing.
        if (match.LavaLine != Fix64.MaxValue)
        {
            int lavaCell = WorldScale.ToCell(match.LavaLine);

            for (int y = Math.Max(0, lavaCell); y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int offset = ((y * Width) + x) * 3;
                    pixels[offset] = 0xE0;
                    pixels[offset + 1] = 0x4A;
                    pixels[offset + 2] = 0x18;
                }
            }
        }

        byte[][] seatColours =
        {
            new byte[] { 0x4B, 0x8B, 0x3B },
            new byte[] { 0xC7, 0x5A, 0x28 },
            new byte[] { 0x4E, 0x82, 0xA6 },
            new byte[] { 0xC4, 0x2A, 0x0C },
        };

        foreach (Mole mole in match.Moles)
        {
            if (mole.IsOffDuty)
            {
                continue;
            }

            byte[] colour = seatColours[mole.Seat];
            Plot(pixels, Width, Height, mole.Position, 5, colour[0], colour[1], colour[2]);
        }

        BmpWriter.Write(path, Width, Height, pixels);
        Console.WriteLine($"wrote      {Path.GetFullPath(path)}");
        return 0;
    }

    private static int PrintCostTable()
    {
        Console.WriteLine("Movement economy");
        Console.WriteLine("----------------");
        Console.WriteLine("material        per metre   reach on 100 stamina");

        foreach (Material material in Enum.GetValues<Material>())
        {
            Fix64 cost = MaterialTable.CostPerMetre(material);
            string reach = MaterialTable.IsPassable(material)
                ? (Fix64.FromInt(100) / cost).ToString() + " m"
                : "impassable";

            Console.WriteLine($"{material,-14}  {cost,8}   {reach}");
        }

        Console.WriteLine();
        Console.WriteLine("An 8-second turn at 5 m/s covers at most 40 m, so on the surface");
        Console.WriteLine("the clock binds first and underground the stamina does.");
        return 0;
    }

    private static (byte Red, byte Green, byte Blue) ColourOf(Material material) => material switch
    {
        Material.Air => (0xF2, 0xF1, 0xE4),
        Material.Turf => (0x6F, 0xA5, 0x53),
        Material.LooseSoil => (0xD8, 0xBF, 0x98),
        Material.PackedSoil => (0xC4, 0xA8, 0x7E),
        Material.RootMat => (0x8B, 0x73, 0x55),
        Material.Bedrock => (0x4A, 0x4A, 0x4A),
        _ => (0xFF, 0x00, 0xFF),
    };

    private static int PrintUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        return PrintUsage(2);
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine("molehill <command>");
        Console.WriteLine();
        Console.WriteLine("  selftest        print a determinism fingerprint for this machine");
        Console.WriteLine("  dump [path]     write a terrain cross-section as a BMP");
        Console.WriteLine("  walk [path]     run one mole through one round and draw its path");
        Console.WriteLine("  match [path]    play a whole four-player match headlessly");
        Console.WriteLine("  costs           print the movement cost table and reaches");
        return exitCode;
    }
}
