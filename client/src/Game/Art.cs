using System.Collections.Generic;
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

    /// <summary>
    /// How many pixels of a mole sprite make a metre.
    /// </summary>
    /// <remarks>
    /// One number for every pose, because the importer scaled each sheet so a mole comes out the
    /// same size whichever sheet its pose came from. Two hundred puts the standing mole at about
    /// four fifths of a metre across, which is a little wider than the three quarters of a metre
    /// its collision circle is, because the artwork's claws stick out past its body.
    /// </remarks>
    public const float MolePixelsPerMetre = 200f;

    /// <summary>
    /// The mole's poses, and how many frames each of them has.
    /// </summary>
    /// <remarks>
    /// The counts live here rather than in the file names because they are a fact about the
    /// artwork, and a name that has to be parsed to be used is a name that will be got wrong. They
    /// have to agree with the manifest in <c>import-art.ps1</c>: a count too low silently plays a
    /// shorter animation, and a count too high draws a sliver of the next frame.
    ///
    /// Airborne is eight rather than four because the artist drew the mirror as well, so a tumbling
    /// mole facing left uses the second four rather than a flipped copy of the first four.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, int> MoleFrames =
        new Dictionary<string, int>
        {
            { "stand", 1 },
            { "ko", 1 },
            { "aim", 5 },
            { "airborne", 8 },
            { "dig", 6 },
            { "hit", 3 },
            { "claws", 6 },
            { "rooted", 8 },
        };

    /// <summary>The eight exits, by the name <see cref="MoleSim.Match.KnockoutExit"/> knows.</summary>
    public static readonly IReadOnlyDictionary<string, int> ExitFrames =
        new Dictionary<string, int>
        {
            { "poof", 6 },
            { "stretcher", 6 },
            { "birds", 6 },
            { "balloon", 8 },
            { "hole", 6 },
            { "helmet", 6 },
            { "sink", 6 },
            { "steam", 7 },
            { "launch", 6 },
        };

    /// <summary>Effects that are not attached to a mole.</summary>
    public static readonly IReadOnlyDictionary<string, int> EffectFrames =
        new Dictionary<string, int>
        {
            { "blast", 8 },
            { "ring", 8 },
            { "geyser", 5 },
            { "drill", 6 },
        };

    /// <summary>Platoon colours in seat order, which is how the importer named the files.</summary>
    private static readonly string[] Seats = { "green", "orange", "blue", "red" };

    private static readonly Dictionary<string, Strip> Strips = new Dictionary<string, Strip>();

    /// <summary>A mole pose in a platoon's colour.</summary>
    public static Strip Mole(int seat, string pose) =>
        Held($"mole/{Seats[Mathf.Clamp(seat, 0, Seats.Length - 1)]}-{pose}.png", MoleFrames[pose]);

    /// <summary>A knockout exit in a platoon's colour.</summary>
    public static Strip Exit(int seat, string exit) =>
        Held($"exit/{Seats[Mathf.Clamp(seat, 0, Seats.Length - 1)]}-{exit}.png", ExitFrames[exit]);

    public static Strip Effect(string effect) =>
        Held($"effect/{effect}.png", EffectFrames[effect]);

    /// <summary>A thing in the world: a projectile, a trap, a crate.</summary>
    public static Texture2D Object(string name) => Held($"object/{name}.png", 1).Art;

    /// <summary>
    /// A weapon's glyph, by the id the simulation already has.
    /// </summary>
    /// <remarks>
    /// Numbered rather than named because the artist drew all fifteen in
    /// <see cref="MoleSim.Match.WeaponId"/> order across three sheets, so there is nothing to map.
    /// White, so it can be tinted to a platoon's colour on the way out, which is the property the
    /// glyphs drawn from primitives had and the reason it was safe to replace them.
    /// </remarks>
    public static Texture2D Weapon(MoleSim.Match.WeaponId weapon) =>
        Held($"glyph/weapon-{(int)weapon:00}.png", 1).Art;

    /// <summary>An interface glyph, white, to be tinted.</summary>
    public static Texture2D Glyph(string name) => Held($"glyph/{name}.png", 1).Art;

    private static Strip Held(string name, int frames)
    {
        if (Strips.TryGetValue(name, out Strip? held))
        {
            return held;
        }

        Strip made = new Strip(Load(name), frames);
        Strips[name] = made;

        return made;
    }

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
