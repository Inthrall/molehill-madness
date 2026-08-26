using System.Collections.Generic;
using Godot;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

/// <summary>
/// The playable build: four platoons, one screen, everything happening at once.
/// </summary>
/// <remarks>
/// This owns the match and the beats it moves through, and nothing else. The views draw it,
/// the panels report it, the planners collect input for it. Splitting it up that way is what
/// lets the same scene be a four-way couch split on a television and a single view with thumb
/// controls on a phone without any of the rules noticing.
///
/// Planning is simultaneous, as the design requires. Every platoon with its own controller
/// plans at the same time as the others, on one shared clock. Platoons without a controller
/// share the pointer and take it in turns, and the clock resets as it changes hands, so the
/// same code covers a hotseat prototype on one mouse and a couch full of gamepads.
///
/// The simulation is authoritative throughout. This reads it, submits plans to it, or plays
/// back a recording it handed over, and draws the map from its own copy taken before the round
/// resolved, so craters appear when the shells land rather than all at once.
/// </remarks>
public partial class MatchScene : Node2D
{
    private const int MapWidthCells = 1000;
    private const int MapHeightCells = 480;
    private const int PlayerCount = 4;

    /// <summary>
    /// How long a planning phase lasts. The design's Live pace is a minute; this is tighter
    /// because a prototype round wants to come round again quickly.
    /// </summary>
    private const float PlanningSeconds = 45f;

    private enum Beat
    {
        Planning,
        Resolving,
        Aftermath,
        Finished,
    }

    /// <summary>What a platoon plans with.</summary>
    private enum Device
    {
        /// <summary>Nothing of its own, so it waits its turn with the pointer.</summary>
        Shared = 0,

        /// <summary>The mouse, or a thumb.</summary>
        Pointer = 1,

        /// <summary>A gamepad, by index.</summary>
        Gamepad = 2,
    }

    private MoleMatch _match = null!;
    private TerrainGrid _shadow = null!;
    private TerrainView _terrain = null!;
    private Stage _stage = null!;
    private MatchHud _hud = null!;
    private TouchControls? _touch;

    private SeatPlanner[] _planners = System.Array.Empty<SeatPlanner>();
    private Device[] _devices = System.Array.Empty<Device>();
    private int[] _gamepad = System.Array.Empty<int>();
    private Vec2[] _penAt = System.Array.Empty<Vec2>();
    private readonly List<WorldView> _views = new List<WorldView>();

    private Beat _beat = Beat.Planning;
    private float _clock;
    private int _pointerSeat;

    private RoundResult? _result;
    private double _playback;
    private int _appliedChanges;
    private bool _sharedReplay;

    private AutoPilot? _autoPilot;
    private double _autoClock;
    private bool _forceSplit;
    private TouchTarget _held = TouchTarget.None;

    public override void _Ready()
    {
        // The terrain is one pixel per cell blown up several times over, so it has to be
        // point-sampled. Filtered, the soil turns to smudge and the cell grid stops reading.
        TextureFilter = TextureFilterEnum.Nearest;

        _match = MoleMatch.Create(PlayerCount, 20260826UL, MapWidthCells, MapHeightCells);
        _shadow = _match.Terrain.Clone();
        _terrain = new TerrainView(_shadow);
        _stage = new Stage(_match, _terrain.Texture, MapWidthCells, MapHeightCells);

        _planners = new SeatPlanner[PlayerCount];
        _penAt = new Vec2[PlayerCount];

        for (int seat = 0; seat < PlayerCount; seat++)
        {
            _planners[seat] = new SeatPlanner(_match, seat);
        }

        _stage.Planners = _planners;
        AssignDevices();

        CanvasLayer overlay = new CanvasLayer();
        AddChild(overlay);

        _hud = new MatchHud();
        overlay.AddChild(_hud);

        if (WantsTouch())
        {
            _touch = new TouchControls();
            overlay.AddChild(_touch);
        }

        _forceSplit = WasAskedFor("--split");

        if (WasAskedFor("--demo"))
        {
            _autoPilot = new AutoPilot(_match);
        }

        if (WasAskedFor("--frail"))
        {
            // Rigged starting conditions, the way a test fixture rigs them, so knockouts
            // happen in the first round or two and the exits can be watched. The one thing
            // here that reaches past the interface, and it does so before the match begins
            // rather than during it.
            foreach (Mole mole in _match.Moles)
            {
                mole.Pluck = 20;
            }
        }

        BeginRound();
    }

    /// <summary>
    /// Hands out the controls. One pointer, and a gamepad each for as many platoons as have
    /// one plugged in.
    /// </summary>
    /// <remarks>
    /// Everything the design asks for from split screen follows from this. Platoons with their
    /// own controller plan at the same moment as each other, which is the whole point; the rest
    /// queue for the pointer, which is what a prototype on one mouse actually is. Both run the
    /// same code, so the hotseat build being testable is not a separate build.
    ///
    /// Gamepad reading below is the one part of this that has never met real hardware. The
    /// simultaneous logic it feeds is exercised by the test driver, which can plan for several
    /// seats at once; the axis reads themselves are still owed a controller.
    /// </remarks>
    private void AssignDevices()
    {
        _devices = new Device[PlayerCount];
        _gamepad = new int[PlayerCount];
        Godot.Collections.Array<int> pads = Input.GetConnectedJoypads();

        _devices[0] = Device.Pointer;

        for (int seat = 1; seat < PlayerCount; seat++)
        {
            if (seat - 1 < pads.Count)
            {
                _devices[seat] = Device.Gamepad;
                _gamepad[seat] = pads[seat - 1];
                continue;
            }

            _devices[seat] = Device.Shared;
        }
    }

    /// <summary>How many platoons can plan at the same moment.</summary>
    private int SimultaneousSeats()
    {
        int count = 0;

        for (int seat = 0; seat < PlayerCount; seat++)
        {
            if (_devices[seat] != Device.Shared && _match.SeatIsAlive(seat))
            {
                count++;
            }
        }

        return count;
    }

    private static bool WasAskedFor(string flag)
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == flag)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WantsTouch() =>
        OS.HasFeature("mobile") || WasAskedFor("--touch");

    // ---- Beats -----------------------------------------------------------------------

    private void BeginRound()
    {
        _beat = Beat.Planning;
        _clock = PlanningSeconds;
        _stage.Planning = true;
        _stage.Recording = null;
        _stage.Result = null;

        foreach (SeatPlanner planner in _planners)
        {
            planner.BeginRound();
            _penAt[planner.Seat] = planner.Actor?.Position ?? Vec2.Zero;
        }

        _pointerSeat = NextPointerSeat(from: 0);
    }

    /// <summary>
    /// Which platoon the pointer is driving. Only ones without a controller of their own queue
    /// for it, and it moves on as each commits.
    /// </summary>
    private int NextPointerSeat(int from)
    {
        for (int seat = from; seat < PlayerCount; seat++)
        {
            if (_devices[seat] == Device.Gamepad)
            {
                continue;
            }

            if (_planners[seat].IsPlanning)
            {
                return seat;
            }
        }

        return -1;
    }

    private SeatPlanner? Pointed() =>
        _pointerSeat >= 0 && _planners[_pointerSeat].IsPlanning ? _planners[_pointerSeat] : null;

    private bool EverybodyIsIn()
    {
        foreach (SeatPlanner planner in _planners)
        {
            if (planner.IsPlanning)
            {
                return false;
            }
        }

        return true;
    }

    private void Resolve()
    {
        foreach (SeatPlanner planner in _planners)
        {
            planner.Commit();
        }

        _result = _match.ResolveRound(record: true);
        _stage.Result = _result;
        _stage.Recording = _result.Recording;
        _stage.Planning = false;
        _stage.Tick = 0;
        _stage.Seconds = Fix64.Zero;
        _playback = 0;
        _appliedChanges = 0;
        _beat = Beat.Resolving;

        NoteWhenThingsHappened();
        DecideReplayLayout();
        ReportRound();
    }

    /// <summary>
    /// Works out when each thing happened, so it can be shown happening.
    /// </summary>
    /// <remarks>
    /// A recording stores how far into the hit and knockout lists each tick had got, which is
    /// the compact way round and the wrong way round for drawing. Inverting it once per round
    /// gives a tick per hit and per exit, so a damage number can rise and fade from the moment
    /// it landed and a pratfall can be timed from the moment it started.
    /// </remarks>
    private void NoteWhenThingsHappened()
    {
        RoundRecording? recording = _result?.Recording;

        if (recording is null)
        {
            return;
        }

        int[] exits = new int[recording.MoleCount];

        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            exits[slot] = -1;

            for (int tick = 0; tick < recording.Ticks; tick++)
            {
                if (recording.IsOffDutyAt(tick, slot))
                {
                    exits[slot] = tick;
                    break;
                }
            }
        }

        int[] hits = new int[_result!.Hits.Count];
        int landed = 0;

        for (int tick = 0; tick < recording.Ticks && landed < hits.Length; tick++)
        {
            while (landed < recording.HitsUpTo(tick) && landed < hits.Length)
            {
                hits[landed] = tick;
                landed++;
            }
        }

        _stage.ExitTick = exits;
        _stage.HitTick = hits;
    }

    /// <summary>
    /// The design's rule for the replay: one screen when the action is close enough to share,
    /// and one view each when it is not.
    /// </summary>
    /// <remarks>
    /// Decided once, from the finished recording, rather than every frame. The round has
    /// already resolved by the time anybody watches it, so the whole extent of it is knowable
    /// up front, and deciding up front is the only way to stop the screen splitting and merging
    /// each time somebody is punted sideways.
    /// </remarks>
    private void DecideReplayLayout()
    {
        RoundRecording? recording = _result?.Recording;

        if (recording is null)
        {
            _sharedReplay = true;
            return;
        }

        Fix64 minX = Fix64.MaxValue;
        Fix64 maxX = Fix64.MinValue;
        Fix64 minY = Fix64.MaxValue;
        Fix64 maxY = Fix64.MinValue;
        bool any = false;

        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            if (!IsActor(slot))
            {
                continue;
            }

            int last = _stage.ExitTick[slot] >= 0 ? _stage.ExitTick[slot] : recording.Ticks - 1;

            for (int tick = 0; tick <= last; tick++)
            {
                Vec2 where = recording.PositionOf(tick, slot);
                minX = Fix64.Min(minX, where.X);
                maxX = Fix64.Max(maxX, where.X);
                minY = Fix64.Min(minY, where.Y);
                maxY = Fix64.Max(maxY, where.Y);
                any = true;
            }
        }

        if (!any)
        {
            _sharedReplay = true;
            return;
        }

        // Platoons spawn interleaved, so early rounds are nearly always close enough to share
        // a view and the split path would go unexercised. The flag forces it for inspection.
        _sharedReplay = !_forceSplit && SplitLayout.ActionFitsOneView(
            Band(),
            (float)(maxX - minX).ToDecimal(),
            (float)(maxY - minY).ToDecimal());
    }

    /// <summary>Whether a mole slot belongs to a platoon that acted this round.</summary>
    private bool IsActor(int slot)
    {
        Mole mole = _match.Moles[slot];

        return _planners[mole.Seat].Actor == mole;
    }

    private void ReportRound()
    {
        if (_autoPilot is null || _result is null)
        {
            return;
        }

        string ending = string.Empty;

        if (_result.MatchOver)
        {
            ending = _result.WinningSeat >= 0
                ? $", seat {_result.WinningSeat + 1} takes the flowerbed"
                : ", everybody went out together";
        }

        GD.Print(
            $"round {_result.Round}: {_result.TotalDamage} damage, "
            + $"{_result.Knockouts.Count} out{ending}");
    }

    private double PlaybackDuration() =>
        (double)(_result?.Recording?.Duration.ToDecimal() ?? 0m);

    /// <summary>
    /// Walks the map forward to wherever the clock has got to. The journal is in the order
    /// things happened, so this plays entries out and never replays the round.
    /// </summary>
    private void CatchTerrainUp(int upTo)
    {
        IReadOnlyList<TerrainChange> changes = _result!.Recording!.TerrainChanges;

        while (_appliedChanges < upTo && _appliedChanges < changes.Count)
        {
            _shadow.Apply(changes[_appliedChanges]);
            _appliedChanges++;
        }
    }

    // ---- Frame -----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        switch (_beat)
        {
            case Beat.Planning:
                RunPlanning(delta);
                break;

            case Beat.Resolving:
                RunReplay(delta);
                break;

            default:
                DriveIfAsked(delta);
                break;
        }

        _terrain.Refresh();
        Relayout(delta);
        _hud.Apply(BuildHudState());

        if (_touch is not null)
        {
            _touch.LayOut(GetViewportRect().Size);
            _touch.Planner = Pointed();
            _touch.QueueRedraw();
        }
    }

    private void RunPlanning(double delta)
    {
        _clock -= (float)delta;

        foreach (SeatPlanner planner in _planners)
        {
            planner.Tick(delta);
        }

        for (int seat = 0; seat < PlayerCount; seat++)
        {
            if (_devices[seat] == Device.Gamepad)
            {
                DriveWithGamepad(seat, delta);
            }
        }

        TrackPointerReset(delta);
        DriveIfAsked(delta);

        // The pointer moves on the moment its platoon is done, and the clock starts again
        // with it. Whoever picks the mouse up next gets a full turn, not the tail of somebody
        // else's.
        if (Pointed() is null)
        {
            int next = NextPointerSeat(from: 0);

            if (next != _pointerSeat)
            {
                _pointerSeat = next;
                _clock = PlanningSeconds;
            }
        }

        if (EverybodyIsIn() || _clock <= 0)
        {
            // Out of time commits whatever is on the paper, which is the whole reason the
            // clock is worth watching.
            Resolve();
        }
    }

    private void RunReplay(double delta)
    {
        _playback += delta;
        _stage.Tick = CurrentTick();
        _stage.Seconds = Fix64.Ratio((int)(_playback * 1000), 1000);
        CatchTerrainUp(_result!.Recording!.ChangesUpTo(_stage.Tick));

        if (_playback < PlaybackDuration())
        {
            return;
        }

        CatchTerrainUp(int.MaxValue);
        _beat = _result.MatchOver ? Beat.Finished : Beat.Aftermath;
    }

    private int CurrentTick()
    {
        int ticks = _result?.Recording?.Ticks ?? 1;

        return Mathf.Clamp((int)(_playback * MatchSettings.TicksPerSecond), 0, ticks - 1);
    }

    // ---- Layout ----------------------------------------------------------------------

    /// <summary>The screen minus the strip the panels own, which the world must stay clear of.</summary>
    private Rect2 Band()
    {
        Vector2 viewport = GetViewportRect().Size;

        return new Rect2(0, 0, viewport.X, viewport.Y);
    }

    private void Relayout(double delta)
    {
        SplitLayout.Pane[] panes = Panes();

        while (_views.Count < panes.Length)
        {
            WorldView view = new WorldView(_stage);
            _views.Add(view);
            AddChild(view);
            MoveChild(view, _views.Count - 1);
        }

        for (int index = 0; index < _views.Count; index++)
        {
            bool used = index < panes.Length;
            _views[index].Visible = used;

            if (used)
            {
                _views[index].Occupy(panes[index], delta);
            }
        }
    }

    /// <summary>
    /// How the screen is carved up right now. One view each while everybody plans at once,
    /// one shared view when only one platoon is actually holding a pen, and for the replay
    /// whatever the action turned out to need.
    /// </summary>
    private SplitLayout.Pane[] Panes()
    {
        Rect2 band = Band();

        if (_touch is not null)
        {
            // A phone is one player, and its screen has no room to be divided anyway.
            return SplitLayout.Shared(band);
        }

        if (_beat == Beat.Planning)
        {
            return _forceSplit || SimultaneousSeats() >= 2
                ? SplitLayout.PerSeat(PlayerCount, band)
                : SplitLayout.Shared(band);
        }

        if (_beat == Beat.Resolving && !_sharedReplay)
        {
            return SplitLayout.PerSeat(PlayerCount, band);
        }

        return SplitLayout.Shared(band);
    }

    // ---- Pointer input ---------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        if (_touch is not null)
        {
            HandleTouch(@event);
            return;
        }

        HandleMouse(@event);
    }

    private void HandleMouse(InputEvent @event)
    {
        SeatPlanner? planner = Pointed();

        if (_beat != Beat.Planning || planner is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    planner.PenDown(PointerWorld(left.Position, planner.Seat));
                }
                else
                {
                    planner.PenUp();
                }

                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                if (right.Pressed)
                {
                    planner.BeginAim(PointerWorld(right.Position, planner.Seat));
                }
                else
                {
                    planner.ReleaseAim();
                }

                break;

            case InputEventMouseMotion motion:
                Vec2 at = PointerWorld(motion.Position, planner.Seat);

                if (planner.PenIsDown)
                {
                    planner.Extend(at);
                }
                else
                {
                    planner.MoveAim(at);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// A touch is a stroke on the map unless it landed on a control, which is the only way a
    /// single finger can do both.
    /// </summary>
    private void HandleTouch(InputEvent @event)
    {
        SeatPlanner? planner = Pointed();

        if (_touch is null || _beat != Beat.Planning || planner is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } press:
                if (press.Pressed)
                {
                    BeginTouch(planner, press.Position);
                }
                else
                {
                    EndTouch(planner);
                }

                break;

            case InputEventMouseMotion motion:
                ContinueTouch(planner, motion);
                break;

            default:
                break;
        }
    }

    private void BeginTouch(SeatPlanner planner, Vector2 at)
    {
        _held = _touch!.Hit(at);
        _touch.Press(_held);

        switch (_held)
        {
            case TouchTarget.None:
                planner.PenDown(PointerWorld(at, planner.Seat));
                break;

            case TouchTarget.Fire:
                planner.BeginAim(planner.Muzzle);
                break;

            case TouchTarget.Commit:
                planner.Commit();
                break;

            default:
                break;
        }
    }

    private void ContinueTouch(SeatPlanner planner, InputEventMouseMotion motion)
    {
        switch (_held)
        {
            case TouchTarget.None:
                planner.Extend(PointerWorld(motion.Position, planner.Seat));
                break;

            case TouchTarget.Fire:
                // Direction and power out of one thumb: the further the stick is pulled, the
                // harder the throw, exactly as a mouse drag works.
                Vector2 drag = motion.Position - _touch!.FireAt;
                _touch.AimDrag = drag;
                planner.MoveAim(AimFromStick(planner, drag));
                break;

            case TouchTarget.Wheel:
                _wheelTravel += motion.Relative.Y;

                while (Mathf.Abs(_wheelTravel) >= _touch!.WheelNotch)
                {
                    planner.CycleWeapon(_wheelTravel > 0 ? 1 : -1);
                    _wheelTravel -= Mathf.Sign(_wheelTravel) * _touch.WheelNotch;
                }

                break;

            default:
                break;
        }
    }

    private void EndTouch(SeatPlanner planner)
    {
        switch (_held)
        {
            case TouchTarget.None:
                planner.PenUp();
                break;

            case TouchTarget.Fire:
                planner.ReleaseAim();
                break;

            case TouchTarget.Dynamite:
                planner.PlantCharge();
                break;

            default:
                break;
        }

        _held = TouchTarget.None;
        _wheelTravel = 0;
        _touch!.Release();
    }

    private float _wheelTravel;

    /// <summary>Turns a thumb stick into an aim point out in the world.</summary>
    private Vec2 AimFromStick(SeatPlanner planner, Vector2 drag)
    {
        if (drag.LengthSquared() < 1f)
        {
            return planner.Muzzle;
        }

        float full = Mathf.Max(_touch!.WheelNotch * 3f, 1f);
        float charge = Mathf.Min(drag.Length() / full, 1f);
        Vector2 direction = drag.Normalized();
        Fix64 reach = SeatPlanner.FullPowerDrag * Fix64.Ratio((int)(charge * 256), 256);

        return planner.Muzzle + new Vec2(
            Fix64.Ratio((int)(direction.X * 256), 256) * reach,
            Fix64.Ratio((int)(direction.Y * 256), 256) * reach);
    }

    private void TrackPointerReset(double delta)
    {
        SeatPlanner? planner = Pointed();

        if (planner is null)
        {
            return;
        }

        bool holding = _held == TouchTarget.Reset || Input.IsKeyPressed(Key.R);

        if (holding)
        {
            planner.HoldReset(delta);
        }
        else
        {
            planner.ReleaseReset();
        }
    }

    /// <summary>
    /// Which view a pointer is over, and where in the world that is. With the screen split,
    /// the same pixel means different things in different panes.
    /// </summary>
    private Vec2 PointerWorld(Vector2 onScreen, int seat)
    {
        WorldView? owned = null;

        foreach (WorldView view in _views)
        {
            if (!view.Visible)
            {
                continue;
            }

            if (new Rect2(view.Position, view.Size).HasPoint(onScreen))
            {
                return view.ToWorld(onScreen - view.Position);
            }

            if (view.Seat == seat || view.Seat < 0)
            {
                owned = view;
            }
        }

        // Outside every pane, so read it against the one the planner belongs to and let the
        // route run off the edge rather than snapping somewhere arbitrary.
        return owned is null
            ? Vec2.Zero
            : owned.ToWorld(onScreen - owned.Position);
    }

    private void HandleKey(InputEventKey key)
    {
        SeatPlanner? planner = Pointed();

        switch (key.Keycode)
        {
            case Key.Space:
                Advance(planner);
                break;

            case Key.Q:
                planner?.CycleWeapon(-1);
                break;

            case Key.E:
                planner?.CycleWeapon(1);
                break;

            case Key.Tab:
                planner?.CycleActor();
                break;

            case Key.F:
                planner?.PlantCharge();
                break;

            default:
                break;
        }
    }

    private void Advance(SeatPlanner? planner)
    {
        switch (_beat)
        {
            case Beat.Planning:
                planner?.Commit();
                break;

            case Beat.Resolving:
                // Skip to the end of a round somebody has seen enough of.
                _playback = PlaybackDuration();
                break;

            case Beat.Aftermath:
                BeginRound();
                break;

            default:
                break;
        }
    }

    // ---- Gamepad input ---------------------------------------------------------------

    /// <summary>
    /// One platoon's controller: left stick steers the pen, the face buttons lay, aim, reset
    /// and commit, and the shoulders turn the wheel.
    /// </summary>
    /// <remarks>
    /// Never yet run against real hardware, and marked as such. The simultaneous planning it
    /// feeds is exercised by the test driver, which plans for several platoons at once, so what
    /// is owed a controller is only the axis and button reads themselves.
    /// </remarks>
    private void DriveWithGamepad(int seat, double delta)
    {
        SeatPlanner planner = _planners[seat];

        if (!planner.IsPlanning)
        {
            return;
        }

        int pad = _gamepad[seat];
        Vector2 stick = new Vector2(
            Input.GetJoyAxis(pad, JoyAxis.LeftX), Input.GetJoyAxis(pad, JoyAxis.LeftY));

        if (stick.Length() > StickDeadZone)
        {
            Fix64 step = Fix64.Ratio((int)((float)delta * PenMetresPerSecond * 256), 256);
            _penAt[seat] += new Vec2(
                Fix64.Ratio((int)(stick.X * 256), 256) * step,
                Fix64.Ratio((int)(stick.Y * 256), 256) * step);
        }

        bool laying = Input.IsJoyButtonPressed(pad, JoyButton.A);

        if (laying && !planner.PenIsDown)
        {
            planner.PenDown(_penAt[seat]);
        }
        else if (laying)
        {
            planner.Extend(_penAt[seat]);
        }
        else if (planner.PenIsDown)
        {
            planner.PenUp();
        }

        Vector2 aim = new Vector2(
            Input.GetJoyAxis(pad, JoyAxis.RightX), Input.GetJoyAxis(pad, JoyAxis.RightY));

        if (aim.Length() > StickDeadZone)
        {
            Fix64 reach = SeatPlanner.FullPowerDrag
                * Fix64.Ratio((int)(Mathf.Min(aim.Length(), 1f) * 256), 256);

            if (!planner.Aiming)
            {
                planner.BeginAim(planner.Muzzle);
            }

            Vector2 direction = aim.Normalized();
            planner.MoveAim(planner.Muzzle + new Vec2(
                Fix64.Ratio((int)(direction.X * 256), 256) * reach,
                Fix64.Ratio((int)(direction.Y * 256), 256) * reach));
        }
        else if (planner.Aiming)
        {
            planner.ReleaseAim();
        }

        if (Input.IsJoyButtonPressed(pad, JoyButton.B))
        {
            planner.HoldReset(delta);
        }
        else
        {
            planner.ReleaseReset();
        }

        if (Input.IsJoyButtonPressed(pad, JoyButton.Start))
        {
            planner.Commit();
        }

        bool planting = Input.IsJoyButtonPressed(pad, JoyButton.X);

        if (planting && !_plantHeld[seat])
        {
            planner.PlantCharge();
        }

        _plantHeld[seat] = planting;
        TurnWheel(seat, pad, planner);
    }

    private void TurnWheel(int seat, int pad, SeatPlanner planner)
    {
        bool up = Input.IsJoyButtonPressed(pad, JoyButton.RightShoulder);
        bool down = Input.IsJoyButtonPressed(pad, JoyButton.LeftShoulder);

        if (up == _shoulderHeldUp[seat] && down == _shoulderHeldDown[seat])
        {
            return;
        }

        if (up && !_shoulderHeldUp[seat])
        {
            planner.CycleWeapon(1);
        }
        else if (down && !_shoulderHeldDown[seat])
        {
            planner.CycleWeapon(-1);
        }

        _shoulderHeldUp[seat] = up;
        _shoulderHeldDown[seat] = down;
    }

    private readonly bool[] _shoulderHeldUp = new bool[PlayerCount];
    private readonly bool[] _shoulderHeldDown = new bool[PlayerCount];
    private readonly bool[] _plantHeld = new bool[PlayerCount];

    private const float StickDeadZone = 0.2f;

    /// <summary>How fast a stick walks the pen across the map.</summary>
    private const float PenMetresPerSecond = 14f;

    // ---- The test driver -------------------------------------------------------------

    /// <summary>
    /// Lets the driver take every platoon that has nobody at its controls. It goes through the
    /// same verbs a player does, so there is no second path into a plan.
    /// </summary>
    private void DriveIfAsked(double delta)
    {
        if (_autoPilot is null)
        {
            return;
        }

        _autoClock += delta;

        if (_autoClock <= AutoPause)
        {
            return;
        }

        if (_beat == Beat.Aftermath)
        {
            _autoClock = 0;
            BeginRound();
            return;
        }

        if (_beat != Beat.Planning)
        {
            return;
        }

        // Laying and committing are separate beats a moment apart, so a recorded frame can
        // catch the planning screen mid-thought.
        bool laid = false;

        foreach (SeatPlanner planner in _planners)
        {
            if (!planner.IsPlanning || planner.Actor is null || planner.Preview is not null)
            {
                continue;
            }

            AutoPilot.Intent intent = _autoPilot.Decide(planner.Actor, planner.Weapon);

            planner.PenDown(planner.Actor.Position);

            foreach (Vec2 point in intent.Route)
            {
                planner.Extend(point);
            }

            planner.PenUp();
            planner.BeginAim(intent.AimAt);
            planner.ReleaseAim();

            if (intent.PlantCharge)
            {
                planner.PlantCharge();
            }

            laid = true;
        }

        _autoClock = 0;

        if (laid)
        {
            return;
        }

        foreach (SeatPlanner planner in _planners)
        {
            planner.Commit();
        }
    }

    /// <summary>Long enough that a recorded frame catches the planning screen mid-thought.</summary>
    private const double AutoPause = 0.35;

    // ---- Reporting -------------------------------------------------------------------

    private MatchHud.State BuildHudState()
    {
        SplitLayout.TrySpareCell(PlayerCount, Band(), out Rect2 spare);
        bool splitting = Panes().Length > 1;

        return new MatchHud.State
        {
            ClockLeft = _beat == Beat.Planning ? Mathf.Max(_clock, 0f) : -1f,
            ClockLength = PlanningSeconds,
            Standing = StandingPerSeat(),
            Committed = CommittedPerSeat(),
            Wind = (float)_match.Wind.ToDecimal(),
            Round = _match.Round + (_beat == Beat.Planning ? 1 : 0),
            Winner = _beat == Beat.Finished ? _result?.WinningSeat ?? -1 : -2,
            SpareCell = spare,
            HasSpareCell = splitting && PlayerCount == 3,
            Split = splitting,
        };
    }

    /// <summary>
    /// How many of each platoon are still up, as of whatever the viewer has actually seen.
    /// </summary>
    /// <remarks>
    /// Read from the recording during playback rather than from the moles themselves. A round
    /// resolves before its first frame is drawn, so counting live moles showed the final score
    /// for the whole eight seconds and gave away every knockout before it happened.
    /// </remarks>
    private int[] StandingPerSeat()
    {
        int[] standing = new int[PlayerCount];
        RoundRecording? recording = _beat == Beat.Resolving ? _stage.Recording : null;

        for (int slot = 0; slot < _match.Moles.Count; slot++)
        {
            Mole mole = _match.Moles[slot];

            bool gone = recording is null
                ? mole.IsOffDuty
                : recording.IsOffDutyAt(_stage.Tick, slot);

            if (!gone)
            {
                standing[mole.Seat]++;
            }
        }

        return standing;
    }

    private bool[] CommittedPerSeat()
    {
        bool[] committed = new bool[PlayerCount];

        for (int seat = 0; seat < PlayerCount; seat++)
        {
            committed[seat] = _beat == Beat.Planning && _planners[seat].Committed;
        }

        return committed;
    }
}
