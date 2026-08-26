using MoleSim.Numerics;

namespace MoleSim
{
    /// <summary>
    /// The one place that knows how world metres relate to terrain cells.
    /// </summary>
    /// <remarks>
    /// The cell size is a power-of-two fraction of a metre on purpose. A nominal 5 cm cell
    /// is not representable in Q48.16: one twentieth of a metre is 3276.8 raw units, so it
    /// has to round, and every conversion afterwards carries the error. That stays
    /// deterministic, so online play would survive it, but it makes positions on a cell
    /// boundary land unpredictably on one side or the other. A position of exactly -0.1 m
    /// worked out to cell -3 rather than -2 during development for precisely that reason.
    ///
    /// One sixteenth of a metre is 4096 raw units exactly, so metres and cells convert by
    /// shifting: exact, reversible, and faster than dividing. At 6.25 cm it is also close
    /// enough to the intended 5 cm that nothing about the feel of the game changes, and
    /// the shipping grid of 2000x960 cells comes out at exactly the 125 m by 60 m field
    /// the design calls for.
    /// </remarks>
    public static class WorldScale
    {
        /// <summary>Cells per metre. A power of two so the conversion is a shift.</summary>
        public const int CellsPerMetre = 16;

        /// <summary>
        /// Bits to shift a raw <see cref="Fix64"/> right by to reach a cell index.
        /// </summary>
        private const int CellShift = Fix64.FractionalBits - 4;

        /// <summary>Cell size in metres: exactly 1/16, or 6.25 cm.</summary>
        public static Fix64 CellSize => Fix64.Ratio(1, CellsPerMetre);

        /// <summary>Shipping map width in cells, which is 125 m.</summary>
        public const int DefaultMapWidthInCells = 2000;

        /// <summary>Shipping map height in cells, which is 60 m.</summary>
        public const int DefaultMapHeightInCells = 960;

        /// <summary>
        /// Cell index containing a world coordinate, flooring so that coordinates just
        /// below zero land in cell -1 rather than collapsing onto cell 0.
        /// </summary>
        public static int ToCell(Fix64 metres) => (int)(metres.Raw >> CellShift);

        /// <summary>World coordinate of a cell's left or top edge.</summary>
        public static Fix64 ToMetres(int cell) => Fix64.FromRaw((long)cell << CellShift);

        /// <summary>World coordinate of a cell's centre, which is where samples are taken.</summary>
        public static Fix64 ToCentreMetres(int cell) =>
            Fix64.FromRaw(((long)cell << CellShift) + (1L << (CellShift - 1)));

        /// <summary>Cell containing a position.</summary>
        public static void ToCell(Vec2 position, out int cellX, out int cellY)
        {
            cellX = ToCell(position.X);
            cellY = ToCell(position.Y);
        }
    }
}
