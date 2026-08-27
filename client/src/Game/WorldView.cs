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

    public WorldView(Stage stage)
    {
        _stage = stage;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
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
        // The sky, painted rather than mapped, so a pane is never looking at nothing and the
        // camera is free to rise above the top of the map.
        DrawRect(new Rect2(Vector2.Zero, Size), Palette.Paper);

        // Everything below is in world pixels; the transform puts them on the pane, and
        // ClipContents keeps them inside it.
        DrawSetTransform(Offset(), 0, Vector2.One);

        DrawGround();
        DrawLava();
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

    private void DrawGround()
    {
        float cell = _scale / WorldScale.CellsPerMetre;

        DrawTextureRect(
            _stage.Terrain,
            new Rect2(0, 0, _stage.MapWidthCells * cell, _stage.MapHeightCells * cell),
            false);
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

            DrawMole(at, mole.Seat, mole.Pluck, acting);
        }
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
    private bool ShowsPlanOf(SeatPlanner planner) =>
        _seat >= 0 ? planner.Seat == _seat : planner.Seat == SharedPlanSeat();

    private void DrawReplay(RoundRecording recording, RoundResult result)
    {
        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            if (recording.IsOffDutyAt(_stage.Tick, slot))
            {
                DrawExit(recording, slot);
                continue;
            }

            DrawMole(
                ToPixels(recording.PositionAt(_stage.Seconds, slot)),
                _stage.Match.Moles[slot].Seat,
                recording.PluckOf(_stage.Tick, slot),
                highlight: false);
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
    /// Two of the eight exits, roughed in. The reel is chosen in the simulation, so this only
    /// plays what it was told and anything without an animation yet gets the default.
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

        if (exit.Value == KnockoutExit.StretcherSquad)
        {
            // Carried off, waving weakly, by two worms and a very small stretcher.
            Vector2 carried = at + new Vector2(life * radius * 3.5f, -life * radius * 0.5f);

            DrawLine(
                carried + new Vector2(-radius, radius * 0.6f),
                carried + new Vector2(radius, radius * 0.6f), Palette.Ink, radius * 0.22f);
            DrawCircle(carried, radius * 0.75f, colour);
            DrawCircle(carried + new Vector2(-radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
            DrawCircle(carried + new Vector2(radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
            return;
        }

        // Spin and poof: spins faster, shrinks to nothing, and goes in a puff of dust that
        // leaves its boots standing. Without the dust it just gets smaller, which reads as a
        // rendering fault rather than as a joke.
        if (life < 1f)
        {
            DrawArc(
                at, radius * (0.6f + (life * 2.2f)), 0, Mathf.Tau, 28,
                new Color(Palette.Dust, 1f - life), radius * 0.3f);
        }

        Vector2 boot = new Vector2(radius * 0.5f, radius * 0.55f);

        DrawRect(new Rect2(at.X - boot.X - 1, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);
        DrawRect(new Rect2(at.X + 1, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);

        DrawCircle(
            at + new Vector2(Mathf.Cos(life * 24f) * radius * 0.4f, -life * radius * 1.6f),
            Mathf.Lerp(radius, 0f, life),
            colour);
    }

    private float MoleRadius() => (float)MatchSettings.Radius.ToDecimal() * _scale;

    private void DrawMole(Vector2 at, int seat, int pluck, bool highlight)
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

        if (planner.Bracing)
        {
            Vector2 at = ToPixels(planner.PlannedPosition) + new Vector2(0, -radius * 2.4f);

            DrawCircle(at, marker * 0.5f, new Color(Palette.Paper, 0.75f));
            Glyphs.Brace(this, at, marker * 0.85f, seat);
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
        Vector2 muzzle = ToPixels(planner.PlannedPosition);
        float radius = MoleRadius();

        if (planner.Aiming)
        {
            DrawArc(muzzle, radius, 0, Mathf.Tau, 20, Palette.Aiming, radius * 0.16f);
            DrawLine(muzzle, ToPixels(planner.AimAt), Palette.Aiming, radius * 0.2f);
            DrawCircle(ToPixels(planner.AimAt), radius * 0.45f, Palette.Aiming);
            return;
        }

        if (planner.Shot is null)
        {
            return;
        }

        Vec2 aim = planner.Shot.Value.AimDirection();
        Vector2 direction = new Vector2((float)aim.X.ToDecimal(), (float)aim.Y.ToDecimal());
        float length = _scale * 2.6f * (planner.Shot.Value.Power / 255f);
        Vector2 tip = muzzle + (direction * length);
        Vector2 across = new Vector2(-direction.Y, direction.X) * radius * 0.45f;

        DrawArc(muzzle, radius, 0, Mathf.Tau, 20, Palette.Damage, radius * 0.16f);
        DrawLine(muzzle, tip, Palette.Damage, radius * 0.2f);
        DrawColoredPolygon(
            new[] { tip + (direction * radius * 0.8f), tip + across, tip - across },
            Palette.Damage);
    }

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
