using Godot;
using MoleSim.Terrain;

/// <summary>
/// Keeps a picture of the terrain up to date as it gets chewed apart.
/// </summary>
/// <remarks>
/// One texel per terrain cell, solid or not, uploaded once and then patched in place. The
/// simulation reports the smallest rectangle that has changed since the last frame, so a crater
/// costs an upload the size of the crater rather than the size of the map. That dirty-region path
/// is the part worth proving at this stage: it is what has to hold when four moles are digging at
/// once.
///
/// This is a field rather than a picture, and everything about how the ground looks lives in
/// <c>terrain.gdshader</c>: the flat fill, the outline, and the smoothing that turns a grid of cells
/// into a curve. That is a better division than it might seem. Deciding here what colour a cell
/// should be meant working out whether it was on an edge, and being on an edge is a property of a
/// cell's neighbours rather than of the cell, so every change had to repaint a wider rectangle than
/// the one that actually changed. The shader samples whatever is there when it draws, so none of
/// that bookkeeping exists any more.
/// </remarks>
public sealed class TerrainView
{
    private readonly TerrainGrid _grid;
    private readonly Image _image;
    private readonly ImageTexture _texture;

    public TerrainView(TerrainGrid grid)
    {
        _grid = grid;

        // One byte a cell. The shader wants a scalar to interpolate, not a colour to blend.
        _image = Image.CreateEmpty(grid.Width, grid.Height, false, Image.Format.L8);

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                _image.SetPixel(x, y, Field(x, y));
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
                _image.SetPixel(x, y, Field(x, y));
            }
        }

        _texture.Update(_image);
    }

    private Color Field(int x, int y) =>
        MaterialTable.IsSolid(_grid[x, y]) ? Colors.White : Colors.Black;
}
