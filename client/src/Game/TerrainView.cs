using Godot;
using MoleSim.Terrain;

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
///
/// The ground is one flat colour with an outline where it meets air, and no other detail. It used
/// to be painted a shade per material, which on a map that is two thirds underground reads as five
/// kinds of stripe and tells a player nothing they can act on. What matters about a cell is which
/// side of an edge it is on. The materials still decide what digging costs; they are simply not
/// what the picture is about.
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

        Repaint(0, 0, grid.Width - 1, grid.Height - 1);

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

        // One cell wider than the change on every side. A cell turning to air changes how its
        // neighbours are drawn, because a neighbour that was buried is now on the edge, so
        // repainting only the reported rectangle leaves a crater with no outline down its sides.
        Repaint(minX - 1, minY - 1, maxX + 1, maxY + 1);
        _texture.Update(_image);
    }

    private void Repaint(int fromX, int fromY, int toX, int toY)
    {
        int left = fromX < 0 ? 0 : fromX;
        int top = fromY < 0 ? 0 : fromY;
        int right = toX >= _grid.Width ? _grid.Width - 1 : toX;
        int bottom = toY >= _grid.Height ? _grid.Height - 1 : toY;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                _image.SetPixel(x, y, Shade(x, y));
            }
        }
    }

    private Color Shade(int x, int y)
    {
        if (!MaterialTable.IsSolid(_grid[x, y]))
        {
            return Palette.Nothing;
        }

        return TouchesAir(x, y) ? Palette.Edge : Palette.Ground;
    }

    /// <summary>
    /// Whether a solid cell has air directly beside it, above it or below it.
    /// </summary>
    /// <remarks>
    /// Four neighbours rather than eight. Counting the diagonals thickens the outline at every
    /// corner, which on ground this chunky turns a crater rim into a smear.
    ///
    /// Off the edge of the map counts as solid, so the world's own borders are not outlined. The
    /// outline is meant to say "the ground stops here", and the ground does not stop at the border,
    /// it is simply where the map runs out.
    /// </remarks>
    private bool TouchesAir(int x, int y) =>
        IsAir(x - 1, y) || IsAir(x + 1, y) || IsAir(x, y - 1) || IsAir(x, y + 1);

    private bool IsAir(int x, int y)
    {
        if (x < 0 || x >= _grid.Width || y < 0 || y >= _grid.Height)
        {
            return false;
        }

        return !MaterialTable.IsSolid(_grid[x, y]);
    }
}
