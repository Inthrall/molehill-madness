using Godot;

/// <summary>
/// The ground and everything behind it, in one shaded pass behind everything else on a pane.
/// </summary>
/// <remarks>
/// A child of the view rather than part of its draw, because a canvas shader applies to everything
/// a node draws and the view draws sixteen moles and a HUD as well. Sitting at a negative
/// <see cref="CanvasItem.ZIndex"/> puts it behind its own parent, which is what lets the view carry
/// on drawing the world in one place without any of it being shaded.
///
/// It covers the whole pane and paints the backdrop as well as the ground, which saves keeping a
/// separate sky draw ordered behind it, and means anything off the edge of the map carries on as
/// countryside for free.
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

        Dress(stage);

        // Behind its own parent, which is where the ground belongs.
        ZIndex = -1;

        // Interpolated on purpose, and this only affects the cell field: the ground and backdrop
        // textures carry their own filter hints in the shader. Everywhere else in this client wants
        // point sampling so the cell grid stays honest; here the whole idea is to read between the
        // cells.
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

    /// <summary>
    /// Hands the material everything about this match that will not change again.
    /// </summary>
    /// <remarks>
    /// Which is nearly all of it: the three textures, the split, where each of them sits in the
    /// world, and the outline colour. None of it depends on where the camera is, and a match does
    /// not get a second backdrop.
    ///
    /// Done once rather than per draw for two reasons. It was four textures handed to the same
    /// material sixty times a second with four panes on screen, which is work nobody asked for; and
    /// a texture set from inside a draw call left the renderer still holding a reference to it after
    /// the pane that set it had gone, which showed up as four textures leaked at exit.
    /// </remarks>
    private void Dress(Stage stage)
    {
        Texture2D field = stage.Terrain;
        Backdrop backdrop = stage.Backdrop;

        _shading.SetShaderParameter(
            "field_texel", new Vector2(1f / field.GetWidth(), 1f / field.GetHeight()));

        _shading.SetShaderParameter("dirt", Art.Dirt);
        _shading.SetShaderParameter("deep", Art.Deep);
        _shading.SetShaderParameter("sky", Art.Surface);
        _shading.SetShaderParameter("split", backdrop.Split);

        _shading.SetShaderParameter("dirt_repeat", backdrop.DirtRepeat);
        _shading.SetShaderParameter("dirt_shift", backdrop.DirtShift);
        _shading.SetShaderParameter("deep_repeat", backdrop.DeepRepeat);
        _shading.SetShaderParameter("deep_shift", backdrop.DeepShift);
        _shading.SetShaderParameter("sky_repeat", backdrop.SkyRepeat);
        _shading.SetShaderParameter("sky_shift", backdrop.SkyShift);
        _shading.SetShaderParameter("sky_texel", backdrop.SkyTexel);

        _shading.SetShaderParameter("edge_colour", Palette.Edge);

        // Nine samples of the cell field per fragment, or five on the low setting. Set here with
        // everything else that cannot change during a match: the quality is decided once when the
        // game starts, and a shader parameter written every draw is what this method exists to
        // avoid.
        _shading.SetShaderParameter("blur_taps", Quality.BlurTaps());
    }

    public override void _Draw()
    {
        if (Size.X <= 0f || Size.Y <= 0f || _map.Size.X <= 0f || _map.Size.Y <= 0f)
        {
            return;
        }

        // Only the three that move.
        _shading.SetShaderParameter("map_origin", _map.Position);
        _shading.SetShaderParameter("map_extent", _map.Size);
        _shading.SetShaderParameter("pane_size", Size);

        // One rect over the whole pane, so the shader gets a fragment everywhere it has something
        // to say, including the parts of the pane the map does not reach.
        DrawTextureRect(_stage.Terrain, new Rect2(Vector2.Zero, Size), false);
    }
}
