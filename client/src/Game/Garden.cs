using Godot;

/// <summary>
/// The dressing standing on the surface: tufts of grass, spoil heaps, flowers, a stone.
/// </summary>
/// <remarks>
/// A node of its own for the same reason <see cref="TerrainSkin"/> is one, and a different one.
/// The skin needs to be separate because a canvas shader would otherwise apply to the whole view;
/// this needs to be separate because a texture filter would. The scene point-samples everything, so
/// that the terrain field reads as cells rather than as smudge, and a grass tuft shrunk from a
/// hundred and sixty pixels to sixty with point sampling loses half its blades to aliasing.
///
/// It sits at the same negative <see cref="CanvasItem.ZIndex"/> as the skin and is added after it,
/// which puts it in front of the ground and behind everything the view draws itself. That is where
/// scenery belongs: in front of the soil it is planted in, behind the moles, the crates, the lava
/// and the instruments.
/// </remarks>
public partial class Garden : Control
{
    private readonly Stage _stage;
    private Vector2 _offset;
    private float _scale;

    public Garden(Stage stage)
    {
        _stage = stage;
        MouseFilter = MouseFilterEnum.Ignore;

        // In front of the ground, behind the view's own drawing.
        ZIndex = -1;

        // Smoothed and mipmapped, unlike the rest of the scene. These are pictures rather than a
        // cell field, they are always drawn smaller than they are stored, and a thin blade of grass
        // minified without mipmaps either aliases into dashes or costs a texture cache miss a
        // fragment. The imported sprites have mipmaps/generate=true to match.
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    /// <summary>
    /// Fills the pane, told where the camera is and how close.
    /// </summary>
    /// <param name="offset">What world pixels are shifted by to land on this pane.</param>
    /// <param name="pixelsPerMetre">How far in the camera is.</param>
    public void Cover(Vector2 offset, float pixelsPerMetre)
    {
        Position = Vector2.Zero;
        Size = GetParentAreaSize();
        _offset = offset;
        _scale = pixelsPerMetre;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Size.X <= 0f || Size.Y <= 0f)
        {
            return;
        }

        // The pane, in world pixels, so nothing off it is handed to the renderer at all. Cheap,
        // and it matters: four panes times everything in the garden is four times the draw calls
        // for the same picture.
        float left = -_offset.X;
        float top = -_offset.Y;
        float right = left + Size.X;
        float bottom = top + Size.Y;

        DrawSetTransform(_offset, 0, Vector2.One);

        foreach (Backdrop.Sprig sprig in _stage.Backdrop.Decor)
        {
            Rect2 where = new Rect2(sprig.Where.Position * _scale, sprig.Where.Size * _scale);

            if (where.End.X < left || where.Position.X > right
                || where.End.Y < top || where.Position.Y > bottom)
            {
                continue;
            }

            // The ground it was planted in may have been blown out from under it since, in which
            // case it went too.
            if (!sprig.StillStanding(_stage.Ground))
            {
                continue;
            }

            if (sprig.Flipped)
            {
                // A rectangle of negative width, which is how a canvas item is asked to mirror a
                // texture. Eight tufts of grass otherwise read as eight copies of eight tufts.
                where = new Rect2(where.End.X, where.Position.Y, -where.Size.X, where.Size.Y);
            }

            DrawTextureRect(sprig.Art, where, false);
        }

        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
