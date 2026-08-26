using Godot;
using MoleSim.Terrain;

// Godot has a Material of its own, meaning a shader. This file is only ever talking about
// what a cell is made of.
using Material = MoleSim.Terrain.Material;

/// <summary>
/// Draws the terrain, and keeps up with it being chewed apart.
/// </summary>
/// <remarks>
/// One image pixel per terrain cell, uploaded once and then patched in place. The
/// simulation reports the smallest rectangle that has changed since the last frame, so a
/// crater costs an upload the size of the crater rather than the size of the map. That
/// dirty-region path is the part worth proving at this stage: it is what has to hold when
/// four moles are digging at once.
///
/// A texture rather than a generated mesh, deliberately. Marching squares would give
/// smooth edges and belongs with the painted biome in Phase 3, but Phase 2 exists to
/// answer whether the game is any fun, and chunky soil answers that just as well while
/// costing a fraction of the work. The performance-critical half of the problem, knowing
/// what to redraw, is exercised either way.
/// </remarks>
public sealed class TerrainView
{
    private readonly TerrainGrid _grid;
    private readonly Image _image;
    private readonly ImageTexture _texture;

    public TerrainView(TerrainGrid grid)
    {
        _grid = grid;
        _image = Image.CreateEmpty(grid.Width, grid.Height, false, Image.Format.Rgba8);

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                _image.SetPixel(x, y, ColourOf(grid[x, y]));
            }
        }

        _texture = ImageTexture.CreateFromImage(_image);
        grid.TakeDirtyRegion(out _, out _, out _, out _);
    }

    public Texture2D Texture => _texture;

    /// <summary>Patches whatever has changed. Cheap when nothing has.</summary>
    public void Refresh()
    {
        if (!_grid.TakeDirtyRegion(out int minX, out int minY, out int maxX, out int maxY))
        {
            return;
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                _image.SetPixel(x, y, ColourOf(_grid[x, y]));
            }
        }

        _texture.Update(_image);
    }

    /// <summary>The document's palette, so the prototype already looks like the game.</summary>
    private static Color ColourOf(Material material) => material switch
    {
        Material.Air => new Color(0.949f, 0.945f, 0.894f),
        Material.Turf => new Color(0.435f, 0.647f, 0.325f),
        Material.LooseSoil => new Color(0.847f, 0.749f, 0.596f),
        Material.PackedSoil => new Color(0.769f, 0.659f, 0.494f),
        Material.RootMat => new Color(0.545f, 0.451f, 0.333f),
        Material.Bedrock => new Color(0.290f, 0.290f, 0.290f),
        _ => new Color(1f, 0f, 1f),
    };
}
