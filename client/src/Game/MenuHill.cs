using Godot;

/// <summary>
/// The ground the menus stand on.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the code screen, the gate, and the lobby all want the same
/// ground the menu has. The first version of this lived in the menu with a spoil heap drawn at a
/// fixed height somewhere else, so the heap floated in the sky with the moles balanced on it. Which
/// is a funny picture but not the intended one.
///
/// It used to be drawn: the map's two soil colours under a turf line, out of a handful of cosine
/// terms rather than a texture, which kept the menus on the same no-assets footing as everything
/// else. There is a painted one now, a turf line over a cross-section of burrows, and it says what
/// the game is about in a way three cosines never did.
///
/// Covered rather than stretched, which is the whole of the arithmetic here. The sheet is very
/// nearly sixteen by nine, so on a normal window the crop is a few pixels; on anything else it
/// crops rather than squashing the burrows into ovals.
/// </remarks>
public static class MenuHill
{
    public static void Draw(CanvasItem into, Vector2 viewport)
    {
        Vector2 size = Covering(viewport);

        into.DrawTextureRect(Art.MenuGround, new Rect2((viewport - size) / 2f, size), false);
    }

    /// <summary>How big the sheet has to be to cover the window without distorting it.</summary>
    private static Vector2 Covering(Vector2 viewport)
    {
        Vector2 sheet = Art.MenuGround.GetSize();

        return sheet * Mathf.Max(viewport.X / sheet.X, viewport.Y / sheet.Y);
    }
}
