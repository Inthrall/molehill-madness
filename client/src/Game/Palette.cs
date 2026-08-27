using Godot;
using MoleSim.Terrain;

// Godot has a Material of its own, meaning a shader. Here it only ever means what a cell is
// made of.
using Material = MoleSim.Terrain.Material;

/// <summary>
/// The document's colours, in one place.
/// </summary>
/// <remarks>
/// Four things draw the game now: the terrain texture, the world views, the panels and the
/// touch controls. When each carried its own copy of the palette the sky and the panel ended
/// up the same cream, and the panel was invisible until somebody rendered a frame and looked
/// at it. One list means that cannot happen twice.
/// </remarks>
public static class Palette
{
    // ---- Paper and ink ---------------------------------------------------------------

    public static readonly Color Paper = new Color(0.949f, 0.945f, 0.894f);

    public static readonly Color Ink = new Color(0.18f, 0.14f, 0.10f);

    /// <summary>Panels sit on ink, not paper. The sky is paper, so a paper panel vanishes.</summary>
    public static readonly Color Panel = new Color(0.18f, 0.14f, 0.10f, 0.90f);

    public static readonly Color OnPanel = new Color(0.949f, 0.945f, 0.894f);

    public static readonly Color OnPanelDim = new Color(0.949f, 0.945f, 0.894f, 0.5f);

    // ---- The ground ------------------------------------------------------------------

    public static readonly Color Lava = new Color(0.878f, 0.290f, 0.094f);

    public static readonly Color Crate = new Color(0.55f, 0.38f, 0.18f);

    public static readonly Color Dust = new Color(0.847f, 0.749f, 0.596f);

    public static readonly Color Snout = new Color(0.85f, 0.55f, 0.5f);

    public static Color Of(Material material) => material switch
    {
        Material.Air => Paper,
        Material.Turf => new Color(0.435f, 0.647f, 0.325f),
        Material.LooseSoil => new Color(0.847f, 0.749f, 0.596f),
        Material.PackedSoil => new Color(0.769f, 0.659f, 0.494f),
        Material.RootMat => new Color(0.545f, 0.451f, 0.333f),
        Material.Bedrock => new Color(0.290f, 0.290f, 0.290f),
        _ => new Color(1f, 0f, 1f),
    };

    /// <summary>
    /// Ground, all of it the same colour whatever it is made of.
    /// </summary>
    /// <remarks>
    /// The terrain used to be painted a shade per material, which reads as five kinds of stripe and
    /// says nothing a player can act on at a glance. What matters about a cell is which side of the
    /// edge it is on, so the ground is one flat fill and the edge does the work. The materials are
    /// still there in the simulation, where they decide what digging costs; they are simply not what
    /// the picture is about.
    /// </remarks>
    public static readonly Color Ground = new Color(0.827f, 0.729f, 0.573f);

    /// <summary>The line where ground meets air, which is the only detail the terrain draws.</summary>
    public static readonly Color Edge = new Color(0.34f, 0.27f, 0.19f);

    /// <summary>Nothing at all, so a pane's painted sky shows through the air.</summary>
    public static readonly Color Nothing = new Color(0f, 0f, 0f, 0f);

    // ---- Platoons --------------------------------------------------------------------

    private static readonly Color[] Seats =
    {
        new Color(0.294f, 0.545f, 0.231f),
        new Color(0.780f, 0.353f, 0.157f),
        new Color(0.306f, 0.510f, 0.651f),
        new Color(0.769f, 0.165f, 0.047f),
    };

    public static Color Seat(int seat) =>
        Seats[Mathf.Clamp(seat, 0, Seats.Length - 1)];

    // ---- Feedback --------------------------------------------------------------------

    public static readonly Color Damage = new Color(0.769f, 0.165f, 0.047f);

    public static readonly Color Aiming = new Color(0.780f, 0.353f, 0.157f);

    public static readonly Color Spent = new Color(0.780f, 0.353f, 0.157f);
}
