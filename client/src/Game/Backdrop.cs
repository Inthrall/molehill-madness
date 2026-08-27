using System.Collections.Generic;
using Godot;
using MoleSim;
using MoleSim.Numerics;
using MoleSim.Terrain;

/// <summary>
/// What is behind the ground, and the garden dressing standing on it. Decided once, at the start
/// of a match, and never again.
/// </summary>
/// <remarks>
/// The map is a third sky over two thirds of caved-out ground, so a pixel of air is one of two
/// completely different things: it is either open sky above the surface or the inside of a tunnel
/// below it, and the two want opposite backdrops. Which one a column is on comes from where its
/// ground started, not from where its ground is now, and that distinction is the whole reason this
/// class exists.
///
/// Everything here is frozen from the pristine map before a single round is played. Deriving it
/// from the live terrain would be cheaper to write and wrong in a way that only shows up in play:
/// dig a shaft down from the lawn and the column's first solid cell drops thirty metres, so the
/// backdrop behind the shaft would turn from soil to sky and the tunnel would read as a canyon.
/// Blow the top off a hill and the sky would follow the crater down. A backdrop that moves when
/// the foreground is destroyed is not a backdrop.
///
/// It is frozen rather than recomputed for a second reason as well. A replay draws the terrain as
/// it stood earlier in the round while the simulation has already run ahead, so there are two
/// versions of the ground on screen over the course of one round and neither is the one the
/// backdrop belongs to.
/// </remarks>
public sealed class Backdrop
{
    /// <summary>Something standing in the garden, purely for the look of it.</summary>
    /// <remarks>
    /// Drawn rather than built out of terrain, which is the opposite of the choice the map maker
    /// takes for pots and gnomes and is right for opposite reasons. Those are cover: a mole hides
    /// behind them, shells break them, and building them out of cells is what makes all of that
    /// true for free. These are scenery. A tuft of grass that stopped a shell would be a
    /// surprise nobody could have planned around, and one made of cells would cost the same
    /// digging as a wall.
    /// </remarks>
    public readonly struct Sprig
    {
        public Sprig(Texture2D art, int column, int row, Rect2 where, bool flipped)
        {
            Art = art;
            Column = column;
            Row = row;
            Where = where;
            Flipped = flipped;
        }

        public Texture2D Art { get; }

        /// <summary>The cell it is planted in, which is the cell that has to still be there.</summary>
        public int Column { get; }

        public int Row { get; }

        /// <summary>Where it goes, in world metres.</summary>
        public Rect2 Where { get; }

        /// <summary>Whether to draw it mirrored, so eight tufts do not read as eight copies.</summary>
        public bool Flipped { get; }

        /// <summary>
        /// Whether the ground it was planted in is still there.
        /// </summary>
        /// <remarks>
        /// Checked every frame rather than once, because the ground goes. Without this a crater
        /// leaves its flowers hanging in the air over it, which is the same fault the map maker's
        /// fallen log had and the same reason it is worth a line of code: nothing in this game
        /// should be visibly exempt from being blown up.
        /// </remarks>
        public bool StillStanding(TerrainGrid ground) =>
            ground.Contains(Column, Row) && MaterialTable.IsSolid(ground[Column, Row]);
    }

    // ---- How big the ground is drawn -------------------------------------------------
    //
    // In cells, so the ground is a fixed size in the world rather than on the screen: a tile
    // spans the same six metres of map however far the camera has zoomed in, which is what makes
    // it look like ground and not like wallpaper stuck to the viewport.
    //
    // The art's own proportions do not match this game's scale and cannot be made to. A mole is
    // three quarters of a metre across in a map sixty metres wide, and the dirt was drawn with
    // maybe five strata bands and a scatter of stones to a tile. Sized so a stone looks like a
    // stone, the strata come out finer than turf; sized so the strata look like strata, every
    // stone is a boulder twice the size of a mole. Six metres is the compromise: bands about a
    // metre apart, and gravel around half a mole. Worth re-reading off a frame if the art changes.

    private const float DirtTileCells = 96f;

    /// <summary>
    /// Three quarters of the foreground's, so the two layers are told apart at a glance.
    /// </summary>
    /// <remarks>
    /// Smaller features read as further away, which is the whole job of the layer behind: a
    /// tunnel has to look like a hole in the ground rather than like ground of a slightly
    /// different colour. The art is already the more muted of the two, and this is the other
    /// half of the same trick.
    /// </remarks>
    private const float DeepTileCells = 72f;

    /// <summary>
    /// How much map the countryside panorama spans, in cells.
    /// </summary>
    /// <remarks>
    /// Thirty-two metres, which is about what one pane shows at its normal framing, so a player
    /// looking at the horizon sees roughly one panorama's worth of it and the repeat is not
    /// something the eye lands on.
    /// </remarks>
    private const float SkyTileCells = 512f;

    // ---- The garden ------------------------------------------------------------------

    /// <summary>
    /// How much dressing to scatter, per thousand cells of map.
    /// </summary>
    /// <remarks>
    /// Close to the map maker's own figure for the things it builds out of terrain, which is not a
    /// coincidence: the surface only has so much room, and the two lots share it. Higher and the
    /// skyline turns into a hedge, which hides the one thing on the surface a player has to read,
    /// namely the shape of the ground.
    /// </remarks>
    private const int ThingsPerThousandCells = 26;

    /// <summary>How many places to try per thing, before settling for fewer things.</summary>
    private const int AttemptsPerThing = 24;

    /// <summary>Cells of surface height change across a footprint that still counts as flat.</summary>
    /// <remarks>
    /// Tighter than the map maker's ten, because that is a test for whether an object can stand
    /// somewhere and this is a test for whether it looks planted. A pot half-buried on a slope is
    /// cover either way; a tuft of grass with daylight under one side is a mistake.
    /// </remarks>
    private const int SteepestGroundToStandOn = 5;

    /// <summary>Cells two things have to be apart, so they read as scattered and not as a row.</summary>
    private const int LeastGapCells = 18;

    /// <summary>How many of ten things are grass. The rest are the odds and ends.</summary>
    private const int GrassInTen = 7;

    private Backdrop(
        Texture2D split, Sprig[] decor,
        Vector2 dirtRepeat, Vector2 dirtShift,
        Vector2 deepRepeat, Vector2 deepShift,
        Vector2 skyRepeat, Vector2 skyShift, float skyTexel)
    {
        Split = split;
        Decor = decor;
        DirtRepeat = dirtRepeat;
        DirtShift = dirtShift;
        DeepRepeat = deepRepeat;
        DeepShift = deepShift;
        SkyRepeat = skyRepeat;
        SkyShift = skyShift;
        SkyTexel = skyTexel;
    }

    /// <summary>
    /// The skyline as the shader reads it: one texel a column, holding that column's split row.
    /// </summary>
    /// <remarks>
    /// Sixteen bits a column rather than eight, packed across the red and green channels. Eight
    /// would quantise a four-hundred-and-eighty-row map to about two rows, and two rows the wrong
    /// way puts a band of soil in the sky directly above the grass, which reads as a dark halo
    /// along every hill. Sampled with point filtering for the same reason it is packed: blending
    /// two columns' high bytes together produces a row number belonging to neither.
    /// </remarks>
    public Texture2D Split { get; }

    public IReadOnlyList<Sprig> Decor { get; }

    /// <summary>How many times the ground texture fits across the map, per axis.</summary>
    public Vector2 DirtRepeat { get; }

    /// <summary>Where the ground texture starts, in tiles. Seeded, so strata differ per match.</summary>
    public Vector2 DirtShift { get; }

    public Vector2 DeepRepeat { get; }

    public Vector2 DeepShift { get; }

    public Vector2 SkyRepeat { get; }

    /// <summary>
    /// Where the panorama sits, in panorama tiles: a sideways phase, and the row its top is on.
    /// </summary>
    /// <remarks>
    /// The vertical half is the part that matters. The panorama has its own horizon in it, with
    /// hills above and near ground below, and it is placed so that horizon lands on the map's own
    /// average surface. Anywhere else and the backdrop disagrees with the foreground about where
    /// the ground is, which reads as distant fields floating above the lawn.
    /// </remarks>
    public Vector2 SkyShift { get; }

    /// <summary>Half a texel of the panorama's height, to clamp the sample inside it by.</summary>
    public float SkyTexel { get; }

    /// <summary>
    /// Reads the split out of a pristine map and scatters the garden along it.
    /// </summary>
    /// <param name="ground">
    /// The map as generated, before anything has been dug. Pass it before playing a round: this
    /// is the one moment the terrain and the backdrop agree, and the whole point of the class is
    /// to keep hold of it.
    /// </param>
    /// <param name="seed">
    /// The match's seed. Everything here comes off it, so two clients playing the same match are
    /// looking at the same garden, and <c>--seed=N</c> brings back the same one to look at again.
    /// </param>
    public static Backdrop Freeze(TerrainGrid ground, ulong seed)
    {
        int[] skyline = ReadSkyline(ground);

        // Its own stream, mixed off the match's seed. Drawing from the simulation's would make
        // the number of tufts on the lawn change how every shot afterwards lands, which is a
        // remarkable way for a decoration to lose a match.
        MatchRng rng = new MatchRng(seed ^ 0x9E3779B97F4A7C15UL);

        // Drawn into named locals in a fixed order rather than passed straight to the constructor,
        // where the order the arguments happen to be evaluated in would be what decides the garden.
        Sprig[] decor = Plant(ground, skyline, rng);
        Vector2 dirtShift = Phase(rng);
        Vector2 deepShift = Phase(rng);
        float skyPhase = Phase(rng).X;

        float skyTall = SkyTileCells * Art.Surface.GetHeight() / Art.Surface.GetWidth();

        // Where the panorama's top row goes, so its own horizon lands on the map's surface.
        float skyTop = Mean(skyline, ground.Height) - (Art.SurfaceHorizon * skyTall);

        return new Backdrop(
            Pack(skyline, ground.Height),
            decor,
            new Vector2(ground.Width / DirtTileCells, ground.Height / DirtTileCells),
            dirtShift,
            new Vector2(ground.Width / DeepTileCells, ground.Height / DeepTileCells),
            deepShift,
            new Vector2(ground.Width / SkyTileCells, ground.Height / skyTall),
            new Vector2(skyPhase, skyTop / skyTall),
            0.5f / Art.Surface.GetHeight());
    }

    /// <summary>Somewhere to start a tiling texture, so two matches do not have identical strata.</summary>
    private static Vector2 Phase(MatchRng rng) =>
        new Vector2(rng.NextInt(1000) / 1000f, rng.NextInt(1000) / 1000f);

    /// <summary>
    /// The first solid cell in each column, which on a pristine map is where the ground starts.
    /// </summary>
    /// <remarks>
    /// A scan rather than something the map maker hands over, because it is also right for a map
    /// that did not come from the map maker: the shipped game gets its maps from an artist through
    /// the map baker, and "the first solid cell" is true of any of them. The caves never break the
    /// surface, which the generator guarantees and <c>MapMakerTests</c> defends, so there is no
    /// risk of this finding the roof of a chamber instead of the lawn.
    ///
    /// Garden clutter standing on the surface raises its own few columns by the height of a
    /// gnome. That is invisible: those columns are solid ground for the whole height in question,
    /// so nothing on either side of the split is ever on screen there.
    /// </remarks>
    private static int[] ReadSkyline(TerrainGrid ground)
    {
        int[] skyline = new int[ground.Width];

        for (int column = 0; column < ground.Width; column++)
        {
            // Falls through to the floor of the world for a column with no ground in it at all,
            // which makes the whole column read as sky rather than as an unexplained band of soil.
            skyline[column] = ground.Height;

            for (int row = 0; row < ground.Height; row++)
            {
                if (MaterialTable.IsSolid(ground[column, row]))
                {
                    skyline[column] = row;
                    break;
                }
            }
        }

        return skyline;
    }

    private static float Mean(int[] skyline, int height)
    {
        long total = 0;

        foreach (int row in skyline)
        {
            total += row;
        }

        return skyline.Length > 0 ? (float)total / skyline.Length : height / 3f;
    }

    private static Texture2D Pack(int[] skyline, int height)
    {
        Image image = Image.CreateEmpty(skyline.Length, 1, false, Image.Format.Rg8);

        for (int column = 0; column < skyline.Length; column++)
        {
            int packed = Mathf.RoundToInt(
                Mathf.Clamp((float)skyline[column] / height, 0f, 1f) * 65535f);

            image.SetPixel(column, 0, Color.Color8((byte)(packed >> 8), (byte)(packed & 0xFF), 0));
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Scatters the dressing along the skyline, on flat ground and spread out.
    /// </summary>
    /// <remarks>
    /// Shaped like the map maker's own scattering, including the part that took three goes to get
    /// right there: the attempt count is its own variable rather than the loop bound, or the
    /// search gives up earlier every time it succeeds and the map ends up nearly bare.
    ///
    /// Nothing keeps clear of the starting positions, unlike the clutter built out of terrain.
    /// These are drawn behind the moles and collide with nothing, so a mole standing in the
    /// flowers is a mole standing in the flowers.
    /// </remarks>
    private static Sprig[] Plant(TerrainGrid ground, int[] skyline, MatchRng rng)
    {
        int wanted = ThingsPerThousandCells * ground.Width / 1000;
        List<Sprig> planted = new List<Sprig>(wanted);

        for (int attempt = 0; attempt < wanted * AttemptsPerThing && planted.Count < wanted; attempt++)
        {
            Texture2D art = rng.NextInt(10) < GrassInTen
                ? Art.Grass[rng.NextIndex(Art.Grass.Length)]
                : Art.Things[rng.NextIndex(Art.Things.Length)];

            float wide = art.GetWidth() / Art.DecorPixelsPerMetre;
            float tall = art.GetHeight() / Art.DecorPixelsPerMetre;
            int half = Mathf.Max(1, Mathf.CeilToInt(wide * WorldScale.CellsPerMetre / 2f));

            if (ground.Width <= half * 2)
            {
                break;
            }

            int column = half + rng.NextInt(ground.Width - (half * 2));
            bool flipped = rng.NextBool();

            if (TooClose(planted, column))
            {
                continue;
            }

            int highest = skyline[column];
            int lowest = skyline[column];

            for (int across = column - half; across <= column + half; across++)
            {
                highest = skyline[across] < highest ? skyline[across] : highest;
                lowest = skyline[across] > lowest ? skyline[across] : lowest;
            }

            if (lowest - highest > SteepestGroundToStandOn || lowest >= ground.Height)
            {
                continue;
            }

            // Planted on the low side of its own footprint, so the high side buries a little of it
            // rather than the low side leaving it hanging.
            float baseline = (float)lowest / WorldScale.CellsPerMetre;

            planted.Add(new Sprig(
                art,
                column,
                lowest,
                new Rect2(
                    ((float)column / WorldScale.CellsPerMetre) - (wide / 2f),
                    baseline - tall,
                    wide,
                    tall),
                flipped));
        }

        return planted.ToArray();
    }

    private static bool TooClose(List<Sprig> planted, int column)
    {
        foreach (Sprig sprig in planted)
        {
            if (Mathf.Abs(sprig.Column - column) < LeastGapCells)
            {
                return true;
            }
        }

        return false;
    }
}
