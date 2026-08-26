using System;
using System.Globalization;
using System.IO;
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
        Console.WriteLine("Molehill determinism fingerprint");
        Console.WriteLine("--------------------------------");
        Console.WriteLine($"runtime      {Environment.Version}");
        Console.WriteLine($"os           {Environment.OSVersion}");
        Console.WriteLine($"64-bit       {Environment.Is64BitProcess}");
        Console.WriteLine();

        // Arithmetic: exercises the wide intermediates in multiply, divide and root.
        Fix64 accumulator = Fix64.One;
        for (int step = 1; step <= 500; step++)
        {
            Fix64 value = Fix64.FromInt(step);
            accumulator += Fix64.Sqrt(value) * Fix64.Ratio(7, 13);
            accumulator = accumulator / Fix64.Ratio(1001, 1000);
            accumulator += Fix64.Hypot(value, Fix64.FromInt(step * 3));
        }

        Console.WriteLine($"fix64        {accumulator.Raw:X16}  ({accumulator})");

        // Randomness: the exact draw sequence a match depends on.
        MatchRng rng = new MatchRng(20260826UL);
        ulong mixed = 0;
        for (int draw = 0; draw < 10_000; draw++)
        {
            mixed ^= rng.NextUInt64();
            mixed = (mixed << 1) | (mixed >> 63);
        }

        Console.WriteLine($"rng          {mixed:X16}");

        // Terrain: a scripted sequence of carves, then both hashes. They must match each
        // other as well as the other machine.
        TerrainGrid grid = BuildTestMap(600, 320);
        MatchRng carveRng = new MatchRng(1337UL);
        for (int carve = 0; carve < 2000; carve++)
        {
            grid.CarveCircle(
                carveRng.NextInt(grid.Width),
                carveRng.NextInt(grid.Height),
                carveRng.NextInt(2, 14));
        }

        Console.WriteLine($"terrain      {grid.Hash:X16}");
        Console.WriteLine($"terrain full {grid.ComputeFullHash():X16}");
        Console.WriteLine();

        if (grid.Hash != grid.ComputeFullHash())
        {
            Console.Error.WriteLine("FAIL: the rolling terrain hash has drifted from a full recompute.");
            return 1;
        }

        Console.WriteLine("Rolling and full terrain hashes agree.");
        Console.WriteLine("Compare every line above against the other platform.");
        return 0;
    }

    private static int DumpTerrain(string path)
    {
        const int Width = 900;
        const int Height = 480;

        TerrainGrid grid = BuildTestMap(Width, Height);

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

    /// <summary>
    /// A miniature of the shipping cross-section: turf line, strata beneath, bedrock along
    /// the floor. Used by both the fingerprint and the dump so they exercise the same map.
    /// </summary>
    private static TerrainGrid BuildTestMap(int width, int height)
    {
        TerrainGrid grid = new TerrainGrid(width, height);
        int surface = height / 4;

        // A rolling surface, drawn with whole-number arithmetic so the shape is identical
        // everywhere. The client's real maps come from baked art instead.
        for (int x = 0; x < width; x++)
        {
            int rise = ((x * 7 / 40) % 24) - 12;
            int top = surface + (rise / 3);

            grid.FillRectangle(x, top, 1, 3, Material.Turf);
            grid.FillRectangle(x, top + 3, 1, height / 8, Material.LooseSoil);
            grid.FillRectangle(x, top + 3 + (height / 8), 1, height / 3, Material.PackedSoil);
        }

        grid.FillRectangle(0, height - (height / 6), width, height / 12, Material.RootMat);
        grid.FillRectangle(0, height - (height / 12), width, height / 12, Material.Bedrock);

        return grid;
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
        Console.WriteLine("  costs           print the movement cost table and reaches");
        return exitCode;
    }
}
