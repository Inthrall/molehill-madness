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
    private readonly Garden _garden;

    public WorldView(Stage stage)
    {
        _stage = stage;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;

        // Both behind this node's own drawing, and the garden after the skin, so the order down
        // the screen is ground, then what is standing on it, then everything that moves.
        _skin = new TerrainSkin(stage);
        AddChild(_skin);

        _garden = new Garden(stage);
        AddChild(_garden);
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

        // Whoever is acting has changed, so the camera goes to them. It keeps the zoom the player
        // chose, which is the whole point: a player who has pinched in to line up a shot wants to be
        // taken to the next mole at that magnification, not zoomed back out to a wide shot. Only the
        // pan lock is released; _zoom is left alone.
        int acting = ActingSlot();

        if (acting != _acting)
        {
            _acting = acting;
            _manual = false;
        }

        // A mole actually walking takes the camera back, whatever the player did with it before.
        // Panning used to take a camera outright until the turn passed on, so a player who dragged
        // the map to look at somebody else and then walked their own mole watched an empty piece of
        // dirt while the mole left the pane. Steering is a clear statement about what you want to be
        // looking at, and it beats a drag from ten seconds ago.
        if (Walking())
        {
            _manual = false;

            // And it cannot be so far out that the thing being steered is a speck. Raised once,
            // here, rather than clamped every frame further down: a floor on the scale alone would
            // pump the view in as the mole moved and back out as it stopped, which is worse to look
            // at than either end of it. The player's zoom is overridden the moment they walk, and
            // then it stays where it was put.
            if (_zoom < WalkingZoomFloor)
            {
                _zoom = WalkingZoomFloor;
            }
        }

        _base = pane.PixelsPerMetre;
        _pushing = PushWeight();
        _scale = Mathf.Max(
            _base * _zoom * Mathf.Lerp(1f, PushIn, _pushing), Filling());
        Chase(delta);
        QueueRedraw();
    }

    /// <summary>
    /// Whether the mole this pane is planning has moved since the last frame.
    /// </summary>
    /// <remarks>
    /// Off the walk's own tick count rather than off whether a key is down, which is the distinction
    /// that matters. The mole is what the camera should follow, so the question is whether the mole
    /// went anywhere: holding a direction against a wall is not movement, and neither is panning,
    /// zooming or thinking. A player who drags the map and then keeps dragging it is left alone.
    /// </remarks>
    private bool Walking()
    {
        int slot = ActingSlot();

        if (slot < 0 || slot >= _stage.Match.Moles.Count)
        {
            _walked = -1;
            return false;
        }

        SeatPlanner planner = _stage.Planners[_stage.Match.Moles[slot].Seat];
        int used = planner.Walk?.TicksUsed ?? 0;
        bool moved = _walked >= 0 && used != _walked;

        _walked = used;
        return moved;
    }

    /// <summary>Ticks the followed walk had used last frame, or -1 when nobody is being planned.</summary>
    private int _walked = -1;

    /// <summary>
    /// The furthest out a camera may sit while its own mole is being walked.
    /// </summary>
    /// <remarks>
    /// One, which is the framing the layout chose for this pane rather than a number of its own, so
    /// a quarter-screen pane still gets a quarter-screen view and only the pinching is undone. A
    /// player may still zoom in as far as they like while walking; it is only the way out that is
    /// shut, and only while the mole is moving.
    /// </remarks>
    private const float WalkingZoomFloor = 1f;

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

    /// <summary>
    /// The closest the camera may be to the map and still cover the pane across.
    /// </summary>
    /// <remarks>
    /// There was a strip of nothing down the right hand side of the map, and it was neither the map
    /// nor a rendering fault: the camera was simply further out than the map is wide. The map is
    /// sixty-two and a half metres across, so a pane 1280 pixels wide needs at least twenty and a
    /// half pixels to the metre to be covered, and both zoom floors sat under that. The manual one
    /// is half of normal framing, which is twenty; the replay director's furthest is thirteen, which
    /// leaves nearly four hundred and seventy pixels of backdrop showing.
    ///
    /// Across only. Off the top of the map is sky and the camera is deliberately allowed to rise
    /// into it, which is why the gap only ever appeared on one axis.
    /// </remarks>
    private float Filling()
    {
        float metres = _stage.MapWidthCells / (float)WorldScale.CellsPerMetre;

        return metres > 0f ? Size.X / metres : 0f;
    }

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
    /// <summary>
    /// Which mole is being planned in this pane, as a slot, or -1 when none is.
    /// </summary>
    /// <remarks>
    /// A slot rather than a position, because this is asked every frame only to notice when it
    /// changes, and a position changes constantly while a mole is being steered.
    /// </remarks>
    private int ActingSlot()
    {
        if (!_stage.Planning)
        {
            return -1;
        }

        int seat = _seat >= 0 ? _seat : SharedPlanSeat();

        if (seat < 0 || seat >= _stage.Planners.Length)
        {
            return -1;
        }

        Mole? actor = _stage.Planners[seat].Actor;

        return actor is null ? -1 : SlotOf(actor);
    }

    private int _acting = -1;

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

        // In a shared view, whoever is actually holding the pen. It used to be the middle of every
        // platoon's actor at once, which on one pointer is the middle of three moles nobody can move
        // and one they can: the mole being steered sat off to one side of its own turn.
        int planning = SharedPlanSeat();

        if (planning >= 0 && planning < _stage.Planners.Length
            && _stage.Planners[planning].Actor is not null)
        {
            return Actor(planning);
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
    ///
    /// Free, but not unbounded, which it was: the ceiling was a whole pane above the map's top
    /// edge, so one could scroll up until the screen was nothing but painted sky with the field
    /// somewhere off the bottom. There is no reason to look at that and it reads as the map having
    /// fallen out of the window. A fixed amount of air above the top edge instead, in metres so it
    /// is the same amount of sky at every zoom.
    ///
    /// The two vertical limits cross at the widest zoom, and that is deliberately handled rather
    /// than clamped away. The map is twice as wide as it is tall, so a pane wide enough to hold all
    /// of it is taller than the map: the highest the camera may sit is then below the lowest, and a
    /// naive clamp pins the camera to whichever it names first. Both bounds are ordered before
    /// clamping, so instead of pinning, the pane keeps the small amount of travel between them.
    /// </remarks>
    private Vector2 Clamped(Vector2 focus)
    {
        float cell = _scale / WorldScale.CellsPerMetre;
        float worldWidth = _stage.MapWidthCells * cell;
        float worldHeight = _stage.MapHeightCells * cell;
        float halfWidth = Size.X / 2f;
        float halfHeight = Size.Y / 2f;
        float ceiling = halfHeight - (SkyRoomMetres * _scale);
        float bed = worldHeight - halfHeight;

        return new Vector2(
            Mathf.Clamp(focus.X, halfWidth, Mathf.Max(halfWidth, worldWidth - halfWidth)),
            Mathf.Clamp(focus.Y, Mathf.Min(ceiling, bed), Mathf.Max(ceiling, bed)));
    }

    /// <summary>Air the camera may show above the map's top edge, in metres.</summary>
    private const float SkyRoomMetres = 8f;

    private Vector2 Offset() => (Size / 2f) - _cameraAt;

    private Vector2 ToPixels(Vec2 metres) =>
        new Vector2((float)metres.X.ToDecimal(), (float)metres.Y.ToDecimal()) * _scale;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        // The ground and the backdrop are drawn by the skin, which sits behind this node so that
        // its shader does not get applied to sixteen moles and a HUD as well. The garden sits
        // between the two for the same sort of reason: it wants a texture filter this node does not.
        _skin.Cover(MapOnPane());
        _garden.Cover(Offset(), _scale);

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
            DrawDiggings();
            DrawStandingMoles();
            DrawPlans();
        }

        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawWhoseTurn();
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
        // A replay camera, and a replay actually rolling. The first was the only test, and it let
        // the crop marks and the tally light sit over the hold after the replay had ended.
        if (_watching is null || !_stage.Replaying)
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
        float dial = barHeight * 2.2f;

        // One bar and one dial, on the map rather than on a panel. What used to sit here was a
        // panel carrying the loaded weapon with its stock pips, two bars, and the reset tokens, and
        // three of those four were already on the key strip along the bottom: the same weapon, the
        // same reset. Two readouts of one number invite the eye to check they agree, which is work
        // for no information.
        //
        // What is left is the two things that are only here: how much puff this turn has left, and
        // how much of the eight seconds is unspent. No plate behind them, which does cost some
        // legibility over pale sky, so the bar keeps a dark surround of its own and the dial is
        // drawn as a ring rather than as a wedge on nothing.
        float barWidth = Mathf.Min(Size.X - dial - (pad * 5f), 240f);
        float left = pad + dial + (pad * 2f);
        float centre = pad + (dial / 2f);

        Clock(new Vector2(pad + (dial / 2f), centre), dial / 2f, 1f - (float)planner.TimeSpent);

        Bar(left, centre - (barHeight / 2f), barWidth, barHeight, (float)planner.PuffSpent,
            planner.RanOutOfPuff ? Palette.Damage : new Color(0.435f, 0.647f, 0.325f));

        Glyphs.Puff(
            this, new Vector2(left + barWidth + (pad * 1.4f), centre),
            barHeight * 1.7f, Palette.OnPanel);
    }

    /// <summary>
    /// The turn's eight seconds, as a ring that empties.
    /// </summary>
    /// <remarks>
    /// A clock rather than the second bar this replaced. Two bars stacked in a corner were two
    /// quantities of the same shape and colour, read as one gauge with a fault, and the eight
    /// seconds is not really a quantity anyway: it is time, which everybody already knows how to
    /// read off a dial.
    ///
    /// Anticlockwise from noon, because a clock that empties has to run the way a clock runs or the
    /// shape means nothing.
    /// </remarks>
    private void Clock(Vector2 at, float radius, float left)
    {
        DrawCircle(at, radius, new Color(0f, 0f, 0f, 0.32f));
        DrawArc(at, radius * 0.86f, 0f, Mathf.Tau, 28, new Color(Palette.OnPanel, 0.28f), 2f);

        if (left > 0f)
        {
            DrawArc(
                at, radius * 0.86f, -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * Mathf.Clamp(left, 0f, 1f)), 32,
                new Color(0.306f, 0.510f, 0.651f), Mathf.Max(radius * 0.22f, 3f));
        }

        Glyphs.Time(this, at, radius * 1.15f, Palette.OnPanel);
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
    /// <summary>
    /// A large arrow bouncing over whoever is being planned, while nobody is touching anything.
    /// </summary>
    /// <remarks>
    /// Sixteen moles at a wide zoom are sixteen identical brown shapes, and the one that answers the
    /// keys is marked by nothing except a plan line it has not drawn yet. Every game of this shape
    /// has had the same answer and there is no reason to invent a different one.
    ///
    /// In screen space rather than world space, which is the part that needed thought. Drawn in the
    /// world it shrinks with the zoom, so at the zoom where one genuinely cannot tell the moles
    /// apart the arrow is a speck. Fixed pixels, sized off the pane so a quarter-screen pane does
    /// not get a quarter-screen arrow.
    ///
    /// And only while idle. An arrow that stayed up through a shot would sit over the aim line for
    /// the whole of every turn, and a thing one has to look past is worse than no thing at all.
    /// </remarks>
    private void DrawWhoseTurn()
    {
        if (!_stage.Planning || _stage.Idle < ArrowAfterSeconds)
        {
            return;
        }

        int slot = ActingSlot();

        if (slot < 0 || slot >= _stage.Match.Moles.Count)
        {
            return;
        }

        Mole mole = _stage.Match.Moles[slot];
        SeatPlanner planner = _stage.Planners[mole.Seat];
        Vector2 head = ToPixels(planner.PlannedPosition) + Offset()
            - new Vector2(0f, MoleRadius());

        // Faded up over its first moment rather than appearing, because something that appears at
        // the edge of the eye reads as a fault where something that fades in reads as a prompt.
        float shown = Mathf.Min((_stage.Idle - ArrowAfterSeconds) * 3f, 1f);
        float size = Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.14f, 40f, 110f);

        // The gap is measured from the mole rather than from the arrow, so that at a wide zoom a
        // hundred-pixel arrow does not float half a screen above the twelve-pixel mole it is
        // pointing at. Positive-only bounce, which keeps the point out of the mole's head and reads
        // as a bounce rather than a wobble.
        float gap = Mathf.Max(MoleRadius() * 0.5f, size * 0.12f);
        float bounce = Mathf.Abs(Mathf.Sin(Beat() * 0.11f)) * (size * 0.22f);
        Vector2 at = head - new Vector2(0f, (size / 2f) + gap + bounce);

        Glyphs.Attention(this, at, size, Palette.Seat(mole.Seat), shown);
    }

    /// <summary>How long a pane waits, in seconds, before it says whose turn it is.</summary>
    private const float ArrowAfterSeconds = 1.2f;

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
        // A dark surround, because this is drawn onto the map now rather than onto a panel and a
        // pale bar over pale sky is not a bar. Outset rather than inset so the fill keeps its width.
        float edge = Mathf.Max(height * 0.34f, 2f);

        DrawRect(
            new Rect2(x - edge, y - edge, width + (edge * 2f), height + (edge * 2f)),
            new Color(0f, 0f, 0f, 0.32f));

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

        // The flat fill first and the crust over it, rather than the crust alone. The strip is
        // about seven metres deep and the lava is thirty by the end of a match, so the fill is what
        // makes it a lake rather than a ribbon, and the two agree on colour because the fill's
        // colour was picked off this artwork in the first place.
        DrawRect(new Rect2(0, top, width, height - top), Palette.Lava);
        Crust(Art.LavaFloor, 0f, width, top, 0f);

        if (match.LavaLeftEdge == Fix64.MinValue)
        {
            return;
        }

        float left = ToPixels(new Vec2(match.LavaLeftEdge, Fix64.Zero)).X;
        float right = ToPixels(new Vec2(match.LavaRightEdge, Fix64.Zero)).X;

        DrawRect(new Rect2(0, 0, left, height), Palette.Lava);
        DrawRect(new Rect2(right, 0, width - right, height), Palette.Lava);

        // Turned a quarter each way, so the crust faces the shrinking middle from both sides. The
        // walls have their own strip because a wall is crust most of the way down where a floor is
        // crust on top of a lake.
        // Opposite quarter turns, so both crusts face the middle. Turned the same way, the right
        // hand strip ran up off the top of the pane and showed as a sliver over the instruments.
        Crust(Art.LavaWall, 0f, height, left, Mathf.Pi / 2f);
        Crust(Art.LavaWall, -height, 0f, right, -Mathf.Pi / 2f);
    }

    /// <summary>
    /// Lays a lava strip along an edge, tiled, at whatever angle the edge runs.
    /// </summary>
    /// <remarks>
    /// A handful of copies rather than a tiling rect, because a tiling rect repeats a texture at its
    /// own pixel size and the size wanted here is a size in the world: thirty-two metres to a tile,
    /// so the crust's waves stay about five metres across however far the camera has zoomed in.
    /// Across a sixty metre map that is three draws.
    ///
    /// The rotation goes through the canvas transform rather than into the rectangles, which is why
    /// the view's own offset has to be put back afterwards: setting a transform replaces the one the
    /// view had already set to put world pixels on the pane.
    /// </remarks>
    private void Crust(Texture2D strip, float from, float to, float along, float turn)
    {
        float tile = LavaTileCells * _scale / WorldScale.CellsPerMetre;
        float deep = tile * strip.GetHeight() / strip.GetWidth();

        DrawSetTransform(
            Offset() + (turn == 0f ? new Vector2(0f, along) : new Vector2(along, 0f)),
            turn,
            Vector2.One);

        for (float at = from; at < to; at += tile)
        {
            DrawTextureRect(strip, new Rect2(at, 0f, tile, deep), false);
        }

        DrawSetTransform(Offset(), 0, Vector2.One);
    }

    /// <summary>How much map a lava tile spans, in cells. Thirty-two metres, as for the sky.</summary>
    private const float LavaTileCells = 512f;

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
        Color seat = Palette.Seat(placement.OwnerSeat);
        bool armed = placement.IsArmed(_stage.Match.Round);

        // A heap of fresh soil while it is arming and the thing itself once it is live, which is
        // exactly what the design's round of delay is for: until it arms there is nothing to see
        // but disturbed earth, and after it arms there is no doubt what is sitting there.
        Texture2D art = Art.Object(armed ? ArmedArt(placement.Weapon) : "mound");
        float wide = art.GetWidth() / Art.MolePixelsPerMetre * _scale;
        float tall = art.GetHeight() / Art.MolePixelsPerMetre * _scale;

        DrawTextureRect(art, new Rect2(at.X - (wide / 2f), at.Y - tall, wide, tall), false);

        // Whose it is. The artwork cannot say, because a snap trap is a snap trap whoever put it
        // down, and whose it is decides whether to walk round it or over it.
        DrawCircle(at + new Vector2(0f, -tall - (wide * 0.16f)), Mathf.Max(wide * 0.14f, 2f), seat);

        // A snare is a zone rather than an object, so it gets a ring the size of the zone. Nothing
        // else in the game says how far a snare reaches, and walking into one costs a round of
        // digging.
        if (armed && placement.Weapon == WeaponId.RootSnare)
        {
            float reach = (float)WeaponTable.Of(WeaponId.RootSnare).BlastRadius.ToDecimal() * _scale;

            Art.Effect("ring").Draw(
                this,
                new Rect2(at.X - reach, at.Y - reach, reach * 2f, reach * 2f),
                Beat() / 4,
                mirrored: false);
        }

        // A capped vent throws things upward, and an armed one is doing that whether or not
        // anybody is standing on it, so it gets its plume.
        if (armed && placement.Weapon == WeaponId.GeyserCap)
        {
            Strip plume = Art.Effect("geyser");
            Vector2 size = plume.FrameSize * (_scale / Art.MolePixelsPerMetre);

            plume.Draw(
                this,
                new Rect2(at.X - (size.X / 2f), at.Y - size.Y, size),
                Beat() / 6,
                mirrored: false);
        }
    }

    /// <summary>What a placement looks like once it is live.</summary>
    private static string ArmedArt(WeaponId weapon) => weapon switch
    {
        WeaponId.SnapTrap => "snaptrap",
        WeaponId.RootSnare => "snare",
        WeaponId.GeyserCap => "vent",
        WeaponId.Sandbag => "sandbag",
        _ => "mound",
    };

    /// <summary>
    /// The crates: where the next ones are coming down, and the ones that are already here.
    /// </summary>
    /// <remarks>
    /// Two things were wrong here and both came from taking the sprite at face value. The crate
    /// sheet is drawn small, so scaled by the sprite pitch that dresses the moles a crate came out
    /// at two fifths of a metre against a mole of three quarters, and read as a parcel. It is sized
    /// to a stated width now, and the chute frame is sized by the box drawn inside it rather than
    /// by its own width, so a crate is the same size in the air as it is on the ground. Sized by the
    /// frame it would shrink on landing: the canopy is sixty-seven pixels across and the box under it
    /// only thirty-one.
    ///
    /// And a crate can land in a cave, which is the point of the change that put it there. A
    /// parachute drawn at a spot with twelve metres of rock over it is a parachute in the rock, and
    /// worse, crates draw over the ground rather than under it, so it was plainly visible doing so.
    /// An underground delivery shows only its marker until it arrives.
    /// </remarks>
    private void DrawCrates()
    {
        // One pitch for all of the crate's art, taken from the box itself, so a chute frame drawn
        // wider than the box stays wider than the box by the amount it is drawn wider.
        float pitch = Art.Object("closed").GetWidth() / CrateMetres;

        foreach (Crate crate in _stage.Match.Crates)
        {
            if (crate.Gone)
            {
                continue;
            }

            Vector2 at = ToPixels(crate.Position);

            if (!crate.HasLanded)
            {
                // Where it is going to land, marked on the ground rather than on the crate,
                // because the ground is where the scramble happens.
                Blit(Art.Object("marker"), pitch, at, centred: true);

                if (!OpenSky(crate.Position))
                {
                    // No sky over it, so no parachute. A ghost of the box instead, because the ring
                    // on its own was a dashed ellipse drawn on some dirt and read as a rendering
                    // fault rather than as a delivery: "what is this circle" was the actual report.
                    // Half-lit, so it is plainly the promise of a crate and not one already there.
                    Blit(Art.Object("closed"), pitch, at, centred: true, showing: 0.45f);
                    continue;
                }

                // Hung from the chute rather than centred in the picture, so the box is at the
                // position the simulation says and the canopy is the part above it.
                Texture2D chute = Art.Object(ChuteArt());
                float canopy = chute.GetWidth() * ChuteBoxWidth / CrateMetres;
                float wide = chute.GetWidth() / canopy * _scale;
                float tall = chute.GetHeight() / canopy * _scale;

                DrawTextureRect(
                    chute,
                    new Rect2(at.X - (wide / 2f), at.Y - (tall * ChuteBoxMiddle), wide, tall),
                    false);

                continue;
            }

            Blit(Art.Object("closed"), pitch, at, centred: true);
        }
    }

    /// <summary>How wide a crate is in the world, which is about the girth of a mole.</summary>
    private const float CrateMetres = 0.8f;

    /// <summary>
    /// The box's share of the width of a chute frame, and where down that frame its middle sits.
    /// </summary>
    /// <remarks>
    /// Measured off <c>chute-0.png</c> rather than guessed, which is how the rest of the art's
    /// numbers are arrived at: thirty-one opaque pixels of sixty-seven across, and the box occupying
    /// rows sixty-one to eighty-six of eighty-six. The canopy and the box are drawn with a gap
    /// between them and no rigging, so neither number can be derived from the frame's own bounds.
    /// </remarks>
    private const float ChuteBoxWidth = 0.4627f;

    private const float ChuteBoxMiddle = 0.855f;

    /// <summary>Draws a texture at a stated pixels-per-metre, centred on or standing over a point.</summary>
    private void Blit(Texture2D art, float pitch, Vector2 at, bool centred, float showing = 1f)
    {
        float wide = art.GetWidth() / pitch * _scale;
        float tall = art.GetHeight() / pitch * _scale;
        float above = centred ? tall / 2f : tall;

        DrawTextureRect(
            art,
            new Rect2(at.X - (wide / 2f), at.Y - above, wide, tall),
            false,
            new Color(1f, 1f, 1f, showing));
    }

    /// <summary>Whether there is nothing but air between a point and the top of the map.</summary>
    private bool OpenSky(Vec2 from)
    {
        int cellX = WorldScale.ToCell(from.X);

        for (int cellY = WorldScale.ToCell(from.Y); cellY >= 0; cellY--)
        {
            if (_stage.Ground.Contains(cellX, cellY)
                && MoleSim.Terrain.MaterialTable.IsSolid(_stage.Ground[cellX, cellY]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Which frame of the parachute swinging, off the engine clock.</summary>
    private static string ChuteArt() => "chute-" + (Beat() / 6 % 3);

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

            // Everything about the pose comes off the mole, so there is nothing here the
            // simulation does not already say. A steered mole is drawn at the position its owner
            // has walked it to, so the pose is read from the same place: whether that position is
            // under ground rather than whether the mole's own is.
            SeatPlanner planner = _stage.Planners[mole.Seat];
            Vec2 where = acting ? planner.PlannedPosition : mole.Position;
            bool aiming = acting && planner.Aiming;
            bool left = aiming
                ? (float)planner.AimAt.X.ToDecimal() < 0f
                : (float)mole.Facing.X.ToDecimal() < 0f;

            string pose = Moles.Pose(
                aiming,
                mole.IsSnared,
                mole.IsAirborne,
                Moles.Underground(where, _stage.Ground),
                mole.DiggingIsCheap,
                Moles.Walking(mole.Velocity));

            int frame = aiming
                ? Moles.AimFrame(planner.AimAt)
                : Moles.Frame(pose, Beat(), left);

            DrawMole(at, mole.Seat, mole.Pluck, acting, Owns(mole.Seat), pose, frame, left);

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
            Vec2 where = recording.PositionAt(_stage.Seconds, slot);
            Vec2 velocity = recording.VelocityOf(_stage.Tick, slot);

            // The recording keeps positions, velocities and pluck, so the poses it can tell apart
            // are the ones those three imply. Snared and clawed are not among them, which is why a
            // replay shows a mole digging where the live view would show it digging with claws.
            bool left = Moles.FacingLeft(velocity, wasLeft: false);
            bool airborne = Mathf.Abs((float)velocity.Y.ToDecimal()) > 1f;

            string pose = Moles.Pose(
                aiming: false,
                snared: false,
                airborne,
                Moles.Underground(where, _stage.Ground),
                clawed: false,
                Moles.Walking(velocity));

            DrawMole(
                ToPixels(where),
                seat,
                recording.PluckOf(_stage.Tick, slot),
                highlight: false,
                ours: Owns(seat),
                pose,
                Moles.Frame(pose, _stage.Tick, left),
                left);
        }

        foreach (Shot shot in recording.ShotsAt(_stage.Tick))
        {
            DrawShot(shot);
        }

        DrawBlasts(result);
        DrawDamageNumbers(recording, result);
    }

    /// <summary>
    /// One thing in the air, drawn as whatever fired it.
    /// </summary>
    /// <remarks>
    /// Every shot in the game was one dark circle, which is what a projectile looks like when the
    /// only thing known about it is where it is. The recording carries the weapon now, so a clod is
    /// a clod, a beetle is an indignant beetle, and a Tunnel Torpedo is a drill: the one weapon
    /// whose picture is a machine rather than a thing being thrown, and the reason the drill sheet
    /// has anywhere to go at all.
    ///
    /// Sized off the weapon's own artwork rather than the projectile's collision radius, which is
    /// two cells for everything and would draw a gnome the size of an acorn. Anything without a
    /// picture falls back to the clod, so a weapon added later appears as something rather than
    /// disappearing.
    /// </remarks>
    private void DrawShot(Shot shot)
    {
        Texture2D art = Art.Object(ShotArt(shot.Weapon));
        float wide = art.GetWidth() / Art.MolePixelsPerMetre * _scale;
        float tall = art.GetHeight() / Art.MolePixelsPerMetre * _scale;
        Vector2 at = ToPixels(shot.Position);

        DrawTextureRect(art, new Rect2(at.X - (wide / 2f), at.Y - (tall / 2f), wide, tall), false);
    }

    private static string ShotArt(WeaponId weapon) => weapon switch
    {
        WeaponId.BeetleLauncher => "beetle",
        WeaponId.AcornMortar => "acorns",
        WeaponId.TunnelTorpedo => "drill",
        WeaponId.Sandbag => "sandbag",
        WeaponId.BoomBeets => "beetroot",
        WeaponId.SpecialDelivery => "sack",
        WeaponId.MolyHandGrenade => "relic",
        WeaponId.GnomeMercy => "gnome",
        _ => "clod",
    };

    /// <summary>
    /// The blasts that have gone off so far, each playing out from where it landed.
    /// </summary>
    /// <remarks>
    /// Paired with the tick they went off on the same way the damage numbers are: the recording
    /// counts detonations at every tick, and the round result lists them in order, so the first
    /// tick the count reaches a blast's index is the tick that blast happened. Nothing new is
    /// recorded to make that work, which is why the counter was worth keeping when the list arrived.
    ///
    /// Scaled to the weapon's own blast radius rather than drawn at a fixed size, so an explosion
    /// says how far it reached. That is information a player can act on: the difference between a
    /// Clod Lobber and a Moly Hand Grenade is most of a metre of blast.
    /// </remarks>
    private void DrawBlasts(RoundResult result)
    {
        int upTo = Mathf.Min(_stage.BlastTick.Length, result.Blasts.Count);
        Strip blast = Art.Effect("blast");

        for (int index = 0; index < upTo; index++)
        {
            int age = _stage.Tick - _stage.BlastTick[index];

            if (age < 0 || age >= BlastTicks)
            {
                continue;
            }

            Detonation went = result.Blasts[index];
            float across = (float)went.Radius.ToDecimal() * _scale * BlastOverdraw * 2f;

            // The frame's own proportions rather than a square, because the artwork is a little
            // taller than it is wide and stretched to a square the ring came out visibly oval.
            Vector2 size = new Vector2(across, across * blast.FrameSize.Y / blast.FrameSize.X);
            Vector2 at = ToPixels(went.At);

            blast.Draw(
                this,
                new Rect2(at - (size / 2f), size),
                age * blast.Frames / BlastTicks,
                mirrored: false);
        }
    }

    /// <summary>How long a blast is on screen, at thirty ticks a second. Half a second.</summary>
    private const int BlastTicks = 15;

    /// <summary>
    /// How much wider than its blast radius the artwork is drawn.
    /// </summary>
    /// <remarks>
    /// The picture is a ring of fire with debris thrown clear of it, so the ring itself sits inside
    /// the frame rather than filling it. Drawn at exactly the radius the fire reads as smaller than
    /// the crater it just made, which is the wrong way round for something meant to say how far it
    /// reached.
    /// </remarks>
    private const float BlastOverdraw = 1.2f;

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
            // Centred on the mole rather than started at it, because these are drawn now rather
            // than typeset and a number has a middle.
            Vector2 at = ToPixels(recording.PositionOf(_stage.HitTick[index], slot))
                + new Vector2(0f, -(_scale * 0.9f) - (life * _scale * 0.7f));

            Glyphs.Number(
                this, hit.Damage, at, size, new Color(Palette.Damage, 1f - (life * life)));
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

        Vec2 went = recording.PositionOf(exitTick, slot);
        Vector2 at = ToPixels(
            ExitReel.Grounded(exit.Value) ? new Vec2(went.X, GroundUnder(went)) : went);

        float life = Mathf.Clamp((_stage.Tick - exitTick) / (float)Stage.ExitTicks, 0f, 1f);

        ExitReel.Play(this, exit.Value, at, _scale, mole.Seat, life);
    }

    /// <summary>
    /// The surface below a point, in world metres, or the point itself if there is none.
    /// </summary>
    /// <remarks>
    /// Scanned down from where the mole was rather than read off the frozen skyline, because what
    /// is wanted here is the ground as it stands now: a wall that stood on the original surface
    /// would hang over the crater that put the mole through it.
    /// </remarks>
    private Fix64 GroundUnder(Vec2 from)
    {
        int cellX = WorldScale.ToCell(from.X);

        for (int cellY = WorldScale.ToCell(from.Y); cellY < _stage.MapHeightCells; cellY++)
        {
            if (_stage.Ground.Contains(cellX, cellY)
                && MoleSim.Terrain.MaterialTable.IsSolid(_stage.Ground[cellX, cellY]))
            {
                return WorldScale.ToMetres(cellY) - MatchSettings.Radius;
            }
        }

        return from.Y;
    }

    private float MoleRadius() => (float)MatchSettings.Radius.ToDecimal() * _scale;

    /// <summary>A thirty-a-second counter, for cycling a pose while nothing is playing back.</summary>
    /// <remarks>
    /// Off the engine clock rather than the simulation's, because during planning there is no tick
    /// to count: the round has not run yet. Nothing depends on this agreeing between panes or
    /// between clients, since all it decides is which frame of a digging mole is on screen.
    /// </remarks>
    private static int Beat() => (int)(Time.GetTicksMsec() / 33UL);

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

    private void DrawMole(
        Vector2 at, int seat, int pluck, bool highlight, bool ours,
        string pose, int frame, bool facingLeft)
    {
        float radius = MoleRadius();
        Color colour = Palette.Seat(seat);

        if (highlight)
        {
            DrawCircle(at, radius * 1.6f, new Color(colour, 0.3f));
        }

        // Which platoon a mole belongs to used to be the whole of what its picture said, because
        // three circles in a platoon's colour is all a shape language can say about an animal.
        // The artwork says which animal and what it is doing; the trunks say whose it is, and the
        // highlight ring under it says which one is being steered.
        Moles.Draw(this, at, _scale, seat, pose, frame, facingLeft);

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

    /// <summary>
    /// The tunnel a plan is going to leave behind it.
    /// </summary>
    /// <remarks>
    /// The walked path is deliberately never drawn as a route, for the reasons in
    /// <see cref="DrawPlan"/>, and the digging is a different thing: not where the mole went but what
    /// will not be there any more when it has. Without it the one expensive half of the movement
    /// budget was invisible. A player could watch the stamina gauge empty and have no idea they had
    /// spent it carving a passage, because the ground they were carving through still looked whole.
    ///
    /// Taken from the walk's own path rather than from anything new. The carve is a disc swept along
    /// that path, so a thick line through the same points is not an approximation of the hole, it is
    /// the hole. Only the stretches where the real ground is solid are drawn, so walking through a
    /// chamber that was already there does not claim credit for digging it.
    ///
    /// The walk runs against a copy of the terrain, which is why none of this is visible for free:
    /// the holes exist, on a map that is thrown away when the round is committed.
    /// </remarks>
    private void DrawDiggings()
    {
        foreach (SeatPlanner planner in _stage.Planners)
        {
            if (planner.Actor is null || planner.Walk is null || !ShowsPlanOf(planner))
            {
                continue;
            }

            DrawDigging(planner.Walk.Path);
        }
    }

    private void DrawDigging(IReadOnlyList<Vec2> path)
    {
        float bore = MoleRadius() * 2f;
        List<Vector2> run = new List<Vector2>();

        foreach (Vec2 step in path)
        {
            if (InSolidGround(step))
            {
                run.Add(ToPixels(step));
                continue;
            }

            Sweep(run, bore);
            run.Clear();
        }

        Sweep(run, bore);
    }

    /// <summary>Lays one unbroken stretch of tunnel down, however short it is.</summary>
    private void Sweep(List<Vector2> run, float bore)
    {
        if (run.Count == 0)
        {
            return;
        }

        // A single point is a dugout rather than a tunnel, and DrawPolyline wants two.
        if (run.Count == 1)
        {
            DrawCircle(run[0], bore / 2f, Palette.Planned);
            return;
        }

        DrawPolyline(run.ToArray(), Palette.Planned, bore);

        // Round off both ends. A polyline is squared off, so a tunnel that stops in the middle of the
        // ground stopped with a flat face on it, which is not the shape a mole leaves.
        DrawCircle(run[0], bore / 2f, Palette.Planned);
        DrawCircle(run[run.Count - 1], bore / 2f, Palette.Planned);
    }

    /// <summary>Whether the ground as it stands is solid at a point, so getting there means digging.</summary>
    private bool InSolidGround(Vec2 at)
    {
        int cellX = WorldScale.ToCell(at.X);
        int cellY = WorldScale.ToCell(at.Y);

        return _stage.Ground.Contains(cellX, cellY)
            && MoleSim.Terrain.MaterialTable.IsSolid(_stage.Ground[cellX, cellY]);
    }

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
