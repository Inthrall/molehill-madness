using Godot;

/// <summary>
/// The hillside the menus stand on.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the code screen needs the same ground the menu has, and the
/// first version of this lived in the menu with a spoil heap drawn at a fixed height somewhere else,
/// so the heap floated in the sky with the moles balanced on it. Which is a funny picture but not the
/// intended one.
///
/// Drawn from the same two soil colours the map uses, out of a handful of cosine terms rather than a
/// texture, which keeps the menus on the same no-assets footing as everything else.
/// </remarks>
public static class MenuHill
{
    public static void Draw(CanvasItem into, Vector2 viewport)
    {
        const int steps = 48;
        Vector2[] surface = new Vector2[steps + 3];

        for (int step = 0; step <= steps; step++)
        {
            float across = step / (float)steps;

            surface[step] = new Vector2(across * viewport.X, SurfaceAt(across, viewport));
        }

        surface[steps + 1] = new Vector2(viewport.X, viewport.Y);
        surface[steps + 2] = new Vector2(0, viewport.Y);

        into.DrawColoredPolygon(surface, Palette.Of(MoleSim.Terrain.Material.LooseSoil));

        // The turf line on top, which is what makes it read as ground rather than as a shape.
        Vector2[] turf = new Vector2[steps + 1];

        for (int step = 0; step <= steps; step++)
        {
            turf[step] = surface[step];
        }

        into.DrawPolyline(turf, Palette.Of(MoleSim.Terrain.Material.Turf), viewport.Y * 0.012f);
    }

    /// <summary>Where the hillside is at a given point across the screen.</summary>
    public static float SurfaceAt(float across, Vector2 viewport) =>
        (viewport.Y * 0.42f)
            + (Mathf.Cos(across * 7.1f) * viewport.Y * 0.045f)
            + (Mathf.Cos((across * 2.7f) + 1.4f) * viewport.Y * 0.03f);
}
