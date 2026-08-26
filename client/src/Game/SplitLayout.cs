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
    /// <summary>One player's window, the shared one, or one of the replay's cameras.</summary>
    public readonly struct Pane
    {
        public Pane(Rect2 rect, int seat, float pixelsPerMetre, int[]? watching = null)
        {
            Rect = rect;
            Seat = seat;
            PixelsPerMetre = pixelsPerMetre;
            Watching = watching;
        }

        public Rect2 Rect { get; }

        /// <summary>Which platoon this belongs to, or -1 for a view everybody shares.</summary>
        public int Seat { get; }

        /// <summary>
        /// Zoom for this pane. Derived from its height for a planning pane, so every pane shows
        /// the same slice of world vertically however the screen was carved up, and chosen by
        /// <see cref="ReplayDirector"/> for a replay camera, to frame what it is pointed at.
        /// </summary>
        public float PixelsPerMetre { get; }

        /// <summary>
        /// Which mole slots this camera is following, or null when it follows a seat.
        /// </summary>
        /// <remarks>
        /// Only the replay sets this. A planning pane belongs to a platoon and looks at whatever
        /// that platoon is steering; a replay camera belongs to a piece of the action and looks at
        /// whoever is in it, which after the round has resolved is a knowable list of moles.
        /// </remarks>
        public int[]? Watching { get; }
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

    /// <summary>
    /// The same carve-up as <see cref="PerSeat"/>, but as bare rectangles for cameras that
    /// belong to a piece of the action rather than to a platoon.
    /// </summary>
    public static Rect2[] Grid(int count, Rect2 band)
    {
        if (count <= 1)
        {
            return new[] { band };
        }

        if (count == 2)
        {
            return new[]
            {
                CellRect(band, 0, 0, 1, 2),
                CellRect(band, 0, 1, 1, 2),
            };
        }

        Rect2[] cells = new Rect2[count];

        for (int index = 0; index < count; index++)
        {
            cells[index] = CellRect(band, index % 2, index / 2, 2, 2);
        }

        return cells;
    }

    private static Pane Cell(Rect2 band, int column, int row, int columns, int rows, int seat)
    {
        Rect2 rect = CellRect(band, column, row, columns, rows);

        return new Pane(rect, seat, ZoomFor(rect.Size.Y));
    }

    private static Rect2 CellRect(Rect2 band, int column, int row, int columns, int rows)
    {
        float width = (band.Size.X - (Gutter * (columns - 1))) / columns;
        float height = (band.Size.Y - (Gutter * (rows - 1))) / rows;

        return new Rect2(
            band.Position.X + (column * (width + Gutter)),
            band.Position.Y + (row * (height + Gutter)),
            width,
            height);
    }

    private static float ZoomFor(float paneHeight) =>
        Mathf.Clamp(paneHeight / MetresTall, FurthestZoom, ClosestZoom);

    /// <summary>How much world a pane shows across, at the zoom its shape makes natural.</summary>
    /// <remarks>
    /// Sixteen to nine of <see cref="MetresTall"/>, so a pane the shape of the project's own
    /// window frames exactly what it always did and nothing about a quiet round looks different
    /// from before there was a director.
    /// </remarks>
    private const float MetresWide = 32f;

    /// <summary>
    /// The zoom that frames a given spread of action in a given cell, and whether that spread is
    /// compact enough to be one shot at all.
    /// </summary>
    /// <remarks>
    /// The two answers are deliberately independent. How far back to stand depends on the pane;
    /// whether a piece of the action is one piece does not, because "these moles are in the same
    /// fight" is a fact about the map rather than about how the screen happens to be carved. Tying
    /// them together made the director's decisions change when a pane changed shape, which is how
    /// you get a two-camera cut of one scrum.
    ///
    /// The natural-zoom cap is what stops a shot being tighter than it needs to be, and it takes
    /// the pane's shape seriously: a shot shows eighteen metres of height or thirty-two of width,
    /// whichever that pane's proportions can manage, rather than both. Demanding both made a wide
    /// short band show sixty-four metres across in order to satisfy the height, which is more than
    /// the whole map is wide and left two thirds of the frame empty.
    /// </remarks>
    public static float ZoomToFit(Rect2 cell, float widthMetres, float heightMetres, out bool compact)
    {
        float wanted = Mathf.Min(
            cell.Size.X / (widthMetres + Breathing),
            cell.Size.Y / (heightMetres + Breathing));

        compact = widthMetres <= ShareableWidth && heightMetres <= ShareableHeight;

        return Mathf.Clamp(
            Mathf.Min(wanted, NaturalZoom(cell)), FurthestZoom, ClosestZoom);
    }

    private static float NaturalZoom(Rect2 cell) =>
        Mathf.Max(cell.Size.Y / MetresTall, cell.Size.X / MetresWide);

    /// <summary>Metres of air kept around the action, so nothing is pressed against a frame edge.</summary>
    private const float Breathing = 6f;

    /// <summary>
    /// How far apart moles can be and still count as one fight. Past this the design's rule is to
    /// split, and forty metres is deliberately well short of the map's sixty-odd: two platoons at
    /// opposite ends of it are two fights, whatever a wide enough lens could technically contain.
    /// </summary>
    private const float ShareableWidth = 40f;

    private const float ShareableHeight = 24f;
}
