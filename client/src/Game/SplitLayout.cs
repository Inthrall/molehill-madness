using Godot;

/// <summary>
/// Carves the screen into one view per player, or one view for everybody.
/// </summary>
/// <remarks>
/// The design asks for two different things from the same machinery. During planning
/// everybody works at once, so everybody needs their own window onto their own mole. During
/// the replay it asks for something subtler: separate views when the action is spread out,
/// and one shared view when it is close enough that four copies of the same hillside would
/// be worse than one.
///
/// Both come out of the same layout table, with the shared case being simply a layout of one.
///
/// Panes are wide and short rather than square. An artillery game is played along the ground,
/// so two players get stacked bands rather than side-by-side columns: a column is nearly
/// square, which is the worst shape for watching something get lobbed forty metres.
/// </remarks>
public static class SplitLayout
{
    /// <summary>One player's window, or the shared one.</summary>
    public readonly struct Pane
    {
        public Pane(Rect2 rect, int seat, float pixelsPerMetre)
        {
            Rect = rect;
            Seat = seat;
            PixelsPerMetre = pixelsPerMetre;
        }

        public Rect2 Rect { get; }

        /// <summary>Which platoon this belongs to, or -1 for a view everybody shares.</summary>
        public int Seat { get; }

        /// <summary>
        /// Zoom for this pane, derived from its height so that every pane shows the same
        /// slice of world vertically however the screen was carved up.
        /// </summary>
        public float PixelsPerMetre { get; }
    }

    /// <summary>
    /// How much world every pane shows top to bottom. Fixing this rather than the zoom is
    /// what stops a four-way split from becoming four keyholes: the panes get shorter, so
    /// they zoom out, and each still frames a mole against enough ground to aim at.
    /// </summary>
    private const float MetresTall = 18f;

    /// <summary>Zoom limits, so a phone does not end up microscopic nor a wall display absurd.</summary>
    private const float ClosestZoom = 48f;

    private const float FurthestZoom = 13f;

    /// <summary>Gap between panes, where the divider is drawn.</summary>
    public const float Gutter = 3f;

    /// <summary>One view for everybody, filling the band.</summary>
    public static Pane[] Shared(Rect2 band) =>
        new[] { new Pane(band, -1, ZoomFor(band.Size.Y)) };

    /// <summary>
    /// One view per seat. Two stack, three and four take a two-by-two grid, and with three
    /// the spare cell is left for the shared clock and tally rather than being padded out.
    /// </summary>
    public static Pane[] PerSeat(int seatCount, Rect2 band)
    {
        if (seatCount <= 1)
        {
            return Shared(band);
        }

        if (seatCount == 2)
        {
            return new[]
            {
                Cell(band, 0, 0, 1, 2, seat: 0),
                Cell(band, 0, 1, 1, 2, seat: 1),
            };
        }

        Pane[] panes = new Pane[seatCount];

        for (int seat = 0; seat < seatCount; seat++)
        {
            panes[seat] = Cell(band, seat % 2, seat / 2, 2, 2, seat);
        }

        return panes;
    }

    /// <summary>
    /// Where the spare cell is in a three-player split, so the clock and tally can go there
    /// instead of into a gap.
    /// </summary>
    public static bool TrySpareCell(int seatCount, Rect2 band, out Rect2 spare)
    {
        if (seatCount != 3)
        {
            spare = default;
            return false;
        }

        spare = Cell(band, 1, 1, 2, 2, seat: -1).Rect;
        return true;
    }

    private static Pane Cell(Rect2 band, int column, int row, int columns, int rows, int seat)
    {
        float width = ((band.Size.X - (Gutter * (columns - 1))) / columns);
        float height = ((band.Size.Y - (Gutter * (rows - 1))) / rows);

        Rect2 rect = new Rect2(
            band.Position.X + (column * (width + Gutter)),
            band.Position.Y + (row * (height + Gutter)),
            width,
            height);

        return new Pane(rect, seat, ZoomFor(height));
    }

    private static float ZoomFor(float paneHeight) =>
        Mathf.Clamp(paneHeight / MetresTall, FurthestZoom, ClosestZoom);

    /// <summary>
    /// Whether the whole round fits in one shared view, which is the design's rule for
    /// showing one screen rather than four.
    /// </summary>
    /// <remarks>
    /// Decided once, from the finished recording, rather than per frame. The round has
    /// already resolved by the time anybody watches it, so the answer is knowable up front,
    /// and deciding it up front is the only way to avoid the screen splitting and merging
    /// every time somebody gets punted sideways.
    /// </remarks>
    public static bool ActionFitsOneView(Rect2 band, float widthMetres, float heightMetres)
    {
        Pane shared = Shared(band)[0];
        float visibleWidth = shared.Rect.Size.X / shared.PixelsPerMetre;
        float visibleHeight = shared.Rect.Size.Y / shared.PixelsPerMetre;

        // A fifth of the view kept as breathing room, so the action is not pressed against
        // the edges of a view that technically contains it.
        return widthMetres <= visibleWidth * 0.8f && heightMetres <= visibleHeight * 0.8f;
    }
}
