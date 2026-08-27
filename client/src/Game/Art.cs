using Godot;

/// <summary>
/// The imported art, in one place.
/// </summary>
/// <remarks>
/// Everything on this screen used to be drawn from primitives, and most of it still is: the HUD,
/// the weapons, the moles and the gauges all come out of <see cref="Glyphs"/>. The ground is the
/// first thing to arrive as pictures, because it is the one part of the screen a shape language
/// cannot do much for. A flat fill with an outline says which side of an edge a cell is on, which
/// is all a player needs to act, and says nothing at all about being underground.
///
/// Loaded once and held, because <c>GD.Load</c> on a missing resource returns null and prints one
/// line. That is the failure this project has been bitten by twice: a shader that quietly fell
/// back to a plain draw, and garden clutter that quietly ended up in the margins. Both looked
/// almost right. Asking for the textures once, up front, and saying so loudly when they are not
/// there turns a render that looks odd into a message that says what is wrong.
/// </remarks>
public static class Art
{
    private const string Directory = "res://art/";

    /// <summary>
    /// How many pixels of a decor sprite make a metre.
    /// </summary>
    /// <remarks>
    /// One number for every sprite on both decor sheets, rather than a size per sprite. Every cell
    /// of both sheets was scaled by the same factor on the way in, so their sizes are still in
    /// proportion to one another, and a molehill is shorter than a tuft of grass because it was
    /// drawn shorter. A hundred and fifty puts a full-height sheet cell at about one and a
    /// quarter metres, which is a little over a mole and a half.
    /// </remarks>
    public const float DecorPixelsPerMetre = 150f;

    /// <summary>
    /// Where the surface backdrop's own ground begins, as a fraction of its height.
    /// </summary>
    /// <remarks>
    /// The panorama is sky and hills over a band of near ground, with a bright line between them.
    /// That line is the horizon, and the client's job is to put it where the map's own surface is,
    /// or the backdrop's distant fields appear above the grass the moles are standing on.
    ///
    /// Measured rather than guessed: <c>import-art.ps1</c> prints this fraction every time it runs,
    /// and if new art moves the horizon the number it prints is the number that belongs here.
    /// </remarks>
    public const float SurfaceHorizon = 0.8194f;

    private static Texture2D? _dirt;
    private static Texture2D? _deep;
    private static Texture2D? _surface;
    private static Texture2D[]? _grass;
    private static Texture2D[]? _things;

    /// <summary>The destructible ground, tiling. This is what a solid cell is made of.</summary>
    public static Texture2D Dirt => _dirt ??= Load("terrain-dirt.png");

    /// <summary>What is behind the ground below the surface, tiling. Seen through a tunnel.</summary>
    public static Texture2D Deep => _deep ??= Load("terrain-deep.png");

    /// <summary>What is behind the ground above the surface: countryside, tiling sideways.</summary>
    public static Texture2D Surface => _surface ??= Load("backdrop-surface.png");

    /// <summary>Tufts of grass. Interchangeable, so the garden picks one by index.</summary>
    public static Texture2D[] Grass => _grass ??= new[]
    {
        Load("decor/grass-0.png"),
        Load("decor/grass-1.png"),
        Load("decor/grass-2.png"),
        Load("decor/grass-3.png"),
        Load("decor/grass-4.png"),
        Load("decor/grass-5.png"),
        Load("decor/grass-6.png"),
        Load("decor/grass-7.png"),
    };

    /// <summary>The rest of the garden dressing: spoil heaps, flowers, a stone, a worm.</summary>
    public static Texture2D[] Things => _things ??= new[]
    {
        Load("decor/molehill-small.png"),
        Load("decor/molehill.png"),
        Load("decor/flowers.png"),
        Load("decor/flower.png"),
        Load("decor/dandelion.png"),
        Load("decor/snowdrops.png"),
        Load("decor/stone.png"),
        Load("decor/worm.png"),
    };

    private static Texture2D Load(string name)
    {
        Texture2D? texture = GD.Load<Texture2D>(Directory + name);

        if (texture is null)
        {
            // Said out loud, because the alternative is a backdrop that draws as nothing and a
            // frame that looks like a layout fault rather than a missing file.
            GD.PushError(
                $"No art at {Directory}{name}. Run tools/scripts/import-art.ps1, then open the "
                + "project in the editor once so Godot imports what it wrote.");

            // Sized rather than empty, so a missing file shows up as an obvious grey square
            // instead of dividing something by a width of zero three calls later.
            return new PlaceholderTexture2D { Size = new Vector2(64f, 64f) };
        }

        return texture;
    }
}
