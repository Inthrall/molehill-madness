using System;
using System.Globalization;
using System.IO;
using MoleSim;
using MoleSim.Diagnostics;
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
        Console.WriteLine("  costs           print the movement cost table and reaches");
        return exitCode;
    }
}
