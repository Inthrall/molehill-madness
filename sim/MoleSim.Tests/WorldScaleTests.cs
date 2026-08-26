using MoleSim;
using MoleSim.Numerics;

namespace MoleSim.Tests;

[TestFixture]
public sealed class WorldScaleTests
{
    [Test]
    public void CellSizeIsExactlyRepresentable()
    {
        // The whole reason the cell is a sixteenth of a metre rather than a twentieth.
        // A 5 cm cell would be 3276.8 raw units and would have to round; this one does not.
        Assert.Multiple(() =>
        {
            Assert.That(WorldScale.CellSize.Raw, Is.EqualTo(4096L));
            Assert.That(WorldScale.CellSize.ToDecimal(), Is.EqualTo(0.0625m));
            Assert.That(WorldScale.CellSize * Fix64.FromInt(WorldScale.CellsPerMetre),
                Is.EqualTo(Fix64.One), "sixteen cells should make exactly one metre");
        });
    }

    [Test]
    public void ANominalFiveCentimetreCellWouldNotHaveBeenExact()
    {
        // Kept as a test so the reasoning survives the next person who wonders why the
        // cell is 6.25 cm.
        Fix64 fiveCentimetres = Fix64.Ratio(1, 20);

        Assert.That(fiveCentimetres * Fix64.FromInt(20), Is.Not.EqualTo(Fix64.One),
            "twenty cells of a rounded 1/20 m do not add back up to a metre");
    }

    [Test]
    public void MetresAndCellsRoundTripExactly()
    {
        for (int cell = -500; cell <= 500; cell++)
        {
            Fix64 metres = WorldScale.ToMetres(cell);

            Assert.That(WorldScale.ToCell(metres), Is.EqualTo(cell),
                $"cell {cell} did not survive the round trip");
        }
    }

    [Test]
    public void CellCentresSitHalfACellInsideTheirCell()
    {
        Fix64 halfCell = WorldScale.CellSize / Fix64.FromInt(2);

        for (int cell = -10; cell <= 10; cell++)
        {
            Fix64 centre = WorldScale.ToCentreMetres(cell);

            Assert.Multiple(() =>
            {
                Assert.That(centre - WorldScale.ToMetres(cell), Is.EqualTo(halfCell));
                Assert.That(WorldScale.ToCell(centre), Is.EqualTo(cell),
                    "a cell's centre must be inside that cell");
            });
        }
    }

    [Test]
    public void ToCellFloorsRatherThanTruncating()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorldScale.ToCell(Fix64.Zero), Is.EqualTo(0));
            Assert.That(WorldScale.ToCell(Fix64.Epsilon), Is.EqualTo(0));
            Assert.That(WorldScale.ToCell(-Fix64.Epsilon), Is.EqualTo(-1),
                "the smallest step below zero belongs to the cell on the left");
            Assert.That(WorldScale.ToCell(Fix64.One), Is.EqualTo(16));
            Assert.That(WorldScale.ToCell(-Fix64.One), Is.EqualTo(-16));
        });
    }

    [Test]
    public void ShippingMapIsTheFieldTheDesignAsksFor()
    {
        Fix64 width = WorldScale.ToMetres(WorldScale.DefaultMapWidthInCells);
        Fix64 height = WorldScale.ToMetres(WorldScale.DefaultMapHeightInCells);

        Assert.Multiple(() =>
        {
            Assert.That(width.ToDecimal(), Is.EqualTo(125m), "map should be 125 m wide");
            Assert.That(height.ToDecimal(), Is.EqualTo(60m), "map should be 60 m deep");
        });
    }

    [Test]
    public void AFullSurfaceRunCrossesAKnownSliceOfTheMap()
    {
        // The design's headline number: 8 seconds at 5 m/s is 40 m, which should be a bit
        // under a third of the field. If the map or the speed is ever retuned, this is
        // where the two stop agreeing.
        Fix64 reach = Fix64.FromInt(40);
        Fix64 mapWidth = WorldScale.ToMetres(WorldScale.DefaultMapWidthInCells);

        Assert.That((mapWidth / reach).ToDecimal(), Is.EqualTo(3.125m).Within(0.01m),
            "the map should be about three full surface runs wide");
    }
}
