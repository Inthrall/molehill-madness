using Godot;

/// <summary>
/// How far through loading the art the game is.
/// </summary>
/// <remarks>
/// A real proportion, not a reassurance. The number of textures a match needs is known before any
/// of them is opened, so this counts them: the bar is at a third because a third of them are in.
///
/// That is the whole difference between this and the bar in the lobby, and it is worth keeping the
/// two apart. The lobby waits for other people to arrive over a network and cannot know how long
/// that will be, so its bar sweeps to say that something is happening. This one can know, so it
/// says how much is done. A sweeping bar where a proportion was available would be throwing away
/// the only useful thing there is to show.
///
/// It sits on the same cream as the menu rather than over a darkened match, because there is no
/// match yet: the world is built but not a frame of it has been drawn, and a loading bar floating
/// over a half-drawn game reads worse than one on a plain ground.
/// </remarks>
public partial class LoadingBar : Control
{
    private float _along;

    public LoadingBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
    }

    /// <summary>How far through, from nothing to one.</summary>
    public void Show(float along)
    {
        _along = Mathf.Clamp(along, 0f, 1f);
        Visible = true;
        QueueRedraw();
    }

    public void Done()
    {
        Visible = false;
    }

    public override void _Draw()
    {
        Vector2 viewport = Size;

        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);

        float wide = Mathf.Clamp(viewport.X * 0.3f, 160f, 460f);
        float tall = Mathf.Clamp(viewport.X * 0.016f, 9f, 20f);
        Vector2 middle = viewport / 2f;
        Rect2 track = new Rect2(middle.X - (wide / 2f), middle.Y - (tall / 2f), wide, tall);

        DrawRect(track, new Color(Palette.Ink, 0.16f));
        DrawRect(
            new Rect2(track.Position, new Vector2(wide * _along, tall)),
            new Color(Palette.Ink, 0.8f));

        // A mole coming up out of the ground as the bar fills, which is the game's own silhouette
        // and gives the eye something to read the progress against other than a rectangle.
        float rise = Mathf.Clamp(viewport.X * 0.03f, 18f, 44f);

        Glyphs.Mole(
            this,
            new Vector2(track.Position.X + (wide * _along), middle.Y - (tall * 0.5f) - rise),
            rise * 1.5f,
            new Color(Palette.Ink, 0.55f));
    }
}
