using System.Collections.Generic;
using Godot;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// One window onto the field, with its own camera and its own zoom.
/// </summary>
/// <remarks>
/// A Control rather than a SubViewport, and clipped to its own rectangle. Sharing one World2D
/// between four SubViewports is the more usual Godot answer, but it means four cameras chasing
/// one canvas and a lot of plumbing to get the seams right. Drawing the world once per view
/// into a clipped Control costs a handful of extra draw calls for a texture and sixteen moles,
/// which is nothing, and it puts the split exactly where the layout says it is.
///
/// A view owns no state about the match. It reads the shared stage and its own camera, so
/// four of them can look at the same moment from four places without any of them disagreeing
/// about what happened.
/// </remarks>
public partial class WorldView : Control
{
    private readonly Stage _stage;
    private Vector2 _cameraAt;
    private bool _cameraPlaced;
    private float _base = 40f;
    private float _zoom = 1f;
    private bool _manual;
    private float _scale = 40f;
    private int _seat = -1;
    private int[]? _watching;
    private int _camera;
    private float _pushing;

    private readonly TerrainSkin _skin;

    public WorldView(Stage stage)
    {
        _stage = stage;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;

        _skin = new TerrainSkin(stage);
        AddChild(_skin);
    }

    /// <summary>Which platoon this view belongs to, or -1 when everybody is sharing it.</summary>
    public int Seat => _seat;

    /// <summary>Takes up the pane the layout has given it.</summary>
    public void Occupy(SplitLayout.Pane pane, int index, double delta)
    {
        Position = pane.Rect.Position;
        Size = pane.Rect.Size;
        _camera = index;

        if (_seat != pane.Seat || !SameSubjects(_watching, pane.Watching))
        {
            // A view that has changed hands should not glide across the map to its new subject;
            // it should already be looking at it. A new camera also starts from the framing the
            // director chose for it rather than from whatever the last player pinched it to.
            _seat = pane.Seat;
            _watching = pane.Watching;
            _cameraPlaced = false;
            _manual = false;
            _zoom = 1f;
        }

        _base = pane.PixelsPerMetre;
        _pushing = PushWeight();
        _scale = _base * _zoom * Mathf.Lerp(1f, PushIn, _pushing);
        Chase(delta);
        QueueRedraw();
    }

    /// <summary>
    /// How far this camera is into its push on the round's big moment, if the moment is its.
    /// </summary>
    /// <remarks>
    /// Only the camera watching the mole it happens to pushes in. The others carry on at their own
    /// framing, slowed by the same clock, which is what a gallery does: everybody rolls, one camera
    /// gets the shot.
    ///
    /// This is the one deliberate exception to the rule that a camera never re-frames mid-round.
    /// That rule exists to stop the framing churning as moles wander; a single push onto the
    /// climax and back is the opposite of churn, and is the reason to have a director at all.
    /// </remarks>
    private float PushWeight()
    {
        Climax climax = _stage.Climax;

        if (_manual || _stage.Planning || _watching is null || !climax.Exists)
        {
            return 0f;
        }

        if (System.Array.IndexOf(_watching, climax.Slot) < 0)
        {
            return 0f;
        }

        return climax.Weight((float)_stage.Seconds.ToDecimal() * MatchSettings.TicksPerSecond);
    }

    /// <summary>How much closer the camera gets at the height of the moment.</summary>
    private const float PushIn = 1.8f;

    private static bool SameSubjects(int[]? mine, int[]? theirs)
    {
        if (ReferenceEquals(mine, theirs))
        {
            return true;
        }

        if (mine is null || theirs is null || mine.Length != theirs.Length)
        {
            return false;
        }

        for (int index = 0; index < mine.Length; index++)
        {
            if (mine[index] != theirs[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Turns a point on this pane into a point in the world.</summary>
    public Vec2 ToWorld(Vector2 onPane)
    {
        Vector2 metres = (onPane - Offset()) / _scale;

        return new Vec2(
            Fix64.Ratio((int)(metres.X * 256f), 256),
            Fix64.Ratio((int)(metres.Y * 256f), 256));
    }

    // ---- Camera ---------------------------------------------------------------------

    /// <summary>
    /// Drags the view. Once a player has done this the camera stops chasing, until the next beat
    /// puts it back to work.
    /// </summary>
    /// <remarks>
    /// A camera that fought back would be worse than no panning at all: every drag would slide
    /// straight back to the mole and the player would conclude the gesture was not implemented.
    /// So panning takes the camera outright, and the scene hands it back at each beat, which is
    /// the moment a player wants to be shown the action rather than to be looking where they left
    /// off.
    /// </remarks>
    public void Pan(Vector2 byPixels)
    {
        _manual = true;
        _cameraAt = Clamped(_cameraAt - byPixels);
        QueueRedraw();
    }

    /// <summary>
    /// Zooms about a point on the pane, keeping whatever is under it where it is.
    /// </summary>
    /// <remarks>
    /// About the finger rather than about the middle, because a pinch that drifts is a pinch
    /// nobody can aim with. The pane's own zoom is a multiplier on the one the layout chose, so a
    /// four-way split stays zoomed out relative to a full screen however far anybody pinches.
    /// </remarks>
    public void ZoomBy(float factor, Vector2 about)
    {
        float wanted = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);

        if (Mathf.IsEqualApprox(wanted, _zoom))
        {
            return;
        }

        Vector2 world = (about - Offset()) / _scale;

        _manual = true;
        _zoom = wanted;
        _scale = _base * _zoom;
        _cameraAt = Clamped((world * _scale) + (Size / 2f) - about);
        QueueRedraw();
    }

    /// <summary>Puts the camera back on the action. The zoom is a preference and survives.</summary>
    public void Recentre()
    {
        _manual = false;
        _cameraPlaced = false;
    }

    /// <summary>How far in and out a pinch may go, either side of the layout's own zoom.</summary>
    private const float MinZoom = 0.5f;

    private const float MaxZoom = 3f;

    private void Chase(double delta)
    {
        if (_manual)
        {
            // Still clamped: the pane or the zoom may have changed under a camera nobody is
            // driving this frame.
            _cameraAt = Clamped(_cameraAt);
            return;
        }

        // Biased so the subject sits below the middle of the pane. A mole centred exactly puts
        // half the view underground, which early in a match is undug soil and nothing else,
        // while the sky above it is where the shells actually go.
        Vector2 target = Clamped(Subject() - new Vector2(0, Size.Y * SkyBias));

        if (!_cameraPlaced)
        {
            _cameraAt = target;
            _cameraPlaced = true;
            return;
        }

        _cameraAt = _cameraAt.Lerp(target, Mathf.Min(1f, (float)delta * CameraChase));
    }

    /// <summary>How eagerly the camera chases, per second.</summary>
    private const float CameraChase = 6f;

    /// <summary>How far below the middle of the pane the subject sits, as a share of height.</summary>
    private const float SkyBias = 0.08f;

    /// <summary>
    /// What this view is looking at. Its own platoon's mole when it has one, and otherwise
    /// the middle of whatever is happening.
    /// </summary>
    private Vector2 Subject()
    {
        if (_watching is not null)
        {
            Vector2 group = Watched();

            if (_pushing <= 0f || _stage.Recording is null)
            {
                return group;
            }

            // Off the middle of the group and onto whoever it is happening to. A mole that has
            // already gone out is held at the spot it went out, which is where the pratfall plays.
            Vector2 onto = ToPixels(
                _stage.Recording.PositionAt(_stage.Seconds, _stage.Climax.Slot));

            return group.Lerp(onto, _pushing);
        }

        if (_seat >= 0)
        {
            return Actor(_seat);
        }

        Vector2 total = Vector2.Zero;
        int counted = 0;

        for (int seat = 0; seat < _stage.Planners.Length; seat++)
        {
            if (_stage.Planners[seat].Actor is null)
            {
                continue;
            }

            total += Actor(seat);
            counted++;
        }

        return counted == 0 ? Vector2.Zero : total / counted;
    }

    /// <summary>
    /// The middle of whatever this camera has been pointed at, as of right now.
    /// </summary>
    /// <remarks>
    /// The middle of the box around them rather than their average position. The director chose
    /// this camera's zoom from how far apart they ever get, so centring the box is what guarantees
    /// they all stay in shot; an average is dragged about by whichever clump is largest, and the
    /// mole on its own at the edge is the first thing to fall out of frame.
    ///
    /// Interpolated, so the camera moves at the display's rate rather than the simulation's. A
    /// camera that advanced thirty times a second would judder against sixty-hertz moles.
    /// </remarks>
    private Vector2 Watched()
    {
        RoundRecording? recording = _stage.Recording;

        if (recording is null || _watching is null || _watching.Length == 0)
        {
            return Vector2.Zero;
        }

        float leftmost = float.MaxValue;
        float rightmost = float.MinValue;
        float highest = float.MaxValue;
        float lowest = float.MinValue;

        foreach (int slot in _watching)
        {
            Vector2 at = ToPixels(recording.PositionAt(_stage.Seconds, slot));

            leftmost = Mathf.Min(leftmost, at.X);
            rightmost = Mathf.Max(rightmost, at.X);
            highest = Mathf.Min(highest, at.Y);
            lowest = Mathf.Max(lowest, at.Y);
        }

        return new Vector2((leftmost + rightmost) / 2f, (highest + lowest) / 2f);
    }

    private Vector2 Actor(int seat)
    {
        if (seat >= _stage.Planners.Length)
        {
            return Vector2.Zero;
        }

        SeatPlanner planner = _stage.Planners[seat];
        Mole? actor = planner.Actor;

        if (actor is null)
        {
            return Vector2.Zero;
        }

        RoundRecording? recording = _stage.Recording;

        if (_stage.Planning)
        {
            // Follows where the mole has been steered to, not where it is standing. The camera
            // has to travel with the stick or the player walks their mole off their own screen.
            return ToPixels(planner.PlannedPosition);
        }

        if (recording is null)
        {
            return ToPixels(actor.Position);
        }

        int slot = SlotOf(actor);

        if (slot < 0)
        {
            return ToPixels(actor.Position);
        }

        // An actor that has gone out is followed to where it went out, not past it.
        int tick = _stage.ExitTick.Length > slot && _stage.ExitTick[slot] >= 0
            ? Mathf.Min(_stage.Tick, _stage.ExitTick[slot])
            : _stage.Tick;

        return ToPixels(recording.PositionOf(tick, slot));
    }

    /// <summary>
    /// Keeps the void past the edge of the map off the screen, except upwards.
    /// </summary>
    /// <remarks>
    /// The sky is painted rather than being part of the map, so the camera is free to rise
    /// above the map's top edge and the pane simply shows more air. It has to be: the map's
    /// generated surface sits about a quarter of the way down, which is less headroom than a
    /// pane wants, and clamping to the map top ate the whole sky bias and left half of every
    /// pane looking at undug soil.
    /// </remarks>
    private Vector2 Clamped(Vector2 focus)
    {
        float cell = _scale / WorldScale.CellsPerMetre;
        float worldWidth = _stage.MapWidthCells * cell;
        float worldHeight = _stage.MapHeightCells * cell;
        float halfWidth = Size.X / 2f;
        float halfHeight = Size.Y / 2f;
        float ceiling = -halfHeight;

        return new Vector2(
            Mathf.Clamp(focus.X, halfWidth, Mathf.Max(halfWidth, worldWidth - halfWidth)),
            Mathf.Clamp(focus.Y, ceiling, Mathf.Max(ceiling, worldHeight - halfHeight)));
    }

    private Vector2 Offset() => (Size / 2f) - _cameraAt;

    private Vector2 ToPixels(Vec2 metres) =>
        new Vector2((float)metres.X.ToDecimal(), (float)metres.Y.ToDecimal()) * _scale;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        // The ground and the sky are drawn by the skin, which sits behind this node so that its
        // shader does not get applied to sixteen moles and a HUD as well.
        _skin.Cover(MapOnPane());

        // Everything below is in world pixels; the transform puts them on the pane, and
        // ClipContents keeps them inside it.
        DrawSetTransform(Offset(), 0, Vector2.One);

        DrawLava();
        DrawPlacements();
        DrawCrates();

        if (!_stage.Planning && _stage.Recording is not null)
        {
            DrawReplay(_stage.Recording, _stage.Result!);
        }
        else
        {
            DrawStandingMoles();
            DrawPlans();
        }

        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawGauges();
        DrawFrame();
        DrawBroadcast();
    }

    /// <summary>
    /// The broadcast furniture: viewfinder corners, a live tally light, which camera this is, and
    /// a caption saying whose fight is in shot.
    /// </summary>
    /// <remarks>
    /// Sports television, and not only for the look of it. A replay that cuts to two cameras needs
    /// to say that it has, or the second pane reads as a rendering fault; and a player watching
    /// four moles they cannot control needs to find their own in a hurry. The corner marks say
    /// "this is a frame somebody chose", the tally says "this is happening", and the caption says
    /// "yours is in this one".
    ///
    /// Wordless, because the design is wordless everywhere: no camera names, and the camera number
    /// is pips rather than a numeral, since the one numeral the design keeps is spent on damage.
    /// </remarks>
    private void DrawBroadcast()
    {
        if (_watching is null)
        {
            return;
        }

        Color ink = new Color(Palette.Ink, 0.6f);
        float inset = Mathf.Max(Mathf.Min(Size.X, Size.Y) * 0.028f, 8f);
        float reach = Mathf.Max(Mathf.Min(Size.X, Size.Y) * 0.06f, 16f);

        DrawRect(new Rect2(Vector2.Zero, Size), ink, false, SplitLayout.Gutter);
        DrawViewfinder(ink, inset, reach);
        DrawTally(inset, reach);
        DrawCaption(inset, reach);
    }

    /// <summary>Crop marks in the corners, which is what tells the eye a frame was composed.</summary>
    private void DrawViewfinder(Color ink, float inset, float reach)
    {
        for (int corner = 0; corner < 4; corner++)
        {
            bool left = (corner & 1) == 0;
            bool top = corner < 2;
            float x = left ? inset : Size.X - inset;
            float y = top ? inset : Size.Y - inset;

            DrawLine(
                new Vector2(x, y),
                new Vector2(left ? x + reach : x - reach, y), ink, 3f);
            DrawLine(
                new Vector2(x, y),
                new Vector2(x, top ? y + reach : y - reach), ink, 3f);
        }
    }

    /// <summary>The tally light, and which camera this is.</summary>
    private void DrawTally(float inset, float reach)
    {
        float radius = Mathf.Max(Mathf.Min(Size.X, Size.Y) * 0.012f, 4f);
        Vector2 at = new Vector2(inset + (reach * 0.4f), inset + (reach * 0.4f));

        // Pulsing. A steady red dot is a bullet hole; a pulsing one is a camera that is live.
        float beat = (Mathf.Sin((float)_stage.Seconds.ToDecimal() * 6.5f) + 1f) / 2f;

        DrawCircle(at, radius, new Color(Palette.Damage, 0.4f + (0.6f * beat)));

        for (int pip = 0; pip <= _camera; pip++)
        {
            DrawRect(
                new Rect2(
                    at.X + (radius * 2.1f) + (pip * radius * 1.5f),
                    at.Y - (radius * 0.55f),
                    radius * 0.6f,
                    radius * 1.1f),
                new Color(Palette.Ink, 0.5f));
        }
    }

    /// <summary>
    /// Whose fight this camera is on, in platoon colours. A broadcast caption with no words in it.
    /// </summary>
    private void DrawCaption(float inset, float reach)
    {
        int seats = _stage.Planners.Length;
        bool[] present = new bool[seats];
        int shown = 0;

        foreach (int slot in _watching!)
        {
            int seat = _stage.Match.Moles[slot].Seat;

            if (seat >= 0 && seat < seats && !present[seat])
            {
                present[seat] = true;
                shown++;
            }
        }

        if (shown == 0)
        {
            return;
        }

        float glyph = Mathf.Max(Mathf.Min(Size.X, Size.Y) * 0.045f, 13f);
        float pad = glyph * 0.34f;
        float height = glyph + (pad * 2f);

        // Clear of the bottom-left crop mark, which the caption sat on top of at first.
        Rect2 bar = new Rect2(
            inset + reach + pad,
            Size.Y - inset - height,
            (shown * (glyph + pad)) + pad,
            height);

        DrawRect(bar, new Color(Palette.Panel, 0.85f));

        float x = bar.Position.X + pad + (glyph / 2f);

        for (int seat = 0; seat < seats; seat++)
        {
            if (!present[seat])
            {
                continue;
            }

            Glyphs.Mole(
                this, new Vector2(x, bar.Position.Y + (height / 2f)), glyph, Palette.Seat(seat));
            x += glyph + pad;
        }
    }

    /// <summary>
    /// This platoon's own instruments, in one strip along the top of its own pane.
    /// </summary>
    /// <remarks>
    /// Time and puff are the whole planning decision, so they go where the player is already
    /// looking rather than into a shared strip they would have to find their own row in. With
    /// four players planning at once, a global gauge could only ever belong to one of them.
    ///
    /// All of it along the top, and none of it in the corners. In a four-way split the corners
    /// of all four panes meet in the middle of the screen, which is where the shared clock and
    /// the tally live, so anything put in a corner ends up on top of them.
    /// </remarks>
    private void DrawGauges()
    {
        SeatPlanner? planner = Gauged();

        if (planner is null || !_stage.Planning || planner.Actor is null)
        {
            return;
        }

        float pad = Padding(Size.Y);
        float barHeight = BarHeight(Size.Y);
        float height = (barHeight * 2f) + (pad * 3f);

        // Sized to the strip rather than to the pane, so nothing pokes out of the panel it is
        // supposed to be sitting on.
        float glyph = height * 0.8f;
        float tokens = glyph * 0.8f * Mathf.Max(planner.ResetsLeft, 1);
        float barWidth = Mathf.Min(Size.X - (glyph * 1.4f) - tokens - (pad * 6f), 240f);
        Color seat = Palette.Seat(planner.Seat);

        // A panel behind them. A bar drawn straight onto the sky is unreadable, and one drawn
        // onto soil is worse.
        float width = (glyph * 1.4f) + barWidth + tokens + (pad * 5f);
        DrawRect(new Rect2(pad, pad, width, height), Palette.Panel);

        // What is on the wheel, in the platoon's own colour ring, with how many are left.
        Vector2 wheelAt = new Vector2(pad + pad + (glyph * 0.7f), pad + (height / 2f));
        DrawArc(wheelAt, glyph * 0.5f, 0, Mathf.Tau, 28, new Color(seat, 0.6f), 2f);
        Glyphs.Weapon(this, planner.Weapon, wheelAt, glyph * 0.78f, Palette.OnPanel);
        DrawStockPips(planner.Stock(planner.Weapon), wheelAt, glyph * 0.5f);

        float left = pad + (glyph * 1.4f) + (pad * 3f);
        float first = pad + pad;
        float second = first + barHeight + pad;

        Bar(left, first, barWidth, barHeight, (float)planner.TimeSpent,
            new Color(0.306f, 0.510f, 0.651f));
        Glyphs.Time(
            this, new Vector2(left - (pad * 1.6f), first + (barHeight / 2f)),
            barHeight * 1.6f, Palette.OnPanel);

        Bar(left, second, barWidth, barHeight, (float)planner.PuffSpent,
            planner.RanOutOfPuff ? Palette.Damage : new Color(0.435f, 0.647f, 0.325f));
        Glyphs.Puff(
            this, new Vector2(left - (pad * 1.6f), second + (barHeight / 2f)),
            barHeight * 1.6f, Palette.OnPanel);

        DrawResets(planner, left + barWidth + (pad * 1.5f), pad + (height / 2f), glyph);
    }

    /// <summary>
    /// How many of the selected weapon are left, as pips around its ring.
    /// </summary>
    /// <remarks>
    /// Pips rather than a numeral, because the design spends its one numeral exception on
    /// damage. Unlimited draws nothing at all, which is the right answer: there is no count to
    /// read on the one weapon that never runs out, and an infinity mark would be a symbol to
    /// learn for no gain.
    /// </remarks>
    private void DrawStockPips(int stock, Vector2 at, float radius)
    {
        if (stock < 0)
        {
            return;
        }

        int shown = Mathf.Min(stock, 5);

        for (int pip = 0; pip < shown; pip++)
        {
            float angle = (-Mathf.Pi / 2f) + 0.45f + (pip * 0.42f);

            DrawCircle(
                at + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * 1.35f),
                radius * 0.16f, Palette.OnPanel);
        }
    }

    /// <summary>
    /// How many resets are left. The design calls this the most watched glyph on the screen, so
    /// it gets to be obvious, and the ring filling up shows the hold registering.
    /// </summary>
    private void DrawResets(SeatPlanner planner, float x, float y, float glyph)
    {
        for (int token = 0; token < planner.ResetsLeft; token++)
        {
            Vector2 at = new Vector2(x + (glyph * 0.4f) + (token * glyph * 0.8f), y);
            Glyphs.Reset(this, at, glyph * 0.75f, Palette.Damage);
        }

        if (planner.ResetHeld <= 0 || planner.ResetsLeft <= 0)
        {
            return;
        }

        Vector2 first = new Vector2(x + (glyph * 0.4f), y);
        DrawArc(
            first, glyph * 0.5f, -Mathf.Pi / 2f,
            (-Mathf.Pi / 2f) + (Mathf.Tau * (float)Mathf.Min(planner.ResetHeld, 1)),
            24, Palette.OnPanel, 3f);
    }

    private static float Padding(float paneHeight) => Mathf.Max(paneHeight * 0.025f, 5f);

    private static float BarHeight(float paneHeight) => Mathf.Max(paneHeight * 0.03f, 7f);

    /// <summary>
    /// How far down a pane its own instruments reach, so anything the whole screen shares can be
    /// placed clear of them.
    /// </summary>
    /// <remarks>
    /// Exists because the shared tally is centred horizontally, which in a two-by-two split is
    /// exactly where the vertical seam is, so it lands on top of the right-hand pane's own strip.
    /// Deriving the clearance from the same two numbers the strip is built from is what stops the
    /// two drifting apart the next time either is retuned.
    /// </remarks>
    public static float InstrumentDepth(float paneHeight) =>
        (Padding(paneHeight) * 4f) + (BarHeight(paneHeight) * 2f);

    /// <summary>Whose instruments this pane shows.</summary>
    private SeatPlanner? Gauged()
    {
        if (_seat >= 0)
        {
            return _seat < _stage.Planners.Length ? _stage.Planners[_seat] : null;
        }

        int sharing = SharedPlanSeat();

        return sharing >= 0 ? _stage.Planners[sharing] : null;
    }

    private void Bar(float x, float y, float width, float height, float fraction, Color fill)
    {
        DrawRect(new Rect2(x, y, width, height), new Color(1, 1, 1, 0.14f));
        DrawRect(new Rect2(x, y, width * fraction, height), fill);
    }

    /// <summary>Where the whole map lands on this pane, which is all the skin needs to know.</summary>
    private Rect2 MapOnPane()
    {
        float cell = _scale / WorldScale.CellsPerMetre;

        return new Rect2(
            Offset(), new Vector2(_stage.MapWidthCells * cell, _stage.MapHeightCells * cell));
    }

    private void DrawLava()
    {
        MoleMatch match = _stage.Match;

        if (match.LavaLine == Fix64.MaxValue)
        {
            return;
        }

        float cell = _scale / WorldScale.CellsPerMetre;
        float width = _stage.MapWidthCells * cell;
        float height = _stage.MapHeightCells * cell;
        float top = ToPixels(new Vec2(Fix64.Zero, match.LavaLine)).Y;

        DrawRect(new Rect2(0, top, width, height - top), Palette.Lava);

        if (match.LavaLeftEdge == Fix64.MinValue)
        {
            return;
        }

        float left = ToPixels(new Vec2(match.LavaLeftEdge, Fix64.Zero)).X;
        float right = ToPixels(new Vec2(match.LavaRightEdge, Fix64.Zero)).X;

        DrawRect(new Rect2(0, 0, left, height), Palette.Lava);
        DrawRect(new Rect2(right, 0, width - right, height), Palette.Lava);
    }

    /// <summary>
    /// Traps, snares and capped vents, as suspicious little mounds of fresh soil.
    /// </summary>
    /// <remarks>
    /// These were invisible until now, which was not a cosmetic gap but a broken rule. The design
    /// gives a trap a round's delay before it arms specifically so that it "sits there as a
    /// suspicious mound for a whole round first and opponents get to decide whether to respect it
    /// or test it". Against something nobody can see, the delay buys nothing and the trap is just a
    /// random punishment for walking somewhere.
    ///
    /// One shape for all three, in the owner's colour, because what matters is that something is
    /// there and whose it is. Which of the three it happens to be changes what it does to you but
    /// not what you should do about it, and a mound is what the design calls it.
    /// </remarks>
    private void DrawPlacements()
    {
        foreach (Placement placement in _stage.Match.Placements)
        {
            if (placement.Spent || !PlantedYet(placement))
            {
                continue;
            }

            Draw(placement);
        }
    }

    /// <summary>
    /// Whether a placement has been put down as far as the viewer has seen.
    /// </summary>
    /// <remarks>
    /// Anything from an earlier round has always been there. Anything from the round being watched
    /// appears at the tick its mole planted it, rather than sitting on the map from the first frame
    /// of a replay that has already resolved.
    /// </remarks>
    private bool PlantedYet(Placement placement)
    {
        RoundResult? result = _stage.Result;

        if (_stage.Planning || result is null || placement.PlacedOnRound < result.Round)
        {
            return true;
        }

        return _stage.Tick >= placement.PlacedOnTick;
    }

    private void Draw(Placement placement)
    {
        Vector2 at = ToPixels(placement.Position);
        float wide = MoleRadius() * 1.15f;
        float tall = wide * 0.62f;
        Color seat = Palette.Seat(placement.OwnerSeat);
        bool armed = placement.IsArmed(_stage.Match.Round);

        // Freshly disturbed soil, which is what the whole game is about looking for.
        DrawColoredPolygon(
            new[]
            {
                at + new Vector2(-wide, tall),
                at + new Vector2(-wide * 0.55f, -tall * 0.55f),
                at + new Vector2(0, -tall),
                at + new Vector2(wide * 0.55f, -tall * 0.55f),
                at + new Vector2(wide, tall),
            },
            Palette.Of(MoleSim.Terrain.Material.LooseSoil));

        // Whose it is, and whether it can catch anybody yet. A mound that is not live yet is
        // outlined; once it arms it gets a solid eye, which is the round the design gives everybody
        // to decide whether to respect it or test it.
        DrawPolyline(
            new[]
            {
                at + new Vector2(-wide, tall),
                at + new Vector2(-wide * 0.55f, -tall * 0.55f),
                at + new Vector2(0, -tall),
                at + new Vector2(wide * 0.55f, -tall * 0.55f),
                at + new Vector2(wide, tall),
            },
            new Color(seat, armed ? 0.95f : 0.45f),
            Mathf.Max(wide * 0.16f, 1.5f));

        if (armed)
        {
            DrawCircle(at + new Vector2(0, -tall * 0.15f), wide * 0.22f, seat);
        }
    }

    private void DrawCrates()
    {
        float half = _scale * 0.3f;

        foreach (Crate crate in _stage.Match.Crates)
        {
            if (crate.Gone)
            {
                continue;
            }

            Vector2 at = ToPixels(crate.Position);

            if (!crate.HasLanded)
            {
                // The telegraph. The design wants the scramble for a crate to be something
                // everybody scheduled in advance rather than a surprise.
                for (float y = at.Y - (_scale * 7f); y < at.Y - half; y += _scale * 0.4f)
                {
                    DrawLine(
                        new Vector2(at.X, y), new Vector2(at.X, y + (_scale * 0.2f)),
                        new Color(Palette.Crate, 0.5f), 2f);
                }
            }

            Rect2 box = new Rect2(at.X - half, at.Y - half, half * 2f, half * 2f);
            DrawRect(box, crate.HasLanded ? Palette.Crate : new Color(Palette.Crate, 0.45f));
            DrawRect(box, Palette.Ink, false, 2f);
        }
    }

    private void DrawStandingMoles()
    {
        foreach (Mole mole in _stage.Match.Moles)
        {
            if (mole.IsOffDuty)
            {
                continue;
            }

            bool acting = IsActing(mole);

            // The acting mole is drawn where its owner has steered it to, and there is only ever
            // one of it. The version with a ghost drew two, and which of them was about to do
            // something was anybody's guess.
            Vector2 at = acting
                ? ToPixels(_stage.Planners[mole.Seat].PlannedPosition)
                : ToPixels(mole.Position);

            DrawMole(at, mole.Seat, mole.Pluck, acting, Owns(mole.Seat));

            // One bubble per platoon, not one per mole. A platoon has up to four of them and the
            // first version put the same picture over every one, which read as four moles all
            // saying the same thing at once rather than as a platoon saying it.
            if (mole.Index == Speaker(mole.Seat))
            {
                DrawSaying(at, mole.Seat);
            }
        }
    }

    /// <summary>
    /// Which of a platoon's moles carries its bubble.
    /// </summary>
    /// <remarks>
    /// The one taking the turn, because that is the one the player and everybody watching is already
    /// looking at. Failing that the first still standing, so a platoon between turns still has a
    /// mouth.
    /// </remarks>
    private int Speaker(int seat)
    {
        int first = -1;

        foreach (Mole mole in _stage.Match.Moles)
        {
            if (mole.Seat != seat || mole.IsOffDuty)
            {
                continue;
            }

            if (IsActing(mole))
            {
                return mole.Index;
            }

            if (first < 0)
            {
                first = mole.Index;
            }
        }

        return first;
    }

    private bool IsActing(Mole mole)
    {
        if (mole.Seat >= _stage.Planners.Length)
        {
            return false;
        }

        SeatPlanner planner = _stage.Planners[mole.Seat];

        return planner.Actor == mole && ShowsPlanOf(planner);
    }

    /// <summary>
    /// Whether this pane is the one showing a given platoon's turn: its own, or the one being
    /// taken right now if everybody is sharing the screen.
    /// </summary>
    private bool ShowsPlanOf(SeatPlanner planner) => Owns(planner.Seat);

    /// <summary>
    /// Whether a platoon is the one this pane belongs to.
    /// </summary>
    /// <remarks>
    /// Its own seat where it has one. Where it does not, the platoon holding the pointer, which is
    /// what a hotseat prototype on one mouse actually is. A replay camera belongs to a piece of the
    /// action rather than to anybody, so it owns nothing and nothing is private to it.
    /// </remarks>
    private bool Owns(int seat) =>
        _seat >= 0 ? seat == _seat : seat == SharedPlanSeat();

    private void DrawReplay(RoundRecording recording, RoundResult result)
    {
        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            if (recording.IsOffDutyAt(_stage.Tick, slot))
            {
                DrawExit(recording, slot);
                continue;
            }

            int seat = _stage.Match.Moles[slot].Seat;

            DrawMole(
                ToPixels(recording.PositionAt(_stage.Seconds, slot)),
                seat,
                recording.PluckOf(_stage.Tick, slot),
                highlight: false,
                ours: Owns(seat));
        }

        float shotRadius = Mathf.Max((float)Projectile.Radius.ToDecimal() * _scale, 3f);

        foreach (Vec2 shot in recording.ShotsAt(_stage.Tick))
        {
            DrawCircle(ToPixels(shot), shotRadius, Palette.Ink);
        }

        DrawDamageNumbers(recording, result);
    }

    /// <summary>
    /// Damage numbers, which rise from where they landed and then get out of the way. Digits
    /// are the one numeral the design keeps, because they read the same in every language.
    /// </summary>
    private void DrawDamageNumbers(RoundRecording recording, RoundResult result)
    {
        int upTo = Mathf.Min(recording.HitsUpTo(_stage.Tick), _stage.HitTick.Length);
        float size = Mathf.Max(_scale * 0.55f, 14f);

        for (int index = 0; index < upTo; index++)
        {
            int age = _stage.Tick - _stage.HitTick[index];

            if (age > Stage.DamageNumberTicks)
            {
                continue;
            }

            BlastHit hit = result.Hits[index];
            int slot = SlotOf(hit.Seat, hit.MoleIndex);

            if (slot < 0)
            {
                continue;
            }

            float life = age / (float)Stage.DamageNumberTicks;
            Vector2 at = ToPixels(recording.PositionOf(_stage.HitTick[index], slot))
                + new Vector2(-size * 0.35f, -(_scale * 0.9f) - (life * _scale * 0.7f));

            DrawString(
                ThemeDB.FallbackFont, at, hit.Damage.ToString(),
                HorizontalAlignment.Left, -1, (int)size,
                new Color(Palette.Damage, 1f - (life * life)));
        }
    }

    /// <summary>
    /// Plays whichever exit the simulation chose. All eight of them are drawn in
    /// <see cref="ExitReel"/>; this only works out where and how far through.
    /// </summary>
    private void DrawExit(RoundRecording recording, int slot)
    {
        int exitTick = _stage.ExitTick.Length > slot ? _stage.ExitTick[slot] : -1;

        if (exitTick < 0)
        {
            return;
        }

        Mole mole = _stage.Match.Moles[slot];
        KnockoutExit? exit = ExitOf(mole);

        if (exit is null)
        {
            return;
        }

        Vector2 at = ToPixels(recording.PositionOf(exitTick, slot));
        float life = Mathf.Clamp((_stage.Tick - exitTick) / (float)Stage.ExitTicks, 0f, 1f);
        float radius = MoleRadius();
        Color colour = Palette.Seat(mole.Seat);

        ExitReel.Play(this, exit.Value, at, radius, colour, life);
    }

    private float MoleRadius() => (float)MatchSettings.Radius.ToDecimal() * _scale;

    /// <summary>
    /// One mole, with a pluck bar over its head only if it is one of this pane's own.
    /// </summary>
    /// <remarks>
    /// Sixteen bars on one screen is fifteen you cannot act on and one you can. A platoon's own
    /// health is worth a glance every turn; everybody else's is a row of gauges to read past, and it
    /// is the same argument as only showing a player their own plan. What everybody does get to see
    /// is the tally, which counts who is still standing, and the damage numbers, which say what a
    /// shot did the moment it lands.
    /// </remarks>
    /// <summary>
    /// Whatever this platoon is currently saying, in a bubble over its mole.
    /// </summary>
    /// <remarks>
    /// Read from the online session rather than from the simulation, because an emote is not in the
    /// simulation and must never be: it arrives out of band, on this client's own clock, and changes
    /// nothing. Drawing it from here is the whole of its effect on the game.
    /// </remarks>
    private void DrawSaying(Vector2 at, int seat)
    {
        if (Online.Match is not Molehill.Online.OnlineMatch online)
        {
            return;
        }

        if (online.Chat.From(seat, online.Elapsed) is not Molehill.Online.Said said)
        {
            return;
        }

        // Generous, because at gameplay zoom a mole is about forty pixels and a picture scaled to
        // match it is a smudge. The bubble is the one thing on the map that has to be readable from
        // across a room.
        float size = Mathf.Max(_scale * 1.6f, 46f);
        Vector2 bubble = at - new Vector2(0, (_scale * 0.9f) + (size * 0.62f));

        DrawCircle(bubble, size * 0.62f, Palette.Panel);
        DrawArc(bubble, size * 0.62f, 0, Mathf.Tau, 24, new Color(Palette.OnPanel, 0.5f), 2f);

        // A tail, so a bubble over a crowd still belongs to one mole.
        DrawColoredPolygon(
            new[]
            {
                bubble + new Vector2(-size * 0.18f, size * 0.5f),
                bubble + new Vector2(size * 0.18f, size * 0.5f),
                at - new Vector2(0, _scale * 0.55f),
            },
            Palette.Panel);

        Glyphs.Say(this, said.Emote, bubble, size * 0.85f, Palette.Seat(seat));
    }

    private void DrawMole(Vector2 at, int seat, int pluck, bool highlight, bool ours)
    {
        float radius = MoleRadius();
        Color colour = Palette.Seat(seat);

        if (highlight)
        {
            DrawCircle(at, radius * 1.6f, new Color(colour, 0.3f));
        }

        DrawCircle(at, radius, colour);
        DrawCircle(at + new Vector2(-radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(0, radius * 0.34f), radius * 0.34f, Palette.Snout);

        if (!ours)
        {
            return;
        }

        // Pluck as a bar over the head. A number there would be unreadable at this size, and
        // wordless is the direction anyway.
        float width = radius * 2.2f;
        float top = at.Y - (radius * 2.1f);
        float thickness = Mathf.Max(radius * 0.28f, 2f);

        DrawRect(new Rect2(at.X - (width / 2f), top, width, thickness), new Color(0, 0, 0, 0.3f));
        DrawRect(
            new Rect2(at.X - (width / 2f), top, width * (pluck / 100f), thickness), colour);
    }

    // ---- The plans ------------------------------------------------------------------

    private void DrawPlans()
    {
        foreach (SeatPlanner planner in _stage.Planners)
        {
            // A player sees only their own plan. Everybody planning at once only works if
            // nobody can read anybody else's off the screen, which is the same reason the
            // online version hides which mole is even acting.
            if (planner.Actor is not null && ShowsPlanOf(planner))
            {
                DrawPlan(planner);
            }
        }
    }

    /// <summary>
    /// In a shared view only one plan can be shown without giving the game away, so it is the
    /// one still being laid. Hotseat on one mouse is exactly this case.
    /// </summary>
    private int SharedPlanSeat()
    {
        foreach (SeatPlanner planner in _stage.Planners)
        {
            if (planner.IsPlanning)
            {
                return planner.Seat;
            }
        }

        return -1;
    }

    /// <summary>
    /// What the player has booked so far. Not the route.
    /// </summary>
    /// <remarks>
    /// The walked path is deliberately not drawn. It was, once, as a line with a ghost looping
    /// along it, and it was the single most confusing thing on the screen: a first-time player
    /// could not say which mole was theirs, and the line looked like a thing to be edited rather
    /// than a record of where they had already been. The mole is where it walked to; that is the
    /// whole story, and the gauges say what it cost. What is left here is only the things that
    /// happen at a moment, marked where that moment was.
    /// </remarks>
    private void DrawPlan(SeatPlanner planner)
    {
        Color seat = Palette.Seat(planner.Seat);
        float radius = MoleRadius();
        float marker = Mathf.Max(radius * 2.2f, 18f);

        // Hops, marked where they were booked. A hop is scheduled at a moment rather than at a
        // place, so the marker records where the mole was when the button went down.
        foreach (PlanAction hop in planner.Hops)
        {
            Vector2 at = ToPixels(planner.HopPosition(hop)) + new Vector2(0, -radius * 1.4f);

            DrawCircle(at, marker * 0.5f, new Color(Palette.Paper, 0.75f));
            Glyphs.Hop(this, at, marker * 0.9f, seat);
        }

        // The charge, where it was dropped rather than where the mole has since walked to.
        // Plant, run, regret, and knowing exactly where you left it is the whole difference
        // between the first two and the third.
        if (planner.Charge is not null)
        {
            Vector2 planted = ToPixels(planner.ChargeAt) + new Vector2(0, radius * 0.6f);

            Glyphs.Dynamite(this, planted, radius * 1.6f, Palette.Damage);
            DrawArc(
                planted, (float)WeaponTable.Of(WeaponId.BoomBeets).BlastRadius.ToDecimal() * _scale,
                0, Mathf.Tau, 40, new Color(Palette.Damage, 0.28f), 2f);
        }

        DrawAim(planner);
    }

    /// <summary>
    /// Where the shot goes, from where the mole will be standing when it fires rather than
    /// from where it is standing now. Without the outline marking that spot the arrow looks
    /// like it belongs to nobody.
    /// </summary>
    private void DrawAim(SeatPlanner planner)
    {
        Vec2 heading = planner.AimHeading;

        if (heading.LengthSquared() == Fix64.Zero)
        {
            return;
        }

        ChargeArrow(
            ToPixels(planner.PlannedPosition),
            new Vector2((float)heading.X.ToDecimal(), (float)heading.Y.ToDecimal()),
            (float)planner.AimCharge,
            planner.Aiming ? Palette.Aiming : Palette.Damage);
    }

    /// <summary>
    /// The aim: an arrow of fixed length that fills up as the throw charges.
    /// </summary>
    /// <remarks>
    /// Both halves of this used to scale the arrow's length by the power, which is the same mistake
    /// the wind gauge made and was fixed for: a gauge scaled by its own value is unreadable exactly
    /// where reading it matters, so a fifth-power shot drew a stub half a mole long and a player
    /// could not tell a soft throw from a mis-click. The track is now the same length every time and
    /// the charge is the fill, which is what a progress bar is for.
    ///
    /// It deliberately does not predict where the shell lands. The arc could be drawn honestly,
    /// since the projectile solver is deterministic and would run against a terrain copy the way
    /// the movement preview does, but an artillery game whose shots are pre-plotted is a different
    /// game. Direction and charge are what the player chose; where it ends up is the round's answer.
    /// </remarks>
    private void ChargeArrow(Vector2 from, Vector2 direction, float charge, Color ink)
    {
        float radius = MoleRadius();
        float length = Mathf.Max(_scale * ArrowMetres, 44f);
        float thickness = Mathf.Max(radius * 0.44f, 4f);
        Vector2 tip = from + (direction * length);
        Vector2 across = new Vector2(-direction.Y, direction.X) * thickness * 1.5f;
        bool full = charge >= 0.999f;

        // The muzzle ring, so an arrow in the middle of a scrum plainly belongs to a mole.
        DrawArc(from, radius, 0, Mathf.Tau, 20, ink, radius * 0.16f);

        // The empty track first, then however much of it is charged. The track is what makes the
        // fill legible: without it there is nothing for the fill to be a fraction of.
        DrawLine(from, tip, new Color(ink, 0.2f), thickness);

        if (charge > 0f)
        {
            DrawLine(from, from + (direction * length * charge), ink, thickness);
        }

        // The head fills in only at maximum, which is the one moment worth calling out: it is the
        // difference between a hard throw and the hardest one available.
        Vector2[] head =
        {
            tip + (direction * thickness * 2.1f),
            tip + across,
            tip - across,
        };

        if (full)
        {
            DrawColoredPolygon(head, ink);
            return;
        }

        DrawColoredPolygon(head, new Color(ink, 0.2f));
        DrawPolyline(
            new[] { head[0], head[1], head[2], head[0] }, new Color(ink, 0.55f), 2f);
    }

    /// <summary>
    /// How long the aim arrow is, in metres, whatever the charge. Long enough that a tenth of it
    /// is visible, short enough not to cover the mole it is aimed at.
    /// </summary>
    private const float ArrowMetres = 3.4f;

    /// <summary>
    /// A border in the platoon's colour, so four views never get mistaken for each other.
    /// </summary>
    private void DrawFrame()
    {
        if (_seat < 0)
        {
            return;
        }

        Rect2 edge = new Rect2(Vector2.Zero, Size);
        DrawRect(edge, Palette.Seat(_seat), false, SplitLayout.Gutter);

        // Dimmed when this platoon has already committed, so at a glance the table can see
        // who everybody is still waiting for.
        if (_seat < _stage.Planners.Length
            && _stage.Planning
            && _stage.Planners[_seat].Committed)
        {
            DrawRect(edge, new Color(Palette.Ink, 0.35f));
        }
    }

    // ---- Lookups --------------------------------------------------------------------

    private int SlotOf(Mole mole) => SlotOf(mole.Seat, mole.Index);

    private int SlotOf(int seat, int moleIndex)
    {
        IReadOnlyList<Mole> moles = _stage.Match.Moles;

        for (int slot = 0; slot < moles.Count; slot++)
        {
            if (moles[slot].Seat == seat && moles[slot].Index == moleIndex)
            {
                return slot;
            }
        }

        return -1;
    }

    private KnockoutExit? ExitOf(Mole mole)
    {
        if (_stage.Result is null)
        {
            return null;
        }

        foreach (Knockout knockout in _stage.Result.Knockouts)
        {
            if (knockout.Seat == mole.Seat && knockout.MoleIndex == mole.Index)
            {
                return knockout.Exit;
            }
        }

        return null;
    }
}
