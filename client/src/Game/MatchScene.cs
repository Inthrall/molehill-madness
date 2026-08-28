using System.Collections.Generic;
using Godot;
using Molehill.Online;
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

        /// <summary>Online only: waiting for the relay to say which world this is.</summary>
        Arriving,

        /// <summary>
        /// Reading the art in, before the first frame of the match is drawn.
        /// </summary>
        /// <remarks>
        /// A beat rather than a blocking call, which is what it was. The cost is the same either
        /// way; the difference is that a beat can draw, so the pause has a bar on it saying how far
        /// through it is instead of being a still frame of nothing.
        /// </remarks>
        Loading,

        /// <summary>
        /// Online only: this platoon has committed and the others have not.
        /// </summary>
        /// <remarks>
        /// Where an online match spends nearly all of its life, and in Anytime pace possibly the
        /// better part of a day. It is a beat rather than a flag on Planning because nothing about
        /// planning is happening: no input is wanted, no clock is running, and the screen has a
        /// different job.
        /// </remarks>
        Waiting,
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

        /// <summary>
        /// Somebody else's phone. Nothing on this device plans for it and nothing here may commit
        /// on its behalf, because its plan arrives from the relay.
        /// </summary>
        Elsewhere = 3,
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

    /// <summary>Every round's plans, so a clip can replay the match up to any moment.</summary>
    private readonly List<Plan[]> _played = new List<Plan[]>();

    /// <summary>Every round's result, so the drama scorer has a match to look at.</summary>
    private readonly List<RoundResult> _rounds = new List<RoundResult>();

    private Art.Warming? _warming;
    private LoadingBar? _loading;

    private PauseMenu? _pause;
    private KeyGuide? _guide;

    /// <summary>How long a relay gets to answer at all before the lobby stops waiting for it.</summary>
    private const double GivingUpAfter = 12d;

    private double _struggledFor;

    private Lobby? _lobby;
    private WaitingSign? _waiting;
    private EmoteWheel? _wheel;
    private bool _saidWhy;

    /// <summary>How often the driver says something. Slower than the relay would allow.</summary>
    private const double SaySomethingEvery = 4.0;

    private double _saidAt;
    private int _saidNext;
    private bool _saidCode;

    /// <summary>Which platoon is this device's, or -1 on the couch where they all are.</summary>
    private int _ours = -1;

    public override void _Ready()
    {
        // Smoothed and mipmapped, which is the opposite of what this was and of the reason it was
        // that. It point-sampled because the terrain used to be a texture of one pixel per cell
        // blown up several times over, and filtered, the soil turned to smudge. That stopped being
        // true when the shader took the terrain over: it samples the cell field through its own
        // filter hint and has done since, so the setting here has only been deciding how everything
        // else looks. Everything else is now artwork drawn smaller than it is stored, and
        // point-sampled a mole's outline comes out as a dotted line.
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;

        // Anything the panes do not cover is painted rather than left to the engine's default,
        // which is a blue-grey that reads as a rendering fault. A three-camera cut leaves the
        // fourth cell of the grid empty, and it should look like a deliberate dark surround.
        RenderingServer.SetDefaultClearColor(Palette.Ink);

        // Online, the world cannot be built yet. The seed and the player count both come from the
        // relay, because every client has to be digging the same ground and the relay is the only
        // thing they all talk to, so the match is built in Build once the seat has arrived.
        if (Online.Playing)
        {
            // Under a CanvasLayer like the rest of the interface. A Control parented straight to a
            // Node2D has no rect to anchor against, so it lays itself out at zero size and draws
            // nothing, which looks exactly like a scene that failed to load.
            CanvasLayer waiting = new CanvasLayer();
            AddChild(waiting);

            _lobby = new Lobby();
            waiting.AddChild(_lobby);

            _beat = Beat.Arriving;
            return;
        }

        Build();
    }

    /// <summary>
    /// Builds the world and everything that draws it.
    /// </summary>
    /// <remarks>
    /// Split out of <c>_Ready</c> for one reason: online, none of this can happen until the relay
    /// has said what seed to grow and how many platoons are in it.
    /// </remarks>
    private void Build()
    {
        _players = Mathf.Clamp(
            MatchSetup.PlayerCount, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);

        _match = MoleMatch.Create(_players, MatchSetup.Seed, MapWidthCells, MapHeightCells);
        _shadow = _match.Terrain.Clone();
        _terrain = new TerrainView(_shadow);

        // Frozen here, off the untouched map, and never worked out again. What is above ground and
        // what is below it is a fact about where the ground started; deriving it later from a map
        // full of craters and shafts would turn every tunnel into a canyon.
        _stage = new Stage(
            _match, _shadow, _terrain.Texture,
            Backdrop.Freeze(_match.Terrain, MatchSetup.Seed), MapWidthCells, MapHeightCells);

        // Before the first frame rather than during the first minute of them, and spread over a
        // few frames rather than blocking, so the pause can show how far through it is.
        _warming = Art.Warm(_players);

        _shoulderHeldUp = new bool[_players];
        _shoulderHeldDown = new bool[_players];
        _plantHeld = new bool[_players];
        _hopHeld = new bool[_players];
        _jumpHeld = new bool[_players];
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

        if (Online.Playing)
        {
            _waiting = new WaitingSign();
            overlay.AddChild(_waiting);

            _wheel = new EmoteWheel();
            overlay.AddChild(_wheel);
            _wheel.Watch(Online.Match);
        }

        if (!Flags.Asked("--mute"))
        {
            _sfx = new Sfx();
            AddChild(_sfx);
        }

        // The keyboard's answer to the thumb layout, and only when there is no thumb layout. A
        // phone already shows every control it has; a desktop showed none of them.
        if (!Flags.WantsTouch())
        {
            _guide = new KeyGuide();
            overlay.AddChild(_guide);
        }

        _pause = new PauseMenu();
        overlay.AddChild(_pause);

        // Over everything including the pause menu, because until the art is in there is nothing
        // to pause and nothing worth showing behind it.
        _loading = new LoadingBar();
        overlay.AddChild(_loading);

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

        // Held until the art is in. BeginRound draws moles.
        _beat = Beat.Loading;
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

        // Online there is exactly one platoon on this device, and it is not necessarily seat zero.
        // Every other seat belongs to somebody else's phone, and nothing here may plan or commit for
        // them: their plans arrive from the relay, and inventing one locally would be a desync.
        if (Online.Playing)
        {
            for (int seat = 0; seat < _players; seat++)
            {
                _devices[seat] = seat == _ours ? Device.Pointer : Device.Elsewhere;
            }

            return;
        }

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
        _stage.Replaying = false;
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
            if (_devices[seat] == Device.Gamepad || _devices[seat] == Device.Elsewhere)
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

    /// <summary>
    /// Whichever platoon is holding the pointer, or null when none is.
    /// </summary>
    /// <remarks>
    /// Bounds checked, because there is a beat where there are no planners at all. Online, the
    /// match cannot be built until the relay has said what seed to grow and how many platoons are
    /// in it, so a scene sits in <see cref="Beat.Arriving"/> with an empty planner list; and the
    /// pointer seat starts at zero rather than at minus one, because zero is what a fresh int is.
    /// Every mouse movement in that beat threw, which does not stop the game but does mean nothing
    /// responds to anything, and from the outside that is indistinguishable from a hang.
    /// </remarks>
    private SeatPlanner? Pointed() =>
        _pointerSeat >= 0
        && _pointerSeat < _planners.Length
        && _planners[_pointerSeat].IsPlanning
            ? _planners[_pointerSeat]
            : null;

    /// <summary>
    /// Whether every platoon this device is responsible for has committed.
    /// </summary>
    /// <remarks>
    /// Online that is one platoon, and the others are none of this device's business: they are
    /// planning on their own phones and the relay is what knows whether they have finished. Counting
    /// them here would leave a match waiting forever for a planner nobody is driving.
    /// </remarks>
    private bool EverybodyIsIn()
    {
        for (int seat = 0; seat < _players; seat++)
        {
            if (_devices[seat] == Device.Elsewhere)
            {
                continue;
            }

            if (_planners[seat].IsPlanning)
            {
                return false;
            }
        }

        return true;
    }

    private void Resolve()
    {
        List<Plan> played = new List<Plan>(_players);

        // Online the plans are already in, read back off the wire, and committing the planners here
        // would submit this device's idea of the other platoons' turns over the top of them.
        if (Online.Playing)
        {
            played.AddRange(Online.Match!.Plans);
        }
        else
        {
            foreach (SeatPlanner planner in _planners)
            {
                if (planner.Seal() is Plan plan)
                {
                    _match.SubmitPlan(plan);
                    played.Add(plan);
                }
            }
        }

        // This device has now planned a turn, which is what stops the drawn paw appearing again and is
        // what "first matches are seeded together" would consult once there is matchmaking to seed.
        if (_autoPilot is null)
        {
            Player.Planned1More();
        }

        // Kept, because a clip is the round played again and replaying round five needs rounds one
        // to five. The whole history of a match is a seed and this list, which is the same fact the
        // relay is built on and is what makes a clip cheap rather than a screen recording.
        _played.Add(played.ToArray());

        _result = _match.ResolveRound(record: true);
        _rounds.Add(_result);
        _stage.Result = _result;
        _stage.Recording = _result.Recording;
        _stage.Planning = false;
        _stage.Replaying = true;
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

        int[] blasts = new int[_result!.Blasts.Count];
        int gone = 0;

        for (int tick = 0; tick < recording.Ticks && gone < blasts.Length; tick++)
        {
            while (gone < recording.DetonationsUpTo(tick) && gone < blasts.Length)
            {
                blasts[gone] = tick;
                gone++;
            }
        }

        _stage.ExitTick = exits;
        _stage.HitTick = hits;
        _stage.BlastTick = blasts;
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
        // The relay is asked every frame and answers whenever it answers. Nothing here waits on it,
        // because a frame that blocks on a network call is a stutter every player sees.
        Online.Match?.Poll(delta);

        ChatterIfDriven(delta);

        if (_beat == Beat.Arriving)
        {
            RunArriving(delta);
            return;
        }

        if (_beat == Beat.Loading)
        {
            RunLoading();
            return;
        }

        switch (_beat)
        {
            case Beat.Planning:
                RunPlanning(delta);
                break;

            case Beat.Resolving:
                RunReplay(delta);
                break;

            case Beat.Waiting:
                RunWaiting();
                break;

            case Beat.Aftermath:
                RunAftermath(delta);
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

        _waiting?.Watch(Online.Match, _beat == Beat.Waiting);

        // The scoreboard is the whole screen once the match is over. Leaving the clock, the wind
        // and the running tally showing through it is three dead instruments behind a result.
        _hud.Visible = _beat != Beat.Finished;
        _hud.Apply(BuildHudState());

        // Only while somebody is planning. During a replay there is nothing to press, and a row of
        // keys that do nothing is the sort of thing a player tries and then distrusts.
        SeatPlanner? holding = _beat == Beat.Planning ? Pointed() : null;
        _guide?.Watch(holding, holding?.Seat ?? 0);

        if (_touch is not null)
        {
            _touch.LayOut(GetViewportRect().Size);
            _touch.Planner = Pointed();
            _touch.QueueRedraw();
        }

        // After the layout, so the paw points at where the controls actually ended up.
    }

    // ---- Playing apart ---------------------------------------------------------------

    /// <summary>
    /// Waiting on the relay for a seat, a seed and a full lobby, with nothing built yet.
    /// </summary>
    /// <remarks>
    /// The lobby is drawn from here rather than as its own scene, because the thing a host is
    /// waiting for and the thing they need on screen, the code to read out, both live on the
    /// OnlineMatch this scene already owns. A separate scene would have to be handed the same
    /// session and would then have to hand it back.
    /// </remarks>
    /// <summary>
    /// Reads a few more textures in, and starts the match when they are all there.
    /// </summary>
    /// <remarks>
    /// A handful a frame. One a frame would make the loading screen the slowest part of starting a
    /// match; the whole lot in one frame is what this replaced.
    /// </remarks>
    private void RunLoading()
    {
        _warming!.Step(TexturesPerFrame);
        _loading!.Show(_warming.Progress);

        if (!_warming.Finished)
        {
            return;
        }

        _loading.Done();
        BeginRound();
    }

    /// <summary>How many textures to read in per frame while the loading bar is up.</summary>
    private const int TexturesPerFrame = 4;

    private void RunArriving(double delta)
    {
        OnlineMatch online = Online.Match!;

        _lobby!.Show(online);

        // Giving up, eventually. A relay that is not running never changes the session's stage, so
        // Live stays true and the check below it never fires: the lobby waits for a machine that is
        // not there, forever, which is the second thing this beat has been mistaken for a hang over.
        // Pressing play with hosting selected and no relay on the desk is the ordinary way to reach
        // it, and it should not be a dead end.
        //
        // Back to the menu rather than an error on this screen, for the reason the stage check
        // already gives: the menu is the only screen that can do anything about it. Long enough
        // that a slow connection is not thrown away, short enough that nobody thinks it has frozen.
        _struggledFor = online.Struggling ? _struggledFor + delta : 0d;

        if (_struggledFor > GivingUpAfter)
        {
            GD.Print($"relay never answered at {Flags.Relay()}, back to the menu");
            Online.Forget();
            GetTree().CallDeferred(
                SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
            return;
        }

        // The token arrives with the seat and cannot be reissued, so it is written down the moment
        // it exists rather than when the match ends. A player who loses it has lost their seat with
        // no way to prove it was theirs.
        if (online.Seating is not null)
        {
            Online.Remember();

            // The same moment, for the same reason: this is the first point at which there is a code
            // and a token to open a socket with. Idempotent, so it costs nothing to ask every frame.
            Online.Listen();

            if (!_saidCode)
            {
                // Once, to the log. A code is the only handle anybody has on a match, so it is worth
                // being able to find one afterwards, and it is how two clients on one desk are
                // pointed at each other during development.
                _saidCode = true;
                GD.Print($"match {online.Code} seat {online.Seat} seed {online.Seed}");
            }
        }

        if (online.Struggling && !_saidWhy)
        {
            // Once, to the log. Every transport failure reaches the player as the same dots, and on a
            // phone the causes are not alike: no signal, no relay, a wrong address, or Android
            // refusing a cleartext HTTP request before it leaves the device. A tester needs to know
            // which.
            _saidWhy = true;
            GD.Print($"relay unreachable at {Flags.Relay()}: {Online.Relay.Trouble}");
        }

        if (!online.Live)
        {
            // A wrong code, a full lobby, or a relay that is not there. Back to the menu, which is
            // the only screen that can do anything about any of them.
            Online.Forget();
            GetTree().CallDeferred(
                SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
            return;
        }

        if (online.Stage != OnlineStage.Planning)
        {
            return;
        }

        // Everybody is seated. The relay's numbers win over anything the menu guessed.
        MatchSetup.PlayerCount = online.PlayerCount;
        MatchSetup.Seed = online.Seed;
        _ours = online.Seat;

        _lobby.Visible = false;
        Build();
    }

    /// <summary>
    /// This platoon has committed. Nothing to do but ask the relay whether the others have.
    /// </summary>
    private void RunWaiting()
    {
        OnlineMatch online = Online.Match!;

        if (!online.Live)
        {
            Online.Forget();
            GetTree().CallDeferred(
                SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
            return;
        }

        if (online.Stage != OnlineStage.RoundReady)
        {
            return;
        }

        // Every plan, mine included, read back from the bytes the relay released rather than from
        // the objects this device built. That is the determinism argument: four clients feeding
        // their simulations from one source cannot drift apart, and four feeding from four sources
        // that are only supposed to match can.
        //
        // An illegal plan is dropped rather than thrown, identically on every client, which is the
        // whole of the anti-cheat story: a cheat costs the cheat its turn and nobody desyncs.
        RoundFeeder.Feed(_match, online.Plans);

        int round = online.Round;

        Resolve();

        // What this client thought the world looked like, so a determinism bug on real hardware
        // arrives as a bug report with a perfect reproduction attached.
        online.ReportHash(round, _match.StateHash());
        online.RoundTaken();
    }

    /// <summary>
    /// Hands this platoon's plan to the relay and stops asking for input.
    /// </summary>
    /// <remarks>
    /// A platoon with nothing left to plan with still owes an answer, because the relay releases a
    /// round only when every seat has committed. An empty plan is that answer, and it is what a
    /// wiped-out platoon would have done anyway.
    /// </remarks>
    private void SendPlan()
    {
        Plan? mine = _ours >= 0 ? _planners[_ours].Seal() : null;

        mine ??= new Plan(
            System.Math.Max(_ours, 0), 0, WeaponId.None,
            System.Array.Empty<RoutePoint>(), System.Array.Empty<PlanAction>());

        Online.Match!.Commit(PlanCodec.Write(mine));
        _beat = Beat.Waiting;
        _stage.Planning = false;
    }

    /// <summary>
    /// The driver says something now and then, so a demo run exercises the emote path end to end.
    /// </summary>
    /// <remarks>
    /// Every frame rather than inside DriveIfAsked, which is gated behind a pause and only runs while
    /// planning. Waiting is where an online match spends nearly all of its life and is exactly when a
    /// player would be saying something, so a driver that only chattered while planning would leave
    /// the interesting half untested.
    /// </remarks>
    private void ChatterIfDriven(double delta)
    {
        if (_autoPilot is null || Online.Match is not Molehill.Online.OnlineMatch online)
        {
            return;
        }

        _saidAt += delta;

        if (_saidAt < SaySomethingEvery)
        {
            return;
        }

        _saidAt = 0;
        online.Say(Molehill.Online.Wheel.Order[_saidNext++ % Molehill.Online.Wheel.Count]);
    }

    private void RunPlanning(double delta)
    {
        // A pause menu that lets an eight second turn run down is not a pause menu, and on a
        // shared clock it would be a way of losing somebody else's round for them.
        if (_pause?.Showing == true)
        {
            return;
        }

        _clock -= (float)delta;

        for (int seat = 0; seat < _players; seat++)
        {
            if (_devices[seat] == Device.Gamepad)
            {
                DriveWithGamepad(seat, delta);
            }
        }

        SteerPointerSeat(delta);
        TrackIdle(delta);
        TrackPointerReset(delta);
        TrackCommitHold(delta);
        TrackAimHold(delta);
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

        if (!EverybodyIsIn() && _clock > 0)
        {
            return;
        }

        // Out of time commits whatever is on the paper, which is the whole reason the clock is
        // worth watching.
        if (Online.Playing)
        {
            SendPlan();
            return;
        }

        Resolve();
    }

    /// <summary>
    /// The beat between rounds: damage read, crates telegraphed, then on with the next one.
    /// </summary>
    /// <remarks>
    /// This is the beat that hung the game, and it hung it by doing nothing at all. Aftermath fell
    /// through the beat switch into the default case, so the only thing that ever moved a match past
    /// it was somebody pressing the key that also committed a plan and skipped a replay. Nothing on
    /// screen said so. A player who watched the replay end and then waited was waiting forever, and
    /// "hangs after one turn" is exactly what that looks like from the sofa.
    ///
    /// It went unnoticed for as long as it did because the autopilot driver calls BeginRound itself
    /// when it sees this beat, so every demo run and every headless pass sailed straight through the
    /// one transition a human had to know a secret to make.
    ///
    /// The design document has always called this a four second beat, so that is what it is now.
    /// Enter still skips ahead for anybody who has read the numbers and wants the next round.
    /// </remarks>
    private void RunAftermath(double delta)
    {
        DriveIfAsked(delta);

        if (_pause?.Showing == true)
        {
            return;
        }

        _afterFor += delta;

        if (_afterFor >= AftermathSeconds)
        {
            BeginRound();
        }
    }

    /// <summary>How long the tallies stay up before the next round starts on its own.</summary>
    private const double AftermathSeconds = 4;

    private double _afterFor;

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
            _afterFor = 0;
            _stage.Replaying = false;
            return;
        }

        _beat = Beat.Finished;
        _finishedFor = 0;
        _scoreboard?.Show(FinalStandings());

        if (Flags.Clip())
        {
            _ = MakeAClip();
        }
    }

    /// <summary>
    /// Picks the match's best moment and re-simulates it into a shareable animation.
    /// </summary>
    /// <remarks>
    /// Not awaited, because this happens as the scoreboard comes up and a player looking at a result
    /// should not be waiting on a renderer. The clip appears when it appears, which is what the
    /// design's one-press share flow will want anyway: the button offers what has already been made
    /// rather than starting the work.
    /// </remarks>
    private async System.Threading.Tasks.Task MakeAClip()
    {
        Molehill.Clip.Moment moment = Molehill.Clip.Drama.Best(_rounds);

        if (!moment.Exists)
        {
            GD.Print("clip: nothing in this match was worth one");
            return;
        }

        ClipMaker maker = new ClipMaker();
        AddChild(maker);

        Molehill.Clip.ClipFile? clip = await maker.Make(
            MatchSetup.Seed, _players, MapWidthCells, MapHeightCells, _played, moment);

        maker.QueueFree();

        if (clip is null)
        {
            GD.Print("clip: the moment could not be replayed");
            return;
        }

        string path = $"user://clip-round{moment.Round}.{clip.Extension}";

        using (FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            file?.StoreBuffer(clip.Bytes);
        }

        // The number the whole feature hangs on. The plan's open question about the clip pipeline is
        // whether five seconds of it can be encoded on a mid-range phone, and this is the only place
        // that can say. A desktop timing is not the answer, but a desktop timing an order of
        // magnitude out would be, so it is printed on every run rather than measured when somebody
        // remembers to.
        GD.Print(
            $"clip: {moment.Kind} round {moment.Round} tick {moment.Tick} score {moment.Score}, "
            + $"{clip.Format} {clip.Bytes.Length} bytes, {clip.Frames} frames, "
            + $"encoded in {clip.Took.TotalMilliseconds:F0} ms, "
            + $"{Verified(clip)}, "
            + $"at {ProjectSettings.GlobalizePath(path)}");
    }

    /// <summary>
    /// Whether the clip that came out is a file anything else could open.
    /// </summary>
    /// <remarks>
    /// An animated PNG goes back through Godot's own decoder, which is a decoder nobody here wrote,
    /// and its first frame is an ordinary PNG so anything that reads one at all should read this. If
    /// it cannot, the file is wrong however good the chunk layout looked.
    ///
    /// An MP4 gets the same question asked the only way it can be asked without a demuxer: the first
    /// box in the file has to be ftyp, which is four bytes of length followed by the four characters,
    /// and faststart is what puts it there. It is a weaker check and it is honest about being one,
    /// but it catches the failure that actually happens, which is ffmpeg writing an error where a
    /// file was expected.
    /// </remarks>
    private static string Verified(Molehill.Clip.ClipFile clip)
    {
        if (clip.Format == Molehill.Clip.ClipFormat.Mp4)
        {
            byte[] bytes = clip.Bytes;
            bool boxed = bytes.Length > 12
                && bytes[4] == (byte)'f' && bytes[5] == (byte)'t'
                && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';

            return boxed ? "ftyp ok" : "NOT AN MP4";
        }

        Image check = new Image();
        Error read = check.LoadPngFromBuffer(clip.Bytes);

        return $"reload {read} {check.GetWidth()}x{check.GetHeight()}";
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

        // The keyboard strip gets its own room rather than sitting over the map. It is there to be
        // read once, so it can afford the space, and a control legend drawn over the ground a mole
        // is standing on is worse than a slightly shorter map.
        float taken = _guide?.Visible == true ? KeyGuide.Height(viewport) : 0f;

        return new Rect2(0, 0, viewport.X, viewport.Y - taken);
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
        // Anything at all counts, including a nudge of the mouse. The point of the timer is whether
        // somebody is at the controls, not whether they have done something useful with them.
        _stage.Idle = 0f;

        if (_beat == Beat.Finished)
        {
            HandleScoreboard(@event);
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            _pause?.Toggle();
            Click();
            return;
        }

        // The offer of the other clock, while this device is in the pool. Above the guard below for
        // the same reason escape is: there is no match yet, and this is the one thing a player
        // waiting on one can usefully do.
        if (Online.Match is Molehill.Online.OnlineMatch queueing
            && queueing.Stage == Molehill.Online.OnlineStage.Queueing
            && _lobby is not null
            && _lobby.Offer.Size.X > 0
            && Pressing(@event, out Vector2 onOffer)
            && _lobby.Offer.HasPoint(onOffer))
        {
            queueing.Requeue(
                queueing.AskedFor == Molehill.Online.MatchPace.Live
                    ? Molehill.Online.MatchPace.Anytime
                    : Molehill.Online.MatchPace.Live);

            Click();
            return;
        }

        // Nothing below this point has a match to act on until one has been built. Escape is above
        // it deliberately: a player waiting on a relay that is never going to answer needs the one
        // door out of the room to work.
        if (_planners.Length == 0)
        {
            return;
        }

        // Nothing reaches the match while the menu is up, which is what makes it a pause rather
        // than an overlay. A press is offered to the menu first and goes no further either way.
        if (_pause?.Showing == true)
        {
            if (Pressing(@event, out Vector2 onMenu))
            {
                Chose(_pause.Pressed(onMenu));
            }

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

        // The wheel gets first refusal, before anything reaches the map. The map is a Node2D reading
        // raw events, so a Control sitting on top of it does not naturally win, and a tap that both
        // opened the wheel and steered a mole is the kind of fault that only turns up in a playtest.
        if (_wheel is not null && Pressing(@event, out Vector2 pressedAt) && _wheel.Pressed(pressedAt))
        {
            return;
        }

        if (_touch is not null)
        {
            HandleTouch(@event);
            return;
        }

        HandleMouse(@event);
    }

    /// <summary>Where a press landed, whichever kind of press it was.</summary>
    private static bool Pressing(InputEvent @event, out Vector2 at)
    {
        switch (@event)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                at = touch.Position;
                return true;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                at = click.Position;
                return true;

            default:
                at = Vector2.Zero;
                return false;
        }
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
                // Direction only, like the stick beside it and for the same reason. The power is
                // how long the button is held, which a thumb can do while it is still choosing
                // where to point, and which does not run out of room the way a pull does.
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

    /// <summary>
    /// Turns a thumb stick into an aim point out in the world.
    /// </summary>
    /// <remarks>
    /// A direction and nothing else. How far the thumb had dragged used to set the power, which on a
    /// stick with a fixed throw meant the useful range of powers lived in the last few millimetres
    /// of travel. The power is the hold now, so the point goes out at the full reach whichever way
    /// the stick is pushed.
    /// </remarks>
    private Vec2 AimFromStick(SeatPlanner planner, Vector2 drag)
    {
        if (drag.LengthSquared() < 1f)
        {
            return planner.PlannedPosition;
        }

        Vector2 direction = drag.Normalized();

        return planner.PlannedPosition + new Vec2(
            Fix64.Ratio((int)(direction.X * 256), 256) * SeatPlanner.AimReach,
            Fix64.Ratio((int)(direction.Y * 256), 256) * SeatPlanner.AimReach);
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
    /// <summary>
    /// Steers whoever is holding the pointer, and turns up on the surface into a jump.
    /// </summary>
    /// <remarks>
    /// Pushing up while standing on the ground did nothing at all. Measured on a flat surface it
    /// moved the mole one cell down over thirty ticks, which is the ground snap doing its job and
    /// the push doing none: there is nothing above a mole on the surface to climb, so the solver
    /// walks it into the sky, finds no ground, and the snap puts it back. A key that does nothing is
    /// worse than a key that does the wrong thing, because it reads as broken.
    ///
    /// So up is the jump, which already existed on its own key and as a planned action. Only on the
    /// surface: underground, up still digs upward, and that is the only way out of a tunnel.
    ///
    /// Up-dominant rather than any upward component, so walking up a slope with up and right held
    /// together is still walking. And on the press rather than the hold, because a hop is booked at
    /// a moment and a held key would book one every frame until the three are gone.
    /// </remarks>
    private void SteerPointerSeat(double delta)
    {
        SeatPlanner? planner = Pointed();

        if (planner is null)
        {
            return;
        }

        Vec2 push = PointerPush();

        if (Jumping(push) && planner.Actor is not null
            && !Moles.Underground(planner.PlannedPosition, _match.Terrain))
        {
            if (!_jumpHeld[planner.Seat] && planner.BookHop())
            {
                Click();
            }

            _jumpHeld[planner.Seat] = true;
            return;
        }

        _jumpHeld[planner.Seat] = false;
        planner.Steer(push, delta);
    }

    /// <summary>
    /// Counts how long everybody has sat still, which is what the turn arrow waits on.
    /// </summary>
    /// <remarks>
    /// Polled rather than only reset from input events, because a held key is one press followed by
    /// silence. Fed by events alone, a player walking a mole across the map would be called idle
    /// after a second of walking, which is the one moment nothing should be drawn over the mole.
    /// </remarks>
    private void TrackIdle(double delta)
    {
        _stage.Idle = Fiddling() ? 0f : _stage.Idle + (float)delta;
    }

    /// <summary>Whether anything is being held down, on any device anybody is using.</summary>
    private bool Fiddling()
    {
        if (PointerPush() != Vec2.Zero || Input.IsMouseButtonPressed(MouseButton.Left)
            || Input.IsMouseButtonPressed(MouseButton.Right))
        {
            return true;
        }

        for (int seat = 0; seat < _players; seat++)
        {
            if (_devices[seat] != Device.Gamepad)
            {
                continue;
            }

            int pad = _gamepad[seat];
            Vector2 stick = new Vector2(
                Input.GetJoyAxis(pad, JoyAxis.LeftX), Input.GetJoyAxis(pad, JoyAxis.LeftY));
            Vector2 aim = new Vector2(
                Input.GetJoyAxis(pad, JoyAxis.RightX), Input.GetJoyAxis(pad, JoyAxis.RightY));

            if (stick.Length() > PadDeadZone || aim.Length() > PadDeadZone)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a push is mostly upward, which on the surface means jump.</summary>
    private static bool Jumping(Vec2 push)
    {
        Fix64 up = -push.Y;

        return up > JumpingAbove && up > (push.X > Fix64.Zero ? push.X : -push.X);
    }

    /// <summary>How far up a push has to be pointed to read as a jump rather than as noise.</summary>
    private static Fix64 JumpingAbove => Fix64.Ratio(4, 10);

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

    /// <summary>
    /// Ending the turn: held rather than tapped, and polled rather than fed from key events.
    /// </summary>
    /// <remarks>
    /// Polled for the same reason the reset and the steering are: a held key is one press followed by
    /// silence, so there is no event to count against the hold. Keyboard only. The thumb layout's
    /// commit button is still a tap, deliberately left alone rather than changed sight unseen, which
    /// does leave the rule inconsistent between the two inputs.
    /// </remarks>
    private void TrackCommitHold(double delta)
    {
        SeatPlanner? planner = Pointed();

        if (planner is null)
        {
            return;
        }

        if (Input.IsKeyPressed(Key.Enter) || Input.IsKeyPressed(Key.KpEnter))
        {
            planner.HoldCommit(delta);
        }
        else
        {
            planner.ReleaseCommit();
        }
    }

    /// <summary>
    /// Winds up whoever is aiming. Every seat, not just the pointer's.
    /// </summary>
    /// <remarks>
    /// A pad seat aims by holding its right stick out and a pointer seat by holding the right
    /// button, and both are held rather than pressed, so neither has an event to count. Walked
    /// across every planner because a pad platoon aims at the same time as the pointer's does, and a
    /// charge that only advanced for whoever had the mouse would leave three of four seats throwing
    /// at the floor.
    /// </remarks>
    private void TrackAimHold(double delta)
    {
        foreach (SeatPlanner planner in _planners)
        {
            planner.HoldAim(delta);
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

    /// <summary>Acts on whatever the pause menu was asked for.</summary>
    private void Chose(PauseMenu.Choice choice)
    {
        switch (choice)
        {
            case PauseMenu.Choice.Resume:
                _pause!.Close();
                Click();
                break;

            case PauseMenu.Choice.Sound:
                _pause!.Sounding = !_pause.Sounding;

                // The bus rather than the player, so a sound already in flight stops too.
                AudioServer.SetBusMute(0, !_pause.Sounding);
                _pause.QueueRedraw();
                break;

            case PauseMenu.Choice.Menu:
                GetTree().CallDeferred(
                    SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
                break;

            default:
                break;
        }
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
                if (planner?.BookHop() == true)
                {
                    Click();
                }

                break;

            // Getting on with it, which means something different in each beat: skip a replay
            // somebody has seen enough of, or start the next round before its clock runs out. During
            // planning it does nothing on the press, because ending a turn is a hold and the hold is
            // counted in TrackCommitHold rather than here.
            case Key.Enter:
            case Key.KpEnter:
                if (_beat != Beat.Planning)
                {
                    Advance(planner);
                }

                break;

            case Key.Q:
                planner?.CycleWeapon(-1);
                Click();
                break;

            case Key.E:
                planner?.CycleWeapon(1);
                Click();
                break;

            case Key.F:
                planner?.PlantCharge();
                Click();
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
            if (!planner.Aiming)
            {
                planner.BeginAim(planner.PlannedPosition);
            }

            // Direction only. How far the stick is pushed no longer says anything about power, so
            // the aim point goes out at the full reach and the charge comes from how long the stick
            // is held out there.
            Vector2 direction = aim.Normalized();

            planner.MoveAim(planner.PlannedPosition + new Vec2(
                Fix64.Ratio((int)(direction.X * 256), 256) * SeatPlanner.AimReach,
                Fix64.Ratio((int)(direction.Y * 256), 256) * SeatPlanner.AimReach));
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

    /// <summary>Held state for up-as-jump, kept apart from the hop key's own.</summary>
    private bool[] _jumpHeld = System.Array.Empty<bool>();

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

            // Never a platoon on somebody else's phone. Their plan is coming from the relay, and
            // one invented here would be submitted over the top of it.
            if (_devices[planner.Seat] == Device.Elsewhere)
            {
                continue;
            }

            // A notch of the wheel each turn, so the driver works its way through the arsenal
            // and the holdings it is spending from actually run down.
            planner.CycleWeapon(1);

            AutoPilot.Intent intent = _autoPilot.Decide(planner.Actor, planner.Weapon);

            WalkThrough(planner, intent);

            // Through the same door a player uses: begin, hold, release. Handed the whole wind-up
            // in one delta rather than a frame at a time, because the driver plans a turn inside a
            // single frame and HoldAim clamps at full anyway.
            planner.BeginAim(intent.AimAt);
            planner.HoldAim(intent.Power * SeatPlanner.ChargeSeconds);
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

        // Online, committing means handing one plan to the relay and waiting, not sealing four
        // planners and resolving. Going the local route here would resolve a round this device made
        // up on its own while the other players were still thinking.
        if (Online.Playing)
        {
            SendPlan();
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
        SplitLayout.Pane[] panes = Panes();
        bool splitting = panes.Length > 1;

        // Keyed on how many panes there are rather than how many platoons. Three cameras leave the
        // fourth cell of the grid empty whether that is because three people are playing or because
        // the director cut the round three ways, and an empty quarter of the screen is a quarter of
        // the screen either way.
        SplitLayout.TrySpareCell(panes.Length, Band(), out Rect2 spare);

        // The keyboard strip owns the bottom of the screen while it is up, so the shared tally
        // moves above it rather than being drawn underneath it.
        float guide = _guide?.Visible == true
            ? KeyGuide.Height(GetViewportRect().Size)
            : 0f;

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
            HasSpareCell = splitting && panes.Length == 3,
            Split = splitting,
            BottomClearance = guide,
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
