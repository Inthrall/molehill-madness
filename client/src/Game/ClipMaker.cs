using System.Collections.Generic;
using Godot;
using Molehill.Clip;
using MoleSim.Match;

/// <summary>
/// Re-simulates a round into a portrait viewport and writes it out as an animated PNG.
/// </summary>
/// <remarks>
/// The plan's clip pipeline, minus the hardware encoders. This is the path that is certain to exist:
/// no platform plugin, no bundled binary, no licence, and the design already calls it out as the
/// pre-designed fallback if the encoder spike misses its five seconds on a mid phone.
///
/// A clip is a re-simulation rather than a screen recording, which is the whole reason it is cheap.
/// The match is a seed and a list of plans, so the round can be played again at any size, in any
/// aspect, at any speed, from data that is already on the device. Portrait because a clip is for a
/// phone, and 1080 by 1920 because that is what the plan says the video path targets.
///
/// The frames are captured smaller than they are rendered, and that is deliberate rather than a
/// shortcut. A full-size frame is 1080 times 1920 times four bytes, which is eight megabytes, and a
/// few seconds of them is a gigabyte of RGBA sitting in memory before anything has been compressed.
/// A hardware encoder never holds more than a frame or two because it consumes them as they arrive;
/// an APNG has to have all of them at once. So the fallback is visibly a fallback, and saying so is
/// better than quietly running a phone out of memory.
/// </remarks>
public partial class ClipMaker : Node
{
    /// <summary>What the round is rendered at, which is what the plan asks for.</summary>
    private const int RenderWide = 1080;

    private const int RenderTall = 1920;

    /// <summary>
    /// What the fallback keeps, which is a third of it in each direction.
    /// </summary>
    /// <remarks>
    /// Three hundred and sixty by six hundred and forty: a third of the render in each direction, so a
    /// ninth of the pixels, and forty-five frames is about forty megabytes of RGBA rather than three
    /// hundred and seventy. The first version kept a quarter and produced a file under a megabyte,
    /// which was leaving fidelity on the table for no reason. Still visibly a fallback, and the point
    /// of a fallback is that it exists.
    /// </remarks>
    private const int KeepWide = 360;

    private const int KeepTall = 640;

    /// <summary>
    /// How fast the clip plays, and how long it runs.
    /// </summary>
    /// <remarks>
    /// Fifteen a second rather than thirty, and three seconds rather than eight. Half the frames of
    /// the simulation and a third of a round: what a clip is for is the moment, and the design's own
    /// replay theatre already establishes that the interesting part of a round is a couple of seconds
    /// either side of the thing that happened.
    /// </remarks>
    private const int ClipFps = 15;

    private const int ClipSeconds = 3;

    /// <summary>Frames in a finished clip.</summary>
    public const int Frames = ClipFps * ClipSeconds;

    /// <summary>
    /// Plays a round again into an offscreen viewport and hands back an animated PNG.
    /// </summary>
    /// <remarks>
    /// Awaits the renderer between frames, because a viewport's texture is not the frame until the
    /// frame has been drawn. Reading it in the same pass gives the previous one, which produces a clip
    /// that is subtly one frame behind everything and is very hard to notice.
    /// </remarks>
    public async System.Threading.Tasks.Task<byte[]?> Make(
        ulong seed, int playerCount, int mapWidthCells, int mapHeightCells,
        IReadOnlyList<Plan[]> rounds, Moment moment)
    {
        if (!moment.Exists || rounds is null || moment.Round > rounds.Count)
        {
            return null;
        }

        // The world from scratch, and every round up to the one being clipped replayed into it. This
        // is the part that would be impossible with a screen recording and is nearly free here.
        MoleMatch match = MoleMatch.Create(playerCount, seed, mapWidthCells, mapHeightCells);

        // Before any of those rounds are replayed into it, because that is the only moment the map
        // still says where the ground started. A clip made off a chewed-up map would put sky behind
        // every tunnel the match had dug by then.
        Backdrop backdrop = Backdrop.Freeze(match.Terrain, seed);

        RoundResult? wanted = null;

        for (int round = 0; round < moment.Round; round++)
        {
            foreach (Plan plan in rounds[round])
            {
                try
                {
                    match.SubmitPlan(plan);
                }
                catch (InvalidPlanException)
                {
                    // Dropped exactly as the live round dropped it, or the replay diverges from the
                    // match it is supposed to be a recording of.
                }
            }

            wanted = match.ResolveRound(record: true);
        }

        if (wanted?.Recording is null)
        {
            return null;
        }

        SubViewport viewport = new SubViewport
        {
            Size = new Vector2I(RenderWide, RenderTall),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };

        AddChild(viewport);

        // A copy of the terrain as it stood before the round, so craters appear as the shells land
        // rather than all at once, exactly as they do in the live replay.
        MoleSim.Terrain.TerrainGrid shadow = match.Terrain.Clone();
        TerrainView terrain = new TerrainView(shadow);
        IReadOnlyList<MoleSim.Terrain.TerrainChange> digging = wanted.Recording.TerrainChanges;
        int applied = 0;
        Stage stage = new Stage(
            match, shadow, terrain.Texture, backdrop, mapWidthCells, mapHeightCells)
        {
            Result = wanted,
            Recording = wanted.Recording,
            Planning = false,
            Planners = System.Array.Empty<SeatPlanner>(),
        };

        WorldView view = new WorldView(stage);
        viewport.AddChild(view);

        List<byte[]> frames = new List<byte[]>(Frames);

        // Centred on the moment rather than started at it, so a clip shows the shot as well as what
        // it did. A clip that opens on the explosion has thrown away the part that made it funny.
        int span = MatchSettings.TicksPerSecond * ClipSeconds;
        int from = Mathf.Max(0, moment.Tick - (span / 3));

        for (int frame = 0; frame < Frames; frame++)
        {
            int tick = Mathf.Min(
                from + (frame * MatchSettings.TicksPerSecond / ClipFps),
                wanted.Recording.Ticks - 1);

            stage.Tick = tick;
            stage.Seconds = MoleSim.Numerics.Fix64.Ratio(tick, MatchSettings.TicksPerSecond);
            // Craters appear as the shells land, the same way the live replay does it.
            int upTo = wanted.Recording.ChangesUpTo(tick);

            while (applied < upTo && applied < digging.Count)
            {
                shadow.Apply(digging[applied]);
                applied++;
            }

            terrain.Refresh();

            view.Occupy(
                new SplitLayout.Pane(
                    new Rect2(Vector2.Zero, new Vector2(RenderWide, RenderTall)),
                    seat: -1,
                    // About eleven metres across the frame. The first attempt showed twenty-two and the
                    // moles came out as specks: a portrait frame is narrow, and after the downscale a
                    // mole three quarters of a metre wide has very few pixels to be recognisable in.
                    pixelsPerMetre: RenderWide / 11f,
                    watching: Watching(moment)),
                index: 0,
                delta: 1d / ClipFps);

            // Two waits: one for the viewport to draw, one for the drawn frame to be readable.
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            Image shot = viewport.GetTexture().GetImage();
            shot.Convert(Image.Format.Rgba8);
            shot.Resize(KeepWide, KeepTall, Image.Interpolation.Bilinear);

            frames.Add(shot.GetData());
        }

        viewport.QueueFree();

        return Apng.Write(KeepWide, KeepTall, frames, ClipFps);
    }

    /// <summary>
    /// Which mole the camera follows: the one the moment happened to.
    /// </summary>
    /// <remarks>
    /// Falls back to the first slot when the moment names nobody, which happens for a plain hit since
    /// a hit records no tick and so no subject either. A clip framed on the wrong mole is a worse clip
    /// than one framed on the right one, but it is better than a tight shot of empty sky, which is a
    /// failure the replay director already has on record.
    /// </remarks>
    private static int[] Watching(Moment moment) => new[] { Mathf.Max(moment.Slot, 0) };
}
