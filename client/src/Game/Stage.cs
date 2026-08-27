using Godot;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// Everything the views need to draw a frame, written once by the scene and read by all of
/// them.
/// </summary>
/// <remarks>
/// With up to four views on screen the alternative is passing the same dozen values into each
/// one every frame, or letting each view reach back into the scene and pull whatever it likes.
/// One shared context that the scene fills and the views only read is the honest middle: the
/// contract between them is this file, and a view that needs something new has to ask for it
/// here rather than helping itself.
///
/// Nothing here is authoritative. Every field is either the simulation itself or something
/// derived from a finished recording, so a view cannot change the match by drawing it.
/// </remarks>
public sealed class Stage
{
    public Stage(MoleMatch match, Texture2D terrain, int mapWidthCells, int mapHeightCells)
    {
        Match = match;
        Terrain = terrain;
        MapWidthCells = mapWidthCells;
        MapHeightCells = mapHeightCells;
        Planners = System.Array.Empty<SeatPlanner>();
        ExitTick = System.Array.Empty<int>();
        HitTick = System.Array.Empty<int>();

        // Explicitly, because a default Climax is tick zero of mole zero rather than nothing.
        Climax = Climax.None;
    }

    public MoleMatch Match { get; }

    /// <summary>The map as the viewer has seen it so far, which lags the real one during a replay.</summary>
    public Texture2D Terrain { get; }

    public int MapWidthCells { get; }

    public int MapHeightCells { get; }

    public SeatPlanner[] Planners { get; set; }

    /// <summary>Whether the plans should be drawn, which is to say whether anybody is still laying one.</summary>
    public bool Planning { get; set; }

    public RoundRecording? Recording { get; set; }

    public RoundResult? Result { get; set; }

    /// <summary>Which tick of the replay is on screen.</summary>
    public int Tick { get; set; }

    /// <summary>The same moment as a time, for drawing between ticks.</summary>
    public Fix64 Seconds { get; set; }

    /// <summary>When each mole went off duty this round, or -1. Indexed by mole slot.</summary>
    public int[] ExitTick { get; set; }

    /// <summary>When each hit landed. Indexed into the round's hit list.</summary>
    public int[] HitTick { get; set; }

    /// <summary>The moment of this round the replay slows down and pushes in on.</summary>
    public Climax Climax { get; set; }

    /// <summary>How long a damage number lives, at thirty ticks a second.</summary>
    public const int DamageNumberTicks = 45;

    /// <summary>
    /// The pratfall's running time, in ticks. A second and a half, which is what the longer gags
    /// need: a helmet has to spin for an unreasonable length of time before it clangs flat.
    /// </summary>
    public const int ExitTicks = 45;
}
