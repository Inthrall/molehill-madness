using System.Collections.Generic;
using Godot;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

/// <summary>
/// The playable prototype: four platoons, one screen, plan and resolve.
/// </summary>
/// <remarks>
/// This exists to answer one question no test can: is simultaneous resolution actually
/// funny and tense with real people in a room? Everything here serves getting to that
/// answer, and very little of it is meant to survive to launch.
///
/// So it is hotseat rather than split screen, and it has words on it. Both are deliberate:
/// the split-screen layouts and the wordless icon set are Phase 3, and doing either now
/// would mean days spent on presentation before knowing whether the loop is worth
/// presenting.
///
/// The simulation is authoritative. This only ever reads it, submits plans to it, or plays
/// back a recording it handed over. It draws the map from its own copy, taken before the
/// round resolved, so craters appear when the shells land rather than all at once.
/// </remarks>
public partial class MatchScene : Node2D
{
    /// <summary>
    /// Close enough in that a mole is thirty pixels across, which is the smallest it can be
    /// and still carry an expression later. The map is larger than the screen at this zoom
    /// in both directions, so the camera has to work, which is the point.
    /// </summary>
    private const float PixelsPerMetre = 40f;

    /// <summary>Where the world starts, clear of the top panel.</summary>
    private const float WorldTop = MatchHud.StripHeight + 10f;

    /// <summary>
    /// Half the design's map in each direction: 62 metres across and 30 deep.
    /// </summary>
    /// <remarks>
    /// The first prototype map was 21 metres deep, and it flooded. Lava rises three metres a
    /// round from round eight, which on the design's sixty-metre map gives twenty rounds of
    /// mounting pressure and on a twenty-one-metre one drowns the whole field by round
    /// fourteen. A headless match ended with ten moles going out in a single round and the
    /// match declared a draw, which is not a thing the design ever asked for.
    ///
    /// The rise is an absolute distance rather than a share of the depth, so it only means
    /// what the design intends on a map of roughly the depth the design assumes. Worth
    /// knowing before hand-authored maps arrive, because a shallow one will misbehave the
    /// same way.
    /// </remarks>
    private const int MapWidthCells = 1000;

    private const int MapHeightCells = 480;

    private const int PlayerCount = 4;

    /// <summary>A mole's drawn size, taken from the size it actually is.</summary>
    private static readonly float MoleRadius =
        (float)MatchSettings.Radius.ToDecimal() * PixelsPerMetre;

    private enum Beat
    {
        Planning,
        Resolving,
        Aftermath,
        Finished,
    }

    private MoleMatch _match = null!;
    private TerrainGrid _shadow = null!;
    private TerrainView _terrain = null!;
    private MatchHud _hud = null!;
    private Beat _beat = Beat.Planning;

    // Planning
    private int _planningSeat;
    private Mole? _actor;
    private readonly List<Vec2> _route = new List<Vec2>();
    private bool _penDown;
    private GhostPreview? _preview;
    private double _ghostClock;
    private WeaponId _weapon = WeaponId.ClodLobber;
    private PlanAction? _stampedShot;
    private bool _aiming;
    private Vec2 _aimAt;
    private double _resetHeld;
    private bool _freeResetSpent;

    // Resolving
    private RoundResult? _result;
    private double _playback;
    private int _appliedChanges;
    private int[] _exitTick = System.Array.Empty<int>();
    private int[] _hitTick = System.Array.Empty<int>();
    private readonly List<int> _actorSlots = new List<int>();

    // Driven rather than played, for inspecting the render layer without four people
    // in the room. Off unless asked for on the command line.
    private AutoPilot? _autoPilot;
    private double _autoClock;
    private Vector2 _cameraAt;

    private static readonly WeaponId[] Wheel =
    {
        WeaponId.ClodLobber,
        WeaponId.BeetleLauncher,
        WeaponId.AcornMortar,
        WeaponId.Fracking,
        WeaponId.BigWhack,
        WeaponId.TunnelTorpedo,
        WeaponId.SnapTrap,
        WeaponId.RootSnare,
        WeaponId.GeyserCap,
        WeaponId.PowerClaws,
        WeaponId.Sandbag,
        WeaponId.SpecialDelivery,
        WeaponId.MolyHandGrenade,
        WeaponId.GnomeMercy,
    };

    private static readonly Color[] SeatColours =
    {
        new Color(0.294f, 0.545f, 0.231f),
        new Color(0.780f, 0.353f, 0.157f),
        new Color(0.306f, 0.510f, 0.651f),
        new Color(0.769f, 0.165f, 0.047f),
    };

    public override void _Ready()
    {
        // The terrain is one pixel per cell blown up several times over, so it has to be
        // point-sampled. Filtered, the soil turns to smudge and the cell grid stops reading.
        TextureFilter = TextureFilterEnum.Nearest;

        _match = MoleMatch.Create(PlayerCount, 20260826UL, MapWidthCells, MapHeightCells);
        _shadow = _match.Terrain.Clone();
        _terrain = new TerrainView(_shadow);

        CanvasLayer overlay = new CanvasLayer();
        AddChild(overlay);
        _hud = new MatchHud();
        overlay.AddChild(_hud);

        if (WasAskedFor("--demo"))
        {
            _autoPilot = new AutoPilot(_match);
        }

        if (WasAskedFor("--frail"))
        {
            // Rigged starting conditions, the way a test fixture rigs them, so knockouts
            // happen in the first round or two and the exits can actually be watched. This
            // is the one thing here that reaches past the interface, and it does it before
            // the match begins rather than during it: nothing mid-match ever touches state
            // a player could not reach.
            foreach (Mole mole in _match.Moles)
            {
                mole.Pluck = 20;
            }
        }

        BeginPlanning(seat: 0);
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

    // ---- Planning ------------------------------------------------------------------

    private void BeginPlanning(int seat)
    {
        _beat = Beat.Planning;
        _planningSeat = seat;
        _actor = null;
        _route.Clear();
        _preview = null;
        _stampedShot = null;
        _penDown = false;
        _aiming = false;
        _resetHeld = 0;
        _freeResetSpent = false;

        foreach (Mole candidate in _match.Eligible(seat))
        {
            _actor = candidate;
            break;
        }

        // A seat with nobody left to move simply passes.
        if (_actor is null)
        {
            AdvanceSeat();
        }
    }

    private void AdvanceSeat()
    {
        int next = _planningSeat + 1;

        while (next < PlayerCount && !_match.SeatIsAlive(next))
        {
            next++;
        }

        if (next < PlayerCount)
        {
            BeginPlanning(next);
            return;
        }

        Resolve();
    }

    private void Commit()
    {
        if (_actor is null)
        {
            AdvanceSeat();
            return;
        }

        RoutePoint[] route = new RoutePoint[_route.Count];

        for (int index = 0; index < _route.Count; index++)
        {
            route[index] = RoutePoint.FromWorld(_route[index]);
        }

        PlanAction[] actions = _stampedShot is null
            ? System.Array.Empty<PlanAction>()
            : new[] { _stampedShot.Value };

        _match.SubmitPlan(new Plan(_planningSeat, _actor.Index, _weapon, route, actions));

        // Remembered so the camera has something to look at. The simulation treats all
        // sixteen moles alike, and rightly, but only four of them are doing anything.
        _actorSlots.Add(SlotOf(_planningSeat, _actor.Index));
        AdvanceSeat();
    }

    private void RebuildPreview()
    {
        if (_actor is null || _route.Count == 0)
        {
            _preview = null;
            return;
        }

        _preview = GhostPreview.Walk(_actor, _match.Terrain, _route);
        _ghostClock = 0;
    }

    /// <summary>
    /// One reset a turn, and then only what the crates have handed over. Spending the free
    /// one first means a hoarded token is still a token afterwards.
    /// </summary>
    private void SpendReset()
    {
        if (_actor is null)
        {
            return;
        }

        if (_freeResetSpent && _actor.ResetTokens <= 0)
        {
            return;
        }

        if (_freeResetSpent)
        {
            _actor.ResetTokens--;
        }
        else
        {
            _freeResetSpent = true;
        }

        _route.Clear();
        _stampedShot = null;
        _preview = null;
    }

    private int ResetsLeft() => (_freeResetSpent ? 0 : 1) + (_actor?.ResetTokens ?? 0);

    // ---- Resolving -----------------------------------------------------------------

    private void Resolve()
    {
        _result = _match.ResolveRound(record: true);
        _playback = 0;
        _appliedChanges = 0;
        _beat = Beat.Resolving;
        NoteExitTicks();
        ReportRound();
    }

    /// <summary>
    /// Says what happened, when the driver is at the controls. This is what makes a headless
    /// run a smoke test rather than a silent one: with no window to look at, a match that
    /// plays to a winner and a match that quietly stops in round two are otherwise
    /// indistinguishable.
    /// </summary>
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

    private void BeginRoundOfPlanning()
    {
        _actorSlots.Clear();
        BeginPlanning(FirstLivingSeat());
    }

    /// <summary>
    /// Works out when each thing happened, so it can be shown happening.
    /// </summary>
    /// <remarks>
    /// The recording says how far into the hit and knockout lists each tick had got, which
    /// is the compact way to store it and the wrong way round for drawing. Inverting it once
    /// per round gives a tick per hit and per exit, so a damage number can rise and fade
    /// from the moment it landed and a pratfall can be timed from the moment it started.
    /// </remarks>
    private void NoteExitTicks()
    {
        RoundRecording? recording = _result?.Recording;

        if (recording is null)
        {
            return;
        }

        _exitTick = new int[recording.MoleCount];

        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            _exitTick[slot] = -1;

            for (int tick = 0; tick < recording.Ticks; tick++)
            {
                if (recording.IsOffDutyAt(tick, slot))
                {
                    _exitTick[slot] = tick;
                    break;
                }
            }
        }

        _hitTick = new int[_result!.Hits.Count];
        int landed = 0;

        for (int tick = 0; tick < recording.Ticks && landed < _hitTick.Length; tick++)
        {
            while (landed < recording.HitsUpTo(tick) && landed < _hitTick.Length)
            {
                _hitTick[landed] = tick;
                landed++;
            }
        }
    }

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

    private double PlaybackDuration() => (double)(_result?.Recording?.Duration.ToDecimal() ?? 0m);

    // ---- Frame ---------------------------------------------------------------------

    public override void _Process(double delta)
    {
        DriveIfAsked(delta);

        switch (_beat)
        {
            case Beat.Planning:
                _ghostClock += delta;
                TrackReset(delta);
                break;

            case Beat.Resolving:
                _playback += delta;
                CatchTerrainUp(_result!.Recording!.ChangesUpTo(CurrentTick()));

                if (_playback >= PlaybackDuration())
                {
                    CatchTerrainUp(int.MaxValue);
                    _beat = _result.MatchOver ? Beat.Finished : Beat.Aftermath;
                }

                break;

            default:
                break;
        }

        _terrain.Refresh();
        UpdateCamera(delta);
        _hud.Apply(BuildHudState());
        QueueRedraw();
    }

    /// <summary>
    /// Lets the test driver take the controls. It goes through the same three steps a
    /// player does, so there is no second path into a plan.
    /// </summary>
    private void DriveIfAsked(double delta)
    {
        if (_autoPilot is null)
        {
            return;
        }

        _autoClock += delta;

        // Laying the plan and committing it are separate beats, a moment apart, so the
        // planning screen with its ghost mid-walk is something a recorded frame can catch.
        if (_beat == Beat.Planning && _actor is not null && _autoClock > AutoPause)
        {
            if (_preview is null)
            {
                AutoPilot.Intent intent = _autoPilot.Decide(_actor, _weapon);
                _route.Clear();
                _route.AddRange(intent.Route);
                RebuildPreview();

                _aimAt = intent.AimAt;
                StampShot();
                _autoClock = 0;
                return;
            }

            _autoClock = 0;
            Commit();
            return;
        }

        if (_beat == Beat.Aftermath && _autoClock > AutoPause * 2)
        {
            _autoClock = 0;
            Advance();
        }
    }

    /// <summary>Long enough that a recorded frame catches the planning screen mid-thought.</summary>
    private const double AutoPause = 0.35;

    private int CurrentTick()
    {
        int ticks = _result?.Recording?.Ticks ?? 1;

        return Mathf.Clamp((int)(_playback * MatchSettings.TicksPerSecond), 0, ticks - 1);
    }

    private void TrackReset(double delta)
    {
        if (Input.IsKeyPressed(Key.R))
        {
            _resetHeld += delta;

            if (_resetHeld >= HoldToReset)
            {
                _resetHeld = 0;
                SpendReset();
            }

            return;
        }

        _resetHeld = 0;
    }

    /// <summary>Long enough that nobody wipes their turn by leaning on a key.</summary>
    private const double HoldToReset = 0.5;

    /// <summary>
    /// Keeps the camera on the action, framed between the two panels and never showing the
    /// void past the edge of the map.
    /// </summary>
    /// <remarks>
    /// Smoothed, because the target is the mean of four moles and any one of them being
    /// launched sideways would otherwise snap the whole view. Purely presentational: the
    /// simulation neither knows nor cares where anybody is looking.
    /// </remarks>
    private void UpdateCamera(double delta)
    {
        Vector2 viewport = GetViewportRect().Size;
        Vector2 focus = _beat == Beat.Resolving && _result?.Recording is not null
            ? ActionCentre()
            : ToPixels(_actor?.Position ?? Vec2.Zero);

        float scale = PixelsPerMetre / WorldScale.CellsPerMetre;
        float halfWidth = viewport.X / 2f;
        float band = viewport.Y - WorldTop - MatchHud.PromptHeight;
        float halfBand = band / 2f;

        float x = Mathf.Clamp(
            focus.X, halfWidth, Mathf.Max(halfWidth, (MapWidthCells * scale) - halfWidth));
        float y = Mathf.Clamp(
            focus.Y, halfBand, Mathf.Max(halfBand, (MapHeightCells * scale) - halfBand));

        Vector2 target = new Vector2(halfWidth - x, WorldTop + halfBand - y);

        _cameraAt = _cameraAt == Vector2.Zero
            ? target
            : _cameraAt.Lerp(target, Mathf.Min(1f, (float)delta * CameraChase));

        Position = _cameraAt;
    }

    /// <summary>How eagerly the camera chases, per second.</summary>
    private const float CameraChase = 6f;

    /// <summary>
    /// Where the interesting thing is: the four moles with plans, and nobody else.
    /// </summary>
    /// <remarks>
    /// Averaging all sixteen puts the camera in the middle of the map every round, where by
    /// definition nothing is happening. Averaging the four who are actually moving at least
    /// looks at the fight.
    ///
    /// When those four are spread wider than the screen this still cannot show all of it,
    /// which is precisely the problem the design answers with split screen: one view per
    /// player when they are far apart, a shared one when they are close. That is a Phase 3
    /// job, and until it exists a single camera on the mean is the honest stand-in.
    /// </remarks>
    private Vector2 ActionCentre()
    {
        RoundRecording recording = _result!.Recording!;
        int tick = CurrentTick();
        Vec2 total = Vec2.Zero;
        int counted = 0;

        foreach (int slot in _actorSlots)
        {
            if (slot < 0 || slot >= recording.MoleCount)
            {
                continue;
            }

            // An actor that has gone out is followed to where it went out, not past it.
            int at = _exitTick[slot] >= 0 ? Mathf.Min(tick, _exitTick[slot]) : tick;
            total += recording.PositionOf(at, slot);
            counted++;
        }

        return counted == 0
            ? ToPixels(recording.PositionOf(tick, 0))
            : ToPixels(total / Fix64.FromInt(counted));
    }

    // ---- Input ---------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        if (_beat != Beat.Planning || _actor is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                _penDown = left.Pressed;

                if (left.Pressed)
                {
                    // A fresh stroke replaces the old one. Ink only dries when the button
                    // comes up, which is what lets the pen be backed up.
                    _route.Clear();
                }

                RebuildPreview();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                if (right.Pressed)
                {
                    _aiming = true;
                    _aimAt = MouseInWorld();
                }
                else if (_aiming)
                {
                    _aiming = false;
                    StampShot();
                }

                break;

            case InputEventMouseMotion:
                if (_penDown)
                {
                    ExtendInk(MouseInWorld());
                }
                else if (_aiming)
                {
                    _aimAt = MouseInWorld();
                }

                break;

            default:
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space:
                Advance();
                break;

            case Key.Q:
                CycleWeapon(-1);
                break;

            case Key.E:
                CycleWeapon(1);
                break;

            case Key.Tab:
                CycleActor();
                break;

            default:
                break;
        }
    }

    private void Advance()
    {
        switch (_beat)
        {
            case Beat.Planning:
                Commit();
                break;

            case Beat.Resolving:
                // Skip to the end of a round somebody has seen enough of.
                _playback = PlaybackDuration();
                break;

            case Beat.Aftermath:
                BeginRoundOfPlanning();
                break;

            default:
                break;
        }
    }

    private int FirstLivingSeat()
    {
        for (int seat = 0; seat < PlayerCount; seat++)
        {
            if (_match.SeatIsAlive(seat))
            {
                return seat;
            }
        }

        return 0;
    }

    private void CycleWeapon(int direction)
    {
        if (_beat != Beat.Planning)
        {
            return;
        }

        int at = System.Array.IndexOf(Wheel, _weapon);
        _weapon = Wheel[(at + direction + Wheel.Length) % Wheel.Length];
    }

    private void CycleActor()
    {
        if (_beat != Beat.Planning || _actor is null)
        {
            return;
        }

        List<Mole> choices = new List<Mole>();

        foreach (Mole candidate in _match.Eligible(_planningSeat))
        {
            choices.Add(candidate);
        }

        if (choices.Count <= 1)
        {
            return;
        }

        _actor = choices[(choices.IndexOf(_actor) + 1) % choices.Count];
        _route.Clear();
        _stampedShot = null;
        _preview = null;
    }

    /// <summary>
    /// Lays or retracts ink. The pen may be backed up along its own stroke while the button
    /// is still down, and dries the moment it comes up.
    /// </summary>
    private void ExtendInk(Vec2 at)
    {
        if (_route.Count == 0)
        {
            _route.Add(at);
            RebuildPreview();
            return;
        }

        if (_route.Count >= 2
            && Vec2.Distance(at, _route[_route.Count - 2]) < PenStep / Fix64.FromInt(2))
        {
            _route.RemoveAt(_route.Count - 1);
            RebuildPreview();
            return;
        }

        if (Vec2.Distance(at, _route[_route.Count - 1]) > PenStep)
        {
            _route.Add(at);
            RebuildPreview();
        }
    }

    /// <summary>How far the cursor travels before another waypoint drops.</summary>
    private static Fix64 PenStep => Fix64.Ratio(3, 4);

    private void StampShot()
    {
        if (_actor is null)
        {
            return;
        }

        Vec2 from = _preview?.End ?? _actor.Position;
        Vec2 aim = _aimAt - from;

        if (aim.LengthSquared() == Fix64.Zero)
        {
            return;
        }

        // Power comes from how far the aim was dragged out, and the moment comes from
        // wherever the ghost had got to: the shot is stamped at the pen's tip.
        Fix64 reach = Fix64.Min(aim.Length(), FullPowerDrag);
        int power = Fix64.ToInt(reach / FullPowerDrag * Fix64.FromInt(byte.MaxValue));
        int tick = Mathf.Clamp(_preview?.TicksUsed ?? 0, 0, MatchSettings.TicksPerRound - 1);

        _stampedShot = PlanAction.Fire(
            tick, aim, (byte)Mathf.Clamp(power, 20, byte.MaxValue));
    }

    /// <summary>Drag distance, in metres, that charges a shot fully.</summary>
    private static Fix64 FullPowerDrag => Fix64.FromInt(20);

    // ---- Drawing -------------------------------------------------------------------

    public override void _Draw()
    {
        float scale = PixelsPerMetre / WorldScale.CellsPerMetre;

        DrawTextureRect(
            _terrain.Texture,
            new Rect2(0, 0, MapWidthCells * scale, MapHeightCells * scale),
            false);

        DrawLava();
        DrawCrates();

        if (_beat == Beat.Resolving && _result?.Recording is not null)
        {
            DrawResolution(_result.Recording, _result);
            return;
        }

        DrawStandingMoles();
        DrawPlan();
    }

    private void DrawLava()
    {
        if (_match.LavaLine == Fix64.MaxValue)
        {
            return;
        }

        Color lava = new Color(0.878f, 0.290f, 0.094f);
        float top = ToPixels(new Vec2(Fix64.Zero, _match.LavaLine)).Y;
        float mapWidth = MapWidthCells * PixelsPerMetre / WorldScale.CellsPerMetre;
        float mapHeight = MapHeightCells * PixelsPerMetre / WorldScale.CellsPerMetre;

        DrawRect(new Rect2(0, top, mapWidth, mapHeight - top), lava);

        if (_match.LavaLeftEdge == Fix64.MinValue)
        {
            return;
        }

        float left = ToPixels(new Vec2(_match.LavaLeftEdge, Fix64.Zero)).X;
        float right = ToPixels(new Vec2(_match.LavaRightEdge, Fix64.Zero)).X;

        DrawRect(new Rect2(0, 0, left, mapHeight), lava);
        DrawRect(new Rect2(right, 0, mapWidth - right, mapHeight), lava);
    }

    private void DrawCrates()
    {
        Color crateColour = new Color(0.55f, 0.38f, 0.18f);

        foreach (Crate crate in _match.Crates)
        {
            if (crate.Gone)
            {
                continue;
            }

            Vector2 at = ToPixels(crate.Position);

            if (!crate.HasLanded)
            {
                // The telegraph: a dotted line marking where it will come down, drawn during
                // planning so the scramble for it is something everybody scheduled.
                for (float y = at.Y - 260; y < at.Y - 14; y += 16)
                {
                    DrawLine(
                        new Vector2(at.X, y), new Vector2(at.X, y + 8),
                        new Color(crateColour, 0.5f), 2f);
                }
            }

            DrawRect(
                new Rect2(at.X - 10, at.Y - 10, 20, 20),
                crate.HasLanded ? crateColour : new Color(crateColour, 0.45f));
            DrawRect(new Rect2(at.X - 10, at.Y - 10, 20, 20), Colors.Black, false, 2f);
        }
    }

    private void DrawStandingMoles()
    {
        foreach (Mole mole in _match.Moles)
        {
            if (mole.IsOffDuty)
            {
                continue;
            }

            DrawMole(
                ToPixels(mole.Position), SeatColours[mole.Seat], mole.Pluck, mole == _actor);
        }
    }

    private void DrawResolution(RoundRecording recording, RoundResult result)
    {
        Fix64 at = Seconds(_playback);
        int tick = CurrentTick();

        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            if (recording.IsOffDutyAt(tick, slot))
            {
                DrawExit(recording, slot);
                continue;
            }

            DrawMole(
                ToPixels(recording.PositionAt(at, slot)),
                SeatColours[_match.Moles[slot].Seat],
                recording.PluckOf(tick, slot),
                highlight: false);
        }

        float shotRadius = (float)Projectile.Radius.ToDecimal() * PixelsPerMetre;

        foreach (Vec2 shot in recording.ShotsAt(tick))
        {
            DrawCircle(ToPixels(shot), Mathf.Max(shotRadius, 3f), new Color(0.18f, 0.14f, 0.10f));
        }

        DrawDamageNumbers(recording, result, tick);
    }

    /// <summary>
    /// Damage numbers, which rise from where they landed and then get out of the way.
    /// </summary>
    /// <remarks>
    /// Digits are the one numeral the design keeps, because they read the same in every
    /// language. They are also the one thing on screen that will happily pile up: leaving
    /// each one where it appeared means the end of a busy round is a wall of red, so each
    /// gets a second and a half and then goes.
    /// </remarks>
    private void DrawDamageNumbers(RoundRecording recording, RoundResult result, int tick)
    {
        int upTo = Mathf.Min(recording.HitsUpTo(tick), _hitTick.Length);

        for (int index = 0; index < upTo; index++)
        {
            int age = tick - _hitTick[index];

            if (age > DamageNumberTicks)
            {
                continue;
            }

            BlastHit hit = result.Hits[index];
            int slot = SlotOf(hit.Seat, hit.MoleIndex);

            if (slot < 0)
            {
                continue;
            }

            float life = age / (float)DamageNumberTicks;
            Vector2 at = ToPixels(recording.PositionOf(_hitTick[index], slot))
                + new Vector2(-8, -(MoleRadius * 2.6f) - (life * 26f));

            DrawString(
                ThemeDB.FallbackFont, at, hit.Damage.ToString(),
                HorizontalAlignment.Left, -1, 22,
                new Color(0.769f, 0.165f, 0.047f, 1f - (life * life)));
        }
    }

    /// <summary>A second and a half at thirty ticks a second.</summary>
    private const int DamageNumberTicks = 45;

    /// <summary>
    /// Two of the eight exits, roughed in. The reel is chosen by the simulation, so this
    /// only plays what it was told, and an exit with no animation yet gets the default.
    /// </summary>
    private void DrawExit(RoundRecording recording, int slot)
    {
        Mole mole = _match.Moles[slot];
        int exitTick = slot < _exitTick.Length ? _exitTick[slot] : -1;

        if (exitTick < 0)
        {
            return;
        }

        KnockoutExit? exit = ExitOf(mole);

        if (exit is null)
        {
            return;
        }

        Vector2 at = ToPixels(recording.PositionOf(exitTick, slot));
        float age = (float)_playback - (exitTick / (float)MatchSettings.TicksPerSecond);
        float life = Mathf.Clamp(age / 1.2f, 0f, 1f);
        Color colour = SeatColours[mole.Seat];

        switch (exit.Value)
        {
            case KnockoutExit.StretcherSquad:
                // Carried off, waving weakly, by two worms and a very small stretcher.
                Vector2 carried = at + new Vector2(life * 48f, -life * 6f);
                DrawLine(
                    carried + new Vector2(-14, 8), carried + new Vector2(14, 8), Colors.Black, 3f);
                DrawCircle(carried, 10f, colour);
                DrawCircle(carried + new Vector2(-18, 10), 5f, new Color(0.85f, 0.55f, 0.5f));
                DrawCircle(carried + new Vector2(18, 10), 5f, new Color(0.85f, 0.55f, 0.5f));
                break;

            default:
                // Spin and poof: spins faster, shrinks to nothing, and goes in a puff of
                // dust that leaves its boots standing. Without the dust the mole simply gets
                // smaller and vanishes, which reads as a rendering fault rather than a joke.
                if (life < 1f)
                {
                    DrawArc(
                        at, MoleRadius * (0.6f + (life * 2.2f)), 0, Mathf.Tau, 28,
                        new Color(0.847f, 0.749f, 0.596f, 1f - life), 4f);
                }

                Vector2 boot = new Vector2(MoleRadius * 0.5f, MoleRadius * 0.55f);
                Color leather = new Color(0.18f, 0.14f, 0.10f);

                DrawRect(new Rect2(at.X - boot.X - 1, at.Y + boot.Y, boot.X, boot.Y), leather);
                DrawRect(new Rect2(at.X + 1, at.Y + boot.Y, boot.X, boot.Y), leather);

                DrawCircle(
                    at + new Vector2(Mathf.Cos(life * 24f) * 5f, -life * 22f),
                    Mathf.Lerp(MoleRadius, 0f, life),
                    colour);
                break;
        }
    }

    private KnockoutExit? ExitOf(Mole mole)
    {
        foreach (Knockout knockout in _result!.Knockouts)
        {
            if (knockout.Seat == mole.Seat && knockout.MoleIndex == mole.Index)
            {
                return knockout.Exit;
            }
        }

        return null;
    }

    private int SlotOf(int seat, int moleIndex)
    {
        for (int slot = 0; slot < _match.Moles.Count; slot++)
        {
            if (_match.Moles[slot].Seat == seat && _match.Moles[slot].Index == moleIndex)
            {
                return slot;
            }
        }

        return -1;
    }

    private void DrawMole(Vector2 at, Color colour, int pluck, bool highlight)
    {
        float radius = MoleRadius;

        if (highlight)
        {
            DrawCircle(at, radius * 1.6f, new Color(colour, 0.3f));
        }

        DrawCircle(at, radius, colour);
        DrawCircle(at + new Vector2(-radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(0, radius * 0.34f), radius * 0.34f, new Color(0.85f, 0.55f, 0.5f));

        // Pluck as a bar over the head. A number there would be unreadable at this size,
        // and wordless is the direction anyway.
        float width = radius * 2.2f;
        float top = at.Y - (radius * 2.1f);

        DrawRect(new Rect2(at.X - (width / 2f), top, width, 4), new Color(0, 0, 0, 0.3f));
        DrawRect(new Rect2(at.X - (width / 2f), top, width * (pluck / 100f), 4), colour);
    }

    private void DrawPlan()
    {
        if (_actor is null)
        {
            return;
        }

        Color seat = SeatColours[_planningSeat];
        Color ink = new Color(0.18f, 0.14f, 0.10f, 0.6f);

        // The route in ink, with the stretch the ghost has already covered painted over it
        // in the platoon's colour. Two passes, so the whole plan is legible at once and the
        // ghost's progress along it is legible too.
        if (_preview is not null && _preview.Path.Count > 1)
        {
            int walked = (int)(_ghostClock * MatchSettings.TicksPerSecond) % _preview.Path.Count;

            for (int index = 1; index < _preview.Path.Count; index++)
            {
                DrawLine(
                    ToPixels(_preview.Path[index - 1]),
                    ToPixels(_preview.Path[index]),
                    ink,
                    3f);
            }

            for (int index = 1; index <= walked; index++)
            {
                DrawLine(
                    ToPixels(_preview.Path[index - 1]),
                    ToPixels(_preview.Path[index]),
                    seat,
                    5f);
            }

            // The ghost itself: the same mole, translucent, walking what was laid, while the
            // real one stands where it is.
            Vector2 ghost = ToPixels(_preview.Path[walked]);
            DrawCircle(ghost, MoleRadius, new Color(seat, 0.4f));
            DrawArc(ghost, MoleRadius, 0, Mathf.Tau, 24, seat, 2f);
        }

        // The waypoints as laid, so what the pen put down is distinguishable from what the
        // solver made of it. When the two disagree, that gap is the whole planning game.
        foreach (Vec2 point in _route)
        {
            DrawCircle(ToPixels(point), 3.5f, ink);
        }

        DrawAim();
    }

    /// <summary>
    /// Where the shot goes, drawn from where the mole will be standing when it fires rather
    /// than from where it is standing now. That is what "stamped at the pen's tip" means,
    /// and without the outline marking the spot the arrow looks like it belongs to nobody.
    /// </summary>
    private void DrawAim()
    {
        Vector2 muzzle = ToPixels(_preview?.End ?? _actor!.Position);
        Color aiming = new Color(0.780f, 0.353f, 0.157f);
        Color stamped = new Color(0.769f, 0.165f, 0.047f);

        if (_aiming)
        {
            DrawArc(muzzle, MoleRadius, 0, Mathf.Tau, 20, aiming, 2f);
            DrawLine(muzzle, ToPixels(_aimAt), aiming, 3f);
            DrawCircle(ToPixels(_aimAt), 6f, aiming);
            return;
        }

        if (_stampedShot is null)
        {
            return;
        }

        Vec2 aim = _stampedShot.Value.AimDirection();
        Vector2 direction = new Vector2((float)aim.X.ToDecimal(), (float)aim.Y.ToDecimal());
        Vector2 tip = muzzle + (direction * ArrowLength * (_stampedShot.Value.Power / 255f));
        Vector2 across = new Vector2(-direction.Y, direction.X) * 7f;

        DrawArc(muzzle, MoleRadius, 0, Mathf.Tau, 20, stamped, 2f);
        DrawLine(muzzle, tip, stamped, 3f);
        DrawColoredPolygon(
            new[] { tip + (direction * 12f), tip + across, tip - across }, stamped);
    }

    /// <summary>Arrow length at full power. Long enough to read the charge off it.</summary>
    private const float ArrowLength = 110f;

    // ---- Helpers -------------------------------------------------------------------

    private MatchHud.State BuildHudState() => new MatchHud.State
    {
        Beat = _beat.ToString(),
        Round = _match.Round + (_beat == Beat.Planning ? 1 : 0),
        Seat = _planningSeat,
        SeatColour = SeatColours[_planningSeat],
        Weapon = _weapon,
        MoleIndex = _actor?.Index ?? -1,
        StaminaSpent = (float)(_preview?.StaminaSpent.ToDecimal() ?? 0m),
        StaminaTotal = (float)(_actor?.Stamina.ToDecimal() ?? 0m),
        TicksUsed = _preview?.TicksUsed ?? 0,
        OverBudget = _preview?.RanOutOfPuff ?? false,
        ResetsLeft = ResetsLeft(),
        ResetHeld = (float)(_resetHeld / HoldToReset),
        HasShot = _stampedShot is not null,
        Wind = (float)_match.Wind.ToDecimal(),
        Standing = StandingPerSeat(),
        LastRoundDamage = _result?.TotalDamage ?? 0,
        Winner = _result?.WinningSeat ?? -1,
    };

    /// <summary>
    /// How many of each platoon are still up, as of whatever the viewer has actually seen.
    /// </summary>
    /// <remarks>
    /// Read from the recording during playback rather than from the moles themselves. A
    /// round resolves before the first frame of it is drawn, so counting live moles showed
    /// the final score for the whole eight seconds and gave away every knockout before it
    /// happened.
    /// </remarks>
    private int[] StandingPerSeat()
    {
        int[] standing = new int[PlayerCount];
        RoundRecording? recording = _beat == Beat.Resolving ? _result?.Recording : null;
        int tick = recording is null ? 0 : CurrentTick();

        for (int slot = 0; slot < _match.Moles.Count; slot++)
        {
            Mole mole = _match.Moles[slot];

            bool gone = recording is null
                ? mole.IsOffDuty
                : recording.IsOffDutyAt(tick, slot);

            if (!gone)
            {
                standing[mole.Seat]++;
            }
        }

        return standing;
    }

    private static Fix64 Seconds(double value) => Fix64.Ratio((int)(value * 1000), 1000);

    private static Vector2 ToPixels(Vec2 metres) =>
        new Vector2((float)metres.X.ToDecimal(), (float)metres.Y.ToDecimal()) * PixelsPerMetre;

    private Vec2 MouseInWorld()
    {
        Vector2 local = GetLocalMousePosition() / PixelsPerMetre;

        return new Vec2(
            Fix64.Ratio((int)(local.X * 256f), 256),
            Fix64.Ratio((int)(local.Y * 256f), 256));
    }
}
