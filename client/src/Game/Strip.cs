using Godot;

/// <summary>
/// An animation, as one texture with its frames laid out in a row.
/// </summary>
/// <remarks>
/// One file per set rather than one per frame, and the reason is registration rather than tidiness.
/// The importer cuts every frame of a set with the same rectangle, the union of what all of them
/// occupy, so a frame's position inside that rectangle is the position the artist drew it at. Cut to
/// its own bounds instead, each frame's bounding box would become its origin and the mole would
/// jitter as the animation played.
///
/// Which means the frames are all the same size, so a frame is a region and nothing here needs a
/// table of rectangles.
/// </remarks>
public sealed class Strip
{
    public Strip(Texture2D art, int frames)
    {
        Art = art;
        Frames = Mathf.Max(1, frames);
    }

    public Texture2D Art { get; }

    public int Frames { get; }

    /// <summary>One frame, in texture pixels.</summary>
    public Vector2 FrameSize =>
        new Vector2((float)Art.GetWidth() / Frames, Art.GetHeight());

    /// <summary>
    /// Draws one frame into a rectangle, optionally mirrored.
    /// </summary>
    /// <param name="frame">
    /// Which frame, wrapped rather than clamped, so a caller can hand over a tick count and get a
    /// cycle without doing the arithmetic itself.
    /// </param>
    /// <param name="tint">
    /// Multiplied over the frame. White leaves the artwork alone, which is what nearly every caller
    /// wants; the menu dims a face by passing white at less than full alpha.
    /// </param>
    public void Draw(CanvasItem into, Rect2 where, int frame, bool mirrored, Color? tint = null)
    {
        Vector2 size = FrameSize;
        int index = ((frame % Frames) + Frames) % Frames;
        Rect2 region = new Rect2(index * size.X, 0f, size.X, size.Y);

        if (mirrored)
        {
            // A rectangle of negative width, which is how a canvas item is asked to mirror a
            // texture. The alternative is a second set of files.
            //
            // The origin stays put. Godot turns a negative width into a horizontal flip and then
            // negates the size in place, without moving the rectangle, so handing it the right edge
            // as an origin drew every mirrored frame one full frame-width to the right of where it
            // belonged. That was the whole of the mole-outside-its-own-circle fault: the highlight
            // ring, the pluck bar and the planned tunnel were all in the right place, and only
            // left-facing artwork was displaced, which is why it looked intermittent. Measured at
            // 131 predicted against 139 observed pixels for a left-facing dig frame, with
            // right-facing moles correct throughout.
            where = new Rect2(where.Position.X, where.Position.Y, -where.Size.X, where.Size.Y);
        }

        into.DrawTextureRectRegion(
            Art, where, region, tint ?? Colors.White);
    }
}
