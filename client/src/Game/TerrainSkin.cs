using Godot;

/// <summary>
/// The ground and the sky, in one shaded pass behind everything else on a pane.
/// </summary>
/// <remarks>
/// A child of the view rather than part of its draw, because a canvas shader applies to everything
/// a node draws and the view draws sixteen moles and a HUD as well. Sitting at a negative
/// <see cref="CanvasItem.ZIndex"/> puts it behind its own parent, which is what lets the view carry
/// on drawing the world in one place without any of it being shaded.
///
/// It covers the whole pane and paints the sky as well as the ground, which saves keeping a separate
/// sky draw ordered behind it, and means anything off the edge of the map reads as sky for free.
/// </remarks>
public partial class TerrainSkin : Control
{
    private readonly Stage _stage;
    private readonly ShaderMaterial _shading;

    public TerrainSkin(Stage stage)
    {
        _stage = stage;
        _shading = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/terrain.gdshader"),
        };

        Material = _shading;
        MouseFilter = MouseFilterEnum.Ignore;

        // Behind its own parent, which is where the ground belongs.
        ZIndex = -1;

        // Interpolated on purpose. Everywhere else in this client wants point sampling so the cell
        // grid stays honest; here the whole idea is to read between the cells.
        TextureFilter = TextureFilterEnum.Linear;
    }

    /// <summary>
    /// Fills the pane, told where the map has ended up on it.
    /// </summary>
    /// <param name="onPane">The map's rectangle in the parent's coordinates.</param>
    public void Cover(Rect2 onPane)
    {
        Position = Vector2.Zero;
        Size = GetParentAreaSize();
        _map = onPane;
        QueueRedraw();
    }

    private Rect2 _map;

    public override void _Draw()
    {
        Texture2D field = _stage.Terrain;

        if (Size.X <= 0f || Size.Y <= 0f || _map.Size.X <= 0f || _map.Size.Y <= 0f)
        {
            return;
        }

        _shading.SetShaderParameter("map_origin", _map.Position);
        _shading.SetShaderParameter("map_extent", _map.Size);
        _shading.SetShaderParameter("pane_size", Size);
        _shading.SetShaderParameter(
            "field_texel", new Vector2(1f / field.GetWidth(), 1f / field.GetHeight()));
        _shading.SetShaderParameter("ground_colour", Palette.Ground);
        _shading.SetShaderParameter("edge_colour", Palette.Edge);
        _shading.SetShaderParameter("sky_colour", Palette.Paper);

        // One rect over the whole pane, so the shader gets a fragment everywhere it has something
        // to say, including the parts of the pane the map does not reach.
        DrawTextureRect(field, new Rect2(Vector2.Zero, Size), false);
    }
}
