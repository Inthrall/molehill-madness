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
    private readonly List<WorldView> _views = new List<WorldView>();

    /// <summary>How many platoons are playing, which the menu chose. Two, three or four.</summary>
    private int _players = MatchSetup.MostPlayers;

    private Beat _beat = Beat.Planning;
    private float _clock;
    private int _pointerSeat;

    private RoundResult? _result;
    private double _playback;
    private int _appliedChanges;
    private SplitLayout.Pane[] _replayPanes = System.Array.Empty<SplitLayout.Pane>();
    private Rect2 _replayBand;

    private AutoPilot? _autoPilot;
    private double _autoClock;
    private Sfx? _sfx;
    private int _sounded;
    private bool _forceSplit;

    private Scoreboard? _scoreboard;
    private int[] _damageTaken = System.Array.Empty<int>();
    private int[] _outAtRound = System.Array.Empty<int>();
    private double _finishedFor;

    public override void _Ready()
    {
        // The terrain is one pixel per cell blown up several times over, so it has to be
        // point-sampled. Filtered, the soil turns to smudge and the cell grid stops reading.
        TextureFilter = TextureFilterEnum.Nearest;

        _players = Mathf.Clamp(
            MatchSetup.PlayerCount, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);

        _match = MoleMatch.Create(_players, MatchSetup.Seed, MapWidthCells, MapHeightCells);
        _shadow = _match.Terrain.Clone();
        _terrain = new TerrainView(_shadow);
        _stage = new Stage(_match, _terrain.Texture, MapWidthCells, MapHeightCells);

        _shoulderHeldUp = new bool[_players];
        _shoulderHeldDown = new bool[_players];
        _plantHeld = new bool[_players];
        _hopHeld = new bool[_players];
        _driven = new bool[_players];
        _damageTaken = new int[_players];
        _outAtRound = new int[_players];

        _planners = new SeatPlanner[_players];

        for (int seat = 0; seat < _players; seat++)
        {
            _planners[seat] = new SeatPlanner(_match, seat);
        }

        _stage.Planners = _planners;
        AssignDevices();

        CanvasLayer overlay = new CanvasLayer();
        AddChild(overlay);

        _hud = new MatchHud();
        overlay.AddChild(_hud);

        _scoreboard = new Scoreboard();
        overlay.AddChild(_scoreboard);

        if (!Flags.Asked("--mute"))
        {
            _sfx = new Sfx();
            AddChild(_sfx);
        }

        if (Flags.WantsTouch())
        {
            _touch = new TouchControls();
            overlay.AddChild(_touch);

            // One kind of event for one kind of gesture. Godot will happily synthesise mouse
            // clicks from a finger, which would run every touch through the pointer path as well
            // and press each button twice; and it will synthesise a finger from the mouse, which
            // is what lets the phone layout be driven on a desktop with --touch. Wanted one way
            // round only, in both cases.
            Input.EmulateMouseFromTouch = false;
            Input.EmulateTouchFromMouse = true;
        }

        _forceSplit = Flags.Asked("--split");

        if (Flags.Asked("--demo"))
        {
            _autoPilot = new AutoPilot(_match);
        }

        if (Flags.Asked("--frail"))
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
        _devices = new Device[_players];
        _gamepad = new int[_players];
        Godot.Collections.Array<int> pads = Input.GetConnectedJoypads();

        _devices[0] = Device.Pointer;

        for (int seat = 1; seat < _players; seat++)
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

        for (int seat = 0; seat < _players; seat++)
        {
            if (_devices[seat] != Device.Shared && _match.SeatIsAlive(seat))
            {
                count++;
            }
        }

        return count;
    }

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
        }

        System.Array.Clear(_driven, 0, _driven.Length);
        _pointerSeat = NextPointerSeat(from: 0);
        RecentreViews();
    }

    /// <summary>
    /// Hands the cameras back to the game. A player who panned away to look at somebody else
    /// wants to be shown their own mole again when the next beat starts, not to have to find it.
    /// </summary>
    private void RecentreViews()
    {
        foreach (WorldView view in _views)
        {
            view.Recentre();
        }
    }

    /// <summary>
    /// Which platoon the pointer is driving. Only ones without a controller of their own queue
    /// for it, and it moves on as each commits.
    /// </summary>
    private int NextPointerSeat(int from)
    {
        for (int seat = from; seat < _players; seat++)
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
        _sounded = 0;
        _beat = Beat.Resolving;

        NoteWhenThingsHappened();
        ComposeReplayCameras();
        RecentreViews();
        KeepScore();
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
        _stage.Climax = PickTheMoment(exits, hits);
    }

    /// <summary>
    /// Which moment of the round the replay slows down and pushes in on.
    /// </summary>
    /// <remarks>
    /// The last knockout, because that is what a round is about and knockouts arrive at the end of
    /// one, which is the "final impact" the design asks for. Failing that the heaviest hit, so a
    /// round nobody went out of still ends on something. Failing both, nothing: a round where four
    /// moles walked about and missed does not want a slow motion replay of the missing.
    /// </remarks>
    private Climax PickTheMoment(int[] exits, int[] hits)
    {
        if (_result is null)
        {
            return Climax.None;
        }

        int latest = -1;
        int latestSlot = -1;

        foreach (Knockout knockout in _result.Knockouts)
        {
            int slot = SlotOf(knockout.Seat, knockout.MoleIndex);

            if (slot < 0 || slot >= exits.Length || exits[slot] <= latest)
            {
                continue;
            }

            latest = exits[slot];
            latestSlot = slot;
        }

        if (latestSlot >= 0)
        {
            return new Climax(latest, latestSlot);
        }

        int hardest = 0;
        int hardestAt = -1;
        int hardestSlot = -1;

        for (int index = 0; index < _result.Hits.Count && index < hits.Length; index++)
        {
            BlastHit hit = _result.Hits[index];

            if (hit.Damage <= hardest)
            {
                continue;
            }

            hardest = hit.Damage;
            hardestAt = hits[index];
            hardestSlot = SlotOf(hit.Seat, hit.MoleIndex);
        }

        return hardestSlot >= 0 ? new Climax(hardestAt, hardestSlot) : Climax.None;
    }

    /// <summary>
    /// Hands the round to the director, which cuts it into cameras once, before it plays.
    /// </summary>
    /// <remarks>
    /// Once, from the finished recording, rather than every frame. The round has already resolved
    /// by the time anybody watches it, so the whole shape of it is knowable up front, and deciding
    /// up front is the only way to stop the screen splitting and merging each time somebody is
    /// punted sideways.
    /// </remarks>
    private void ComposeReplayCameras()
    {
        RoundRecording? recording = _result?.Recording;
        _replayBand = Band();

        _replayPanes = recording is null
            ? SplitLayout.Shared(_replayBand)
            : ReplayDirector.Compose(
                recording, _stage.ExitTick, ReplaySubjects(), _replayBand, _forceSplit);
    }

    /// <summary>
    /// Everybody worth pointing a camera at: whoever had a plan, and whoever it happened to.
    /// </summary>
    /// <remarks>
    /// Victims and not only actors, because the interesting half of an artillery round is the end
    /// of the shell rather than the start of it. Framing only the moles who acted would keep the
    /// thrower in shot and cut away from the landing, which is the bit worth watching.
    /// </remarks>
    private List<int> ReplaySubjects()
    {
        List<int> subjects = new List<int>();
        bool[] taken = new bool[_match.Moles.Count];

        for (int slot = 0; slot < _match.Moles.Count; slot++)
        {
            if (IsActor(slot))
            {
                taken[slot] = true;
                subjects.Add(slot);
            }
        }

        if (_result is null)
        {
            return subjects;
        }

        foreach (BlastHit hit in _result.Hits)
        {
            Involve(SlotOf(hit.Seat, hit.MoleIndex), taken, subjects);
        }

        foreach (Knockout knockout in _result.Knockouts)
        {
            Involve(SlotOf(knockout.Seat, knockout.MoleIndex), taken, subjects);
        }

        return subjects;
    }

    private static void Involve(int slot, bool[] taken, List<int> subjects)
    {
        if (slot < 0 || taken[slot])
        {
            return;
        }

        taken[slot] = true;
        subjects.Add(slot);
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

    /// <summary>Whether a mole slot belongs to a platoon that acted this round.</summary>
    private bool IsActor(int slot)
    {
        Mole mole = _match.Moles[slot];

        return _planners[mole.Seat].Actor == mole;
    }

    /// <summary>
    /// Adds the round to the running tally the final scoreboard is built from.
    /// </summary>
    /// <remarks>
    /// Damage taken rather than dealt, because that is what the simulation records: a
    /// <see cref="BlastHit"/> names its victim, and nothing anywhere names who threw the thing.
    /// Recording the thrower would mean carrying attribution through every blast, gusher and lava
    /// bounce for the sake of a scoreboard, and "who took the most" is a fair proxy for how a
    /// platoon's afternoon went anyway.
    ///
    /// The round a platoon went out is kept so the scoreboard can order the losers by how long
    /// they lasted, which is the only ranking a free-for-all has below the winner.
    /// </remarks>
    private void KeepScore()
    {
        if (_result is null)
        {
            return;
        }

        foreach (BlastHit hit in _result.Hits)
        {
            if (hit.Seat >= 0 && hit.Seat < _damageTaken.Length)
            {
                _damageTaken[hit.Seat] += hit.Damage;
            }
        }

        for (int seat = 0; seat < _players; seat++)
        {
            if (_outAtRound[seat] == 0 && !_match.SeatIsAlive(seat))
            {
                _outAtRound[seat] = _result.Round;
            }
        }
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

            case Beat.Finished:
                _finishedFor += delta;
                DriveIfAsked(delta);
                break;

            default:
                DriveIfAsked(delta);
                break;
        }

        _terrain.Refresh();
        Relayout(delta);

        // The scoreboard is the whole screen once the match is over. Leaving the clock, the wind
        // and the running tally showing through it is three dead instruments behind a result.
        _hud.Visible = _beat != Beat.Finished;
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

        for (int seat = 0; seat < _players; seat++)
        {
            if (_devices[seat] == Device.Gamepad)
            {
                DriveWithGamepad(seat, delta);
            }
        }

        SteerPointerSeat(delta);
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
        _playback += delta * Pace();
        _stage.Tick = CurrentTick();
        _stage.Seconds = Fix64.Ratio((int)(_playback * 1000), 1000);
        CatchTerrainUp(_result!.Recording!.ChangesUpTo(_stage.Tick));
        SoundTheRound();

        if (_playback < PlaybackDuration())
        {
            return;
        }

        CatchTerrainUp(int.MaxValue);

        if (!_result.MatchOver)
        {
            _beat = Beat.Aftermath;
            return;
        }

        _beat = Beat.Finished;
        _finishedFor = 0;
        _scoreboard?.Show(FinalStandings());
    }

    /// <summary>
    /// The match as a table: winner first, then whoever lasted longest.
    /// </summary>
    /// <remarks>
    /// Ordering is the whole result in a free-for-all. There is no second place otherwise, only
    /// four platoons and the order they stopped being platoons in.
    /// </remarks>
    private Scoreboard.Standing[] FinalStandings()
    {
        Scoreboard.Standing[] rows = new Scoreboard.Standing[_players];
        int winner = _result?.WinningSeat ?? -1;

        for (int seat = 0; seat < _players; seat++)
        {
            int survivors = 0;

            foreach (Mole mole in _match.Moles)
            {
                if (mole.Seat == seat && !mole.IsOffDuty)
                {
                    survivors++;
                }
            }

            rows[seat] = new Scoreboard.Standing(
                seat, survivors, _damageTaken[seat], _outAtRound[seat], seat == winner);
        }

        System.Array.Sort(rows, (first, second) =>
        {
            if (first.Won != second.Won)
            {
                return first.Won ? -1 : 1;
            }

            // Never knocked out sorts as having lasted forever, which is what a draw is.
            int lasted = (second.OutAtRound == 0 ? int.MaxValue : second.OutAtRound)
                .CompareTo(first.OutAtRound == 0 ? int.MaxValue : first.OutAtRound);

            return lasted != 0 ? lasted : second.Survivors.CompareTo(first.Survivors);
        });

        return rows;
    }

    /// <summary>
    /// Makes the noises for every tick the clock has passed since the last frame.
    /// </summary>
    /// <remarks>
    /// Driven off the recording, exactly like the damage numbers, because the round finished
    /// before the first frame of it was drawn. A frame can span several ticks, so this walks
    /// them rather than looking only at the current one: skipping a tick would silently drop the
    /// bang that happened on it.
    ///
    /// A crater and a mole digging both change the terrain, and only one of them is an
    /// explosion, which is what the detonation count is for.
    /// </remarks>
    private void SoundTheRound()
    {
        if (_sfx is null || _result?.Recording is null)
        {
            return;
        }

        RoundRecording recording = _result.Recording;

        while (_sounded < _stage.Tick)
        {
            int previous = _sounded;
            _sounded++;

            bool banged = recording.DetonationsUpTo(_sounded) > recording.DetonationsUpTo(previous);
            bool dug = recording.ChangesUpTo(_sounded) > recording.ChangesUpTo(previous);
            int flying = recording.ShotsAt(_sounded).Count;
            int wasFlying = recording.ShotsAt(previous).Count;

            if (banged)
            {
                _sfx.Play(Sound.Boom, volumeDb: -4f);
            }
            else if (dug)
            {
                // Terrain changing with nothing going off is somebody tunnelling.
                _sfx.Play(Sound.Dig, volumeDb: -12f);
            }

            if (flying > wasFlying)
            {
                _sfx.Play(Sound.Launch, volumeDb: -8f);
            }

            if (recording.HitsUpTo(_sounded) > recording.HitsUpTo(previous))
            {
                _sfx.Play(Sound.Ouch, volumeDb: -5f);
            }

            if (recording.KnockoutsUpTo(_sounded) > recording.KnockoutsUpTo(previous))
            {
                _sfx.Play(Sound.Poof, volumeDb: -3f);
            }

            if (_sounded == CrateLandingTick && _match.Crates.Count > 0)
            {
                _sfx.Play(Sound.Thunk, volumeDb: -8f);
            }
        }
    }

    /// <summary>
    /// How fast the replay is running: full speed, until the round's big moment.
    /// </summary>
    /// <remarks>
    /// The clock is scaled rather than the tick, so everything slows together and stays in step:
    /// the moles, the terrain catching up, the damage numbers, the cameras, and the noises, each of
    /// which is driven off this one number. Playback still ends at the same point in the recording,
    /// because <see cref="_playback"/> counts simulation seconds rather than real ones. It simply
    /// takes longer to get there.
    /// </remarks>
    private double Pace()
    {
        if (!_stage.Climax.Exists)
        {
            return 1;
        }

        float weight = _stage.Climax.Weight(
            (float)(_playback * MatchSettings.TicksPerSecond));

        return Mathf.Lerp(1f, SlowMotion, weight);
    }

    /// <summary>How slowly the big moment plays. About a third, which reads as deliberate.</summary>
    private const float SlowMotion = 0.32f;

    /// <summary>Halfway through the round, which is when the crates arrive.</summary>
    private const int CrateLandingTick = MatchSettings.TicksPerRound / 2;

    private void Click()
    {
        _sfx?.Play(Sound.Click, volumeDb: -14f, pitchSpread: 0.05f);
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
                _views[index].Occupy(panes[index], index, delta);
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

        if (_beat == Beat.Planning)
        {
            if (_touch is not null)
            {
                // A phone is one player at a time, and its screen has no room to be divided.
                return SplitLayout.Shared(band);
            }

            return _forceSplit || SimultaneousSeats() >= 2
                ? SplitLayout.PerSeat(_players, band)
                : SplitLayout.Shared(band);
        }

        // The replay keeps the director's cut through the aftermath, so the last thing anybody
        // saw happen stays framed rather than cutting to a wide shot the moment it stops moving.
        if (_beat is Beat.Resolving or Beat.Aftermath && _replayPanes.Length > 0)
        {
            if (_replayBand != band)
            {
                // The window changed shape mid-replay, so the cut has to be re-framed for it.
                ComposeReplayCameras();
            }

            return _replayPanes;
        }

        return SplitLayout.Shared(band);
    }

    // ---- Pointer input ---------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_beat == Beat.Finished)
        {
            HandleScoreboard(@event);
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleKey(key);
            return;
        }

        // The wheel zooms in either layout. A phone has no wheel, but --touch on a desktop is the
        // only way the thumb layout gets looked at, and it is worth being able to zoom while doing
        // so rather than needing two fingers nobody has.
        if (@event is InputEventMouseButton { Pressed: true } button
            && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            ZoomAt(
                button.Position,
                button.ButtonIndex == MouseButton.WheelUp ? ZoomNotch : 1f / ZoomNotch);
            return;
        }

        if (_touch is not null)
        {
            HandleTouch(@event);
            return;
        }

        HandleMouse(@event);
    }

    /// <summary>
    /// The desktop pointer: a drag moves the camera, the wheel zooms it, and the right button
    /// still aims.
    /// </summary>
    /// <remarks>
    /// The left button used to draw the route, which is why the map could not be dragged. Now that
    /// the mole is steered with the keys, a drag on the map means what a drag on a map means
    /// everywhere else, and the one gesture the game had to invent for itself is gone.
    /// </remarks>
    private void HandleMouse(InputEvent @event)
    {
        SeatPlanner? planner = Pointed();

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                _panning = left.Pressed ? ViewAt(left.Position) : null;
                return;

            default:
                break;
        }

        if (_beat != Beat.Planning || planner is null)
        {
            // Panning and zooming work in every beat; aiming only makes sense in one.
            if (@event is InputEventMouseMotion drag && _panning is not null)
            {
                _panning.Pan(drag.Relative);
            }

            return;
        }

        switch (@event)
        {
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
                if (_panning is not null)
                {
                    _panning.Pan(motion.Relative);
                    break;
                }

                planner.MoveAim(PointerWorld(motion.Position, planner.Seat));
                break;

            default:
                break;
        }
    }

    /// <summary>How much one notch of the wheel zooms.</summary>
    private const float ZoomNotch = 1.12f;

    /// <summary>
    /// The phone: a finger on a control operates it, a finger on the map drags it, and two
    /// fingers on the map pinch.
    /// </summary>
    /// <remarks>
    /// Real screen-touch events rather than the synthesised mouse, because a pinch needs to know
    /// which finger is which and a mouse has only ever had one.
    /// </remarks>
    private void HandleTouch(InputEvent @event)
    {
        SeatPlanner? planner = Pointed();

        if (_touch is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventScreenTouch touch:
                if (touch.Pressed)
                {
                    BeginTouch(planner, touch.Index, touch.Position);
                }
                else
                {
                    EndTouch(planner, touch.Index);
                }

                break;

            case InputEventScreenDrag drag:
                ContinueTouch(planner, drag);
                break;

            default:
                break;
        }
    }

    /// <summary>One finger, and what it landed on.</summary>
    private sealed class Finger
    {
        public TouchTarget Target { get; set; }

        public Vector2 At { get; set; }
    }

    private readonly Dictionary<long, Finger> _fingers = new Dictionary<long, Finger>();
    private WorldView? _panning;
    private float _pinchSpan;
    private float _wheelTravel;
    private Vector2 _thumbPush;

    private void BeginTouch(SeatPlanner? planner, long index, Vector2 at)
    {
        TouchTarget target = planner is null || _beat != Beat.Planning
            ? TouchTarget.None
            : _touch!.Hit(at);

        _fingers[index] = new Finger { Target = target, At = at };

        if (target == TouchTarget.None)
        {
            _panning = ViewAt(at);
            _pinchSpan = 0;
            return;
        }

        _touch!.Press(target);

        switch (target)
        {
            case TouchTarget.Fire:
                planner!.BeginAim(planner.PlannedPosition);
                break;

            case TouchTarget.Commit:
                planner!.Commit();
                Click();
                break;

            case TouchTarget.Hop:
                if (planner!.BookHop())
                {
                    Click();
                }

                break;

            default:
                break;
        }
    }

    private void ContinueTouch(SeatPlanner? planner, InputEventScreenDrag drag)
    {
        if (!_fingers.TryGetValue(drag.Index, out Finger? finger))
        {
            return;
        }

        finger.At = drag.Position;

        if (finger.Target == TouchTarget.None)
        {
            DragTheMap(drag);
            return;
        }

        if (planner is null)
        {
            return;
        }

        switch (finger.Target)
        {
            case TouchTarget.Stick:
                // Direction only. There is one walking speed in every material, so how far the
                // stick is pushed cannot mean anything, and pretending otherwise would promise a
                // creep the simulation has no way to deliver.
                _thumbPush = drag.Position - _touch!.StickAt;
                _touch.StickPush = _thumbPush;
                break;

            case TouchTarget.Fire:
                // Direction and power out of one thumb: the further the stick is pulled, the
                // harder the throw, exactly as a mouse drag works.
                Vector2 pull = drag.Position - _touch!.FireAt;
                _touch.AimDrag = pull;
                planner.MoveAim(AimFromStick(planner, pull));
                break;

            case TouchTarget.Wheel:
                _wheelTravel += drag.Relative.Y;

                while (Mathf.Abs(_wheelTravel) >= _touch!.WheelNotch)
                {
                    planner.CycleWeapon(_wheelTravel > 0 ? 1 : -1);
                    _wheelTravel -= Mathf.Sign(_wheelTravel) * _touch.WheelNotch;
                    Click();
                }

                break;

            default:
                break;
        }
    }

    private void EndTouch(SeatPlanner? planner, long index)
    {
        if (!_fingers.TryGetValue(index, out Finger? finger))
        {
            return;
        }

        _fingers.Remove(index);

        switch (finger.Target)
        {
            case TouchTarget.None:
                _panning = null;
                _pinchSpan = 0;
                break;

            case TouchTarget.Stick:
                _thumbPush = Vector2.Zero;
                break;

            case TouchTarget.Fire:
                planner?.ReleaseAim();
                break;

            case TouchTarget.Dynamite:
                planner?.PlantCharge();
                Click();
                break;

            default:
                break;
        }

        _wheelTravel = 0;

        // Only let go of the controls when nothing is still holding one. Releasing on any lift
        // would unstick a button another thumb is still pressing.
        if (!AnyFingerOnAControl())
        {
            _touch!.Release();
            _thumbPush = Vector2.Zero;
        }
    }

    private bool AnyFingerOnAControl()
    {
        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Target != TouchTarget.None)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A finger on the map: one drags it, two pinch it.
    /// </summary>
    private void DragTheMap(InputEventScreenDrag drag)
    {
        if (TwoOnTheMap(out Vector2 first, out Vector2 second))
        {
            Pinch(first, second);
            return;
        }

        _pinchSpan = 0;
        _panning?.Pan(drag.Relative);
    }

    private void Pinch(Vector2 first, Vector2 second)
    {
        float span = first.DistanceTo(second);

        if (_panning is not null && _pinchSpan > 1f && span > 1f)
        {
            Vector2 between = (first + second) / 2f;
            _panning.ZoomBy(span / _pinchSpan, between - _panning.Position);
        }

        _pinchSpan = span;
    }

    private bool TwoOnTheMap(out Vector2 first, out Vector2 second)
    {
        first = Vector2.Zero;
        second = Vector2.Zero;
        int found = 0;

        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Target != TouchTarget.None)
            {
                continue;
            }

            if (found == 0)
            {
                first = finger.At;
            }
            else if (found == 1)
            {
                second = finger.At;
            }

            found++;
        }

        return found == 2;
    }

    /// <summary>Turns a thumb stick into an aim point out in the world.</summary>
    private Vec2 AimFromStick(SeatPlanner planner, Vector2 drag)
    {
        if (drag.LengthSquared() < 1f)
        {
            return planner.PlannedPosition;
        }

        float full = Mathf.Max(_touch!.WheelNotch * 3f, 1f);
        float charge = Mathf.Min(drag.Length() / full, 1f);
        Vector2 direction = drag.Normalized();
        Fix64 reach = SeatPlanner.FullPowerDrag * Fix64.Ratio((int)(charge * 256), 256);

        return planner.PlannedPosition + new Vec2(
            Fix64.Ratio((int)(direction.X * 256), 256) * reach,
            Fix64.Ratio((int)(direction.Y * 256), 256) * reach);
    }

    // ---- Steering --------------------------------------------------------------------

    /// <summary>
    /// Walks the platoon at the pointer, from whichever of a thumb or the keys is pushing.
    /// </summary>
    /// <remarks>
    /// Both, deliberately, rather than one or the other. The phone layout can then be driven from
    /// a desktop keyboard under <c>--touch</c>, which is the only way the thumb layout gets looked
    /// at without a phone in the room.
    /// </remarks>
    private void SteerPointerSeat(double delta)
    {
        Pointed()?.Steer(PointerPush(), delta);
    }

    private Vec2 PointerPush()
    {
        if (_touch is not null && _thumbPush.Length() >= _touch.StickTravel * ThumbDeadZone)
        {
            return Quantised(_thumbPush);
        }

        return Quantised(KeyboardPush());
    }

    private static Vector2 KeyboardPush() =>
        new Vector2(
            Held(Key.D, Key.Right) - Held(Key.A, Key.Left),
            Held(Key.S, Key.Down) - Held(Key.W, Key.Up));

    private static float Held(Key first, Key second) =>
        Input.IsKeyPressed(first) || Input.IsKeyPressed(second) ? 1f : 0f;

    /// <summary>
    /// Turns a screen direction into one the simulation will accept.
    /// </summary>
    /// <remarks>
    /// Quantised to a two-hundred-and-fifty-sixth on the way in, so that even the preview, which
    /// runs the real solver, is computing from a number a phone and a desktop would both arrive
    /// at. Floating point is fine on this side of the line and never crosses it.
    /// </remarks>
    private static Vec2 Quantised(Vector2 direction)
    {
        if (direction.LengthSquared() < 0.0001f)
        {
            return Vec2.Zero;
        }

        Vector2 unit = direction.Normalized();

        return new Vec2(
            Fix64.Ratio((int)(unit.X * 256f), 256),
            Fix64.Ratio((int)(unit.Y * 256f), 256));
    }

    /// <summary>How far a thumb must push, as a share of the stick's travel, to count.</summary>
    private const float ThumbDeadZone = 0.25f;

    private void TrackPointerReset(double delta)
    {
        SeatPlanner? planner = Pointed();

        if (planner is null)
        {
            return;
        }

        if (Input.IsKeyPressed(Key.R) || FingerOn(TouchTarget.Reset))
        {
            planner.HoldReset(delta);
        }
        else
        {
            planner.ReleaseReset();
        }
    }

    private bool FingerOn(TouchTarget target)
    {
        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Target == target)
            {
                return true;
            }
        }

        return false;
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

        // Outside every pane, so read it against the one the planner belongs to and let the aim
        // run off the edge rather than snapping somewhere arbitrary.
        return owned is null
            ? Vec2.Zero
            : owned.ToWorld(onScreen - owned.Position);
    }

    /// <summary>Which pane a point on the screen belongs to, for panning and zooming it.</summary>
    private WorldView? ViewAt(Vector2 onScreen)
    {
        foreach (WorldView view in _views)
        {
            if (view.Visible && new Rect2(view.Position, view.Size).HasPoint(onScreen))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>Zooms whichever pane a point is over, about that point.</summary>
    private void ZoomAt(Vector2 onScreen, float factor)
    {
        WorldView? over = ViewAt(onScreen);

        over?.ZoomBy(factor, onScreen - over.Position);
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
                Click();
                break;

            case Key.E:
                planner?.CycleWeapon(1);
                Click();
                break;

            case Key.Tab:
                planner?.CycleActor();
                Click();
                break;

            case Key.F:
                planner?.PlantCharge();
                Click();
                break;

            case Key.H:
                if (planner?.BookHop() == true)
                {
                    Click();
                }

                break;

            case Key.C:
                RecentreViews();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The scoreboard takes one press, and every press is the same press.
    /// </summary>
    /// <remarks>
    /// The button is drawn so there is something obvious to aim at, but a tap anywhere counts.
    /// Four people crowding round a phone to see who won should not then have to take turns
    /// finding a target, and there is nothing else on this screen a press could have meant.
    ///
    /// Held off for a moment first, so the press that skipped the last replay cannot carry through
    /// and dismiss the result nobody has read yet.
    /// </remarks>
    private void HandleScoreboard(InputEvent @event)
    {
        if (_finishedFor < ScoreboardSettles)
        {
            return;
        }

        bool pressed = @event switch
        {
            InputEventKey { Pressed: true, Echo: false } => true,
            InputEventScreenTouch { Pressed: true } => true,
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } => true,
            _ => false,
        };

        if (pressed)
        {
            BackToTheMenu();
        }
    }

    /// <summary>Long enough that the keypress which skipped the last replay does not carry on.</summary>
    private const double ScoreboardSettles = 0.6;

    private void BackToTheMenu()
    {
        // Deferred, because tearing the scene down from inside its own input handling is the
        // one thing Godot asks you not to do.
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
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
    /// One platoon's controller: left stick walks the mole, right stick aims, and the face
    /// buttons do the things that happen at a moment.
    /// </summary>
    /// <remarks>
    /// Never yet run against real hardware, and marked as such. The simultaneous planning it
    /// feeds is exercised by the test driver, which plans for several platoons at once, so what
    /// is owed a controller is only the axis and button reads themselves.
    ///
    /// Shorter than it was, because steering removed the need for a button to hold down while
    /// laying and a cursor to walk about the map independently of the mole.
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

        planner.Steer(stick.Length() > PadDeadZone ? Quantised(stick) : Vec2.Zero, delta);

        Vector2 aim = new Vector2(
            Input.GetJoyAxis(pad, JoyAxis.RightX), Input.GetJoyAxis(pad, JoyAxis.RightY));

        if (aim.Length() > PadDeadZone)
        {
            Fix64 reach = SeatPlanner.FullPowerDrag
                * Fix64.Ratio((int)(Mathf.Min(aim.Length(), 1f) * 256), 256);

            if (!planner.Aiming)
            {
                planner.BeginAim(planner.PlannedPosition);
            }

            Vector2 direction = aim.Normalized();
            planner.MoveAim(planner.PlannedPosition + new Vec2(
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

        bool hopping = Input.IsJoyButtonPressed(pad, JoyButton.Y);

        if (hopping && !_hopHeld[seat])
        {
            // Booked for this moment of the walk, which is the same one press a thumb makes.
            planner.BookHop();
        }

        _hopHeld[seat] = hopping;
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

    // Sized in _Ready rather than here, because the player count is a decision the menu makes
    // and a field initialiser cannot see another field.
    private bool[] _shoulderHeldUp = System.Array.Empty<bool>();
    private bool[] _shoulderHeldDown = System.Array.Empty<bool>();
    private bool[] _plantHeld = System.Array.Empty<bool>();
    private bool[] _hopHeld = System.Array.Empty<bool>();

    /// <summary>How far a gamepad axis must move before it counts as pushed.</summary>
    private const float PadDeadZone = 0.2f;

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

        if (_beat == Beat.Finished)
        {
            // Back to the menu, which under the driver starts another match at once. That makes a
            // long run a soak test of the whole loop rather than of one match, and it is the only
            // way the way out of a finished match gets exercised without a finger. It waits long
            // enough first that the result is on screen for a recorded frame to catch, which is
            // roughly as long as a person takes to look at it.
            if (_finishedFor > ScoreboardPause)
            {
                _autoClock = 0;
                BackToTheMenu();
            }

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

        // Walking and committing are separate beats a moment apart, so a recorded frame can
        // catch the planning screen mid-thought.
        bool walked = false;

        foreach (SeatPlanner planner in _planners)
        {
            if (!planner.IsPlanning || planner.Actor is null || _driven[planner.Seat])
            {
                continue;
            }

            // A notch of the wheel each turn, so the driver works its way through the arsenal
            // and the holdings it is spending from actually run down.
            planner.CycleWeapon(1);

            AutoPilot.Intent intent = _autoPilot.Decide(planner.Actor, planner.Weapon);

            WalkThrough(planner, intent);

            planner.BeginAim(intent.AimAt);
            planner.ReleaseAim();

            if (intent.PlantCharge)
            {
                planner.PlantCharge();
            }

            _driven[planner.Seat] = true;
            walked = true;
        }

        _autoClock = 0;

        if (walked)
        {
            return;
        }

        foreach (SeatPlanner planner in _planners)
        {
            planner.Commit();
        }
    }

    /// <summary>
    /// Steers a platoon along the route the driver wants, one tick at a time.
    /// </summary>
    /// <remarks>
    /// Through the same door a thumb uses. The driver could assemble a list of waypoints and hand
    /// it over directly, and it would be shorter, but then a round it drove would be a round no
    /// player could have played, which is the one thing this thing is not allowed to be.
    /// </remarks>
    private static void WalkThrough(SeatPlanner planner, AutoPilot.Intent intent)
    {
        for (int leg = 0; leg < intent.Route.Count; leg++)
        {
            // A hop partway along, booked while walking, because that is when a hop is booked
            // now. On a rota rather than at random, so it turns up in a recorded capture.
            if (intent.Hop && leg == intent.Route.Count / 2)
            {
                planner.BookHop();
            }

            for (int tick = 0; tick < TicksPerLeg && planner.HasTimeLeft; tick++)
            {
                Vec2 toward = intent.Route[leg] - planner.PlannedPosition;

                if (toward.Length() <= MatchSettings.Radius)
                {
                    break;
                }

                planner.StepToward(toward);
            }
        }
    }

    /// <summary>
    /// Ticks the driver will spend trying to reach one waypoint before giving up on it. Its legs
    /// are a couple of metres, which is about a dozen ticks, so this is generous on purpose:
    /// walking into a hillside should cost it the digging rather than stop it dead.
    /// </summary>
    private const int TicksPerLeg = 60;

    private bool[] _driven = System.Array.Empty<bool>();

    /// <summary>Long enough that a recorded frame catches the planning screen mid-thought.</summary>
    private const double AutoPause = 0.35;

    /// <summary>How long the driver leaves a result up before starting the next match.</summary>
    private const double ScoreboardPause = 1.6;

    // ---- Reporting -------------------------------------------------------------------

    private MatchHud.State BuildHudState()
    {
        SplitLayout.TrySpareCell(_players, Band(), out Rect2 spare);
        SplitLayout.Pane[] panes = Panes();
        bool splitting = panes.Length > 1;

        return new MatchHud.State
        {
            // Cleared past the top row of panes' own instruments, which the shared strip used to
            // sit on top of whenever a vertical seam ran down the middle of the screen.
            TopClearance = splitting
                ? WorldView.InstrumentDepth(panes[0].Rect.Size.Y) + 6f
                : 6f,
            ClockLeft = _beat == Beat.Planning ? Mathf.Max(_clock, 0f) : -1f,
            ClockLength = PlanningSeconds,
            Standing = StandingPerSeat(),
            Committed = CommittedPerSeat(),
            Wind = (float)_match.Wind.ToDecimal(),
            Round = _match.Round + (_beat == Beat.Planning ? 1 : 0),
            SpareCell = spare,
            HasSpareCell = splitting && _players == 3,
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
        int[] standing = new int[_players];
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
        bool[] committed = new bool[_players];

        for (int seat = 0; seat < _players; seat++)
        {
            committed[seat] = _beat == Beat.Planning && _planners[seat].Committed;
        }

        return committed;
    }
}
