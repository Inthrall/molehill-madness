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
    /// The line where ground meets air.
    /// </summary>
    /// <remarks>
    /// All that is left of the terrain's own colours. The ground was a flat fill here until the art
    /// arrived, and a shade per material before that, which read as five kinds of stripe and said
    /// nothing a player could act on at a glance. It is textures now, in <see cref="Art"/>, and the
    /// outline is the only part of the picture the palette still has a say in. The materials are
    /// still there in the simulation, where they decide what digging costs; they are still not what
    /// the picture is about.
    /// </remarks>
    /// <remarks>
    /// Pale, which looks like the wrong way round for an art style that outlines everything in dark
    /// brown, and is not. It was dark brown, and once the painted dirt went in it disappeared. The
    /// reason is worth writing down because it is not obvious from looking at either colour.
    ///
    /// The line has to serve two different boundaries. At the skyline it separates dirt from the sky
    /// backdrop, and those two are 4.05:1 apart in contrast on their own, so that boundary is plainly
    /// visible with no line at all. At a cave wall it separates dirt from the deep backdrop, and the
    /// deep backdrop is multiplied by <c>deep_shade</c> to make a hole read as a hole, which lands it
    /// at 2.05:1 from the dirt. That is the weak boundary, and it is the one somebody underground is
    /// trying to read.
    ///
    /// A dark line is the exact inverse of what is wanted: measured against the shipped sheets it is
    /// 6.66:1 against the sky, where nothing was needed, and 1.25:1 against the deep, where
    /// everything was. No single dark value fixes it either, because dirt and the shaded deep sit
    /// within a whisker of each other and any colour close enough to contrast with one is close
    /// enough to vanish against the other.
    ///
    /// This value goes the other way: 3.9:1 against dirt, 8.0:1 against the deep backdrop and 3.0:1
    /// against turf, spending its one weakness at the skyline, which already has 4.05:1 of its own.
    /// </remarks>
    public static readonly Color Edge = new Color(0.91f, 0.87f, 0.75f);

    /// <summary>
    /// The same line, for the layer behind the ground: where the countryside meets the deep soil.
    /// </summary>
    /// <remarks>
    /// <see cref="Edge"/> muted by about a third, which is the whole of the thinking. It has to be
    /// recognisably the same line, because the two boundaries meet wherever a tunnel breaks the
    /// surface and a different colour there would read as two unrelated marks; and it has to sit
    /// back, because everything in that layer is meant to be further away. Depth in a flat picture
    /// is contrast, so a backdrop line at the foreground's strength would bring the backdrop
    /// forward, which is the opposite of what <c>deep_shade</c> spends its whole existence doing.
    ///
    /// Measured off a rendered frame against the two things it actually separates, which is the
    /// method the note above uses and the reason it can be trusted: 3.6:1 against the shaded deep
    /// soil and 2.6:1 against the countryside. Deliberately balanced rather than spent on one side,
    /// because unlike the foreground's line this one has no strong side. <see cref="Edge"/> in the
    /// same places measures 8.3:1 and 1.1:1, which is a line that shouts into a hole and disappears
    /// against the sky.
    /// </remarks>
    public static readonly Color Horizon = new Color(0.60f, 0.57f, 0.49f);

    /// <summary>
    /// A hole somebody has walked their plan through but has not dug yet.
    /// </summary>
    /// <remarks>
    /// Dark rather than in the platoon's colour, because what it is showing is absence of ground and
    /// not a route. Only ever one plan is on screen in any pane, so nothing needs to say whose it is.
    /// </remarks>
    public static readonly Color Planned = new Color(0.10f, 0.06f, 0.04f, 0.55f);


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
