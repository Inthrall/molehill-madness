using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Tests;

[TestFixture]
public sealed class TerrainQueryTests
{
    private static readonly Fix64 Radius = MatchSettings.Radius;

    private static TerrainGrid SolidBlock(int width = 200, int height = 200)
    {
        TerrainGrid grid = new TerrainGrid(width, height);
        grid.FillRectangle(0, 0, width, height, Material.PackedSoil);
        return grid;
    }

    [Test]
    public void CarvingABodyAlwaysClearsRoomForThatBody()
    {
        // The invariant a real bug broke. Carving is measured in whole cells from a centre
        // cell; overlap is measured from a continuous position. If the carve is not wider
        // than the body, a mole part-way across a cell clears its own path and still
        // reports itself blocked by the sliver left behind, which stopped tunnelling dead
        // after exactly one step.
        //
        // Swept across a whole cell in sixteenths, because the failure only appears at
        // certain sub-cell offsets.
        Fix64 sixteenth = WorldScale.CellSize / Fix64.FromInt(16);

        for (int offsetX = 0; offsetX < 16; offsetX++)
        {
            for (int offsetY = 0; offsetY < 16; offsetY++)
            {
                TerrainGrid grid = SolidBlock();
                Vec2 position = new Vec2(
                    WorldScale.ToMetres(100) + (sixteenth * offsetX),
                    WorldScale.ToMetres(100) + (sixteenth * offsetY));

                TerrainQuery.CarveBody(grid, position, Radius);

                Assert.That(TerrainQuery.IsBlocked(grid, position, Radius), Is.False,
                    $"still blocked after carving at offset {offsetX}/16, {offsetY}/16");
            }
        }
    }

    [Test]
    public void OpenAirBlocksNothing()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);

        Assert.That(TerrainQuery.IsBlocked(grid, new Vec2(Fix64.FromInt(5), Fix64.FromInt(5)), Radius),
            Is.False);
    }

    [Test]
    public void SolidGroundBlocksABodyStandingInIt()
    {
        TerrainGrid grid = SolidBlock();

        Assert.That(TerrainQuery.IsBlocked(grid, new Vec2(Fix64.FromInt(5), Fix64.FromInt(5)), Radius),
            Is.True);
    }

    [Test]
    public void SupportMeansClearWhereYouAreAndSolidJustBelow()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);
        grid.FillRectangle(0, 100, 200, 100, Material.PackedSoil);

        Fix64 groundY = WorldScale.ToMetres(100);
        Vec2 standing = new Vec2(WorldScale.ToMetres(50), groundY - Radius - WorldScale.CellSize);
        Vec2 highUp = new Vec2(WorldScale.ToMetres(50), WorldScale.ToMetres(20));
        Vec2 buried = new Vec2(WorldScale.ToMetres(50), WorldScale.ToMetres(150));

        Assert.Multiple(() =>
        {
            Assert.That(TerrainQuery.IsSupported(grid, standing, Radius), Is.True, "on the ground");
            Assert.That(TerrainQuery.IsSupported(grid, highUp, Radius), Is.False, "in the air");
            Assert.That(TerrainQuery.IsSupported(grid, buried, Radius), Is.False, "buried is not supported");
        });
    }

    [Test]
    public void StepUpFindsRoomAboveALowLip()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);
        grid.FillRectangle(0, 100, 200, 100, Material.Turf);

        // A position buried a couple of cells into the ground, as a mole walking into a
        // low kerb would be.
        Vec2 inside = new Vec2(WorldScale.ToMetres(50), WorldScale.ToMetres(100) - Radius + WorldScale.CellSize);

        Assert.That(TerrainQuery.IsBlocked(grid, inside, Radius), Is.True, "precondition");
        Assert.That(
            TerrainQuery.TryStepUp(grid, inside, Radius, MatchSettings.StepHeight, out Vec2 stepped),
            Is.True);
        Assert.That(stepped.Y, Is.LessThan(inside.Y), "stepping up means going up");
        Assert.That(TerrainQuery.IsBlocked(grid, stepped, Radius), Is.False, "and being clear afterwards");
    }

    [Test]
    public void StepUpGivesUpOnAWallTallerThanTheStepHeight()
    {
        TerrainGrid grid = SolidBlock();
        Vec2 deep = new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(100));

        Assert.That(
            TerrainQuery.TryStepUp(grid, deep, Radius, MatchSettings.StepHeight, out _),
            Is.False,
            "there is no lip here, only more soil");
    }

    [Test]
    public void SnapDownFollowsAShallowDropAndIgnoresACliff()
    {
        TerrainGrid grid = new TerrainGrid(200, 400);
        grid.FillRectangle(0, 100, 200, 300, Material.Turf);

        // Just above the ground: snapping should find it.
        Vec2 nearGround = new Vec2(
            WorldScale.ToMetres(50),
            WorldScale.ToMetres(100) - Radius - (WorldScale.CellSize * Fix64.FromInt(3)));

        Assert.That(
            TerrainQuery.TrySnapDown(grid, nearGround, Radius, MatchSettings.GroundSnap, out Vec2 snapped),
            Is.True);
        Assert.That(snapped.Y, Is.GreaterThanOrEqualTo(nearGround.Y));

        // High above it: nothing within the snap distance, so this is a fall.
        Vec2 highUp = new Vec2(WorldScale.ToMetres(50), WorldScale.ToMetres(20));

        Assert.That(
            TerrainQuery.TrySnapDown(grid, highUp, Radius, MatchSettings.GroundSnap, out _),
            Is.False);
    }

    [Test]
    public void EscapeDirectionPointsAwayFromTheGround()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);
        grid.FillRectangle(0, 100, 200, 100, Material.PackedSoil);

        // Half sunk into a floor: the way out is up.
        Vec2 sunk = new Vec2(WorldScale.ToMetres(50), WorldScale.ToMetres(100));
        Vec2 escape = TerrainQuery.EscapeDirection(grid, sunk, Radius);

        Assert.That(escape.Y, Is.LessThan(Fix64.Zero), "up is negative Y");
        Assert.That(Fix64.Abs(escape.X), Is.LessThan(Fix64.Ratio(1, 4)), "and mostly straight up");
    }

    [Test]
    public void EscapeDirectionPointsSidewaysOutOfAWall()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);
        grid.FillRectangle(100, 0, 100, 200, Material.PackedSoil);

        // Buried in the left face of a wall: the way out is left.
        Vec2 inWall = new Vec2(WorldScale.ToMetres(100), WorldScale.ToMetres(50));
        Vec2 escape = TerrainQuery.EscapeDirection(grid, inWall, Radius);

        Assert.That(escape.X, Is.LessThan(Fix64.Zero), "back the way it came");
    }

    [Test]
    public void ABodyCompletelyBuriedIsToldToGoUp()
    {
        // Fully surrounded, so no direction is preferred by the geometry. Up is the least
        // surprising answer for something buried, and it must be deterministic.
        TerrainGrid grid = SolidBlock();
        Vec2 deep = new Vec2(WorldScale.ToCentreMetres(100), WorldScale.ToCentreMetres(100));

        Assert.That(TerrainQuery.EscapeDirection(grid, deep, Radius), Is.EqualTo(-Vec2.UnitY));
    }

    [Test]
    public void MaterialAtReadsTheCellUnderThePoint()
    {
        TerrainGrid grid = new TerrainGrid(200, 200);
        grid.FillRectangle(0, 100, 200, 100, Material.RootMat);

        Assert.Multiple(() =>
        {
            Assert.That(TerrainQuery.MaterialAt(grid, new Vec2(Fix64.FromInt(3), WorldScale.ToMetres(50))),
                Is.EqualTo(Material.Air));
            Assert.That(TerrainQuery.MaterialAt(grid, new Vec2(Fix64.FromInt(3), WorldScale.ToMetres(150))),
                Is.EqualTo(Material.RootMat));
        });
    }
}
