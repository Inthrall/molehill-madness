using Godot;
using Molehill.Online;

/// <summary>
/// The lobby: play, who with, how many platoons, and how fast the clock runs.
/// </summary>
/// <remarks>
/// One question per row, read down the screen, and every row after the first only appears when the
/// row above it has left something to ask. Play stays in its corner, away from all of them, because
/// it is the button pressed at the end of every visit here and a thumb reaching for the same place
/// every time is worth more than putting it at the head of the sentence.
///
/// The top row is not "offline versus online", which is a word about plumbing. It is whether the
/// other three are people you know or people you do not, and that is the one distinction here that
/// changes the kind of match rather than the arrangements for it. It used to be four panels, with
/// the couch, hosting and joining side by side as though they were three different games; they are
/// one game with the other players in three different places, so they are three modes under one
/// venue now. Meeting strangers keeps a panel of its own because it is the only one that needs an
/// account, the only one an age band can refuse, and the only one where the count is not a choice.
///
/// A joiner is asked nothing below the mode, because the host already answered all of it and the
/// code is the only thing a joiner contributes.
///
/// Nearly wordless, like the rest of it. The count is shown as that many platoon-coloured moles
/// rather than as a numeral, because the design spends its one numeral on damage, and a row of moles
/// says "this many of you" more directly than a digit would. The words that survive are the ones
/// buying something a picture cannot: which venue, which mode, which pace. The two corner buttons
/// lost theirs, since a cog and a heart are settled enough and a wrong guess there costs a press
/// rather than a match.
/// </remarks>
public partial class MenuScene : Control
{
    private int _players = MatchSetup.MostPlayers;

    /// <summary>
    /// Which table the menu opens on.
    /// </summary>
    /// <remarks>
    /// Settled in <c>_Ready</c> rather than here, because whether the player may sit with strangers
    /// depends on an age band read off the disk and there is no reading anything from a field
    /// initialiser. The couch is the fallback and is the right one: it is the table every account
    /// may sit at.
    /// </remarks>
    private MatchSetup.Table _where = MatchSetup.Table.Couch;
    private MatchPace _pace = MatchPace.Live;

    private readonly Rect2[] _choices = new Rect2[MatchSetup.MostPlayers - MatchSetup.FewestPlayers + 1];

    /// <summary>The two venues on the top row: people you know, and people you do not.</summary>
    private readonly Rect2[] _venues = new Rect2[2];

    /// <summary>
    /// The three ways to play with people you know, on the row under the venue.
    /// </summary>
    /// <remarks>
    /// Local is one of these rather than a venue of its own, which is the shape the menu was asked
    /// for and is also the honest grouping: all three are a match with people you already know, and
    /// what they differ in is only where those people are sitting. Meeting strangers is the one that
    /// is genuinely a different kind of match, and it is the one that keeps a panel of its own.
    /// </remarks>
    private readonly Rect2[] _modes = new Rect2[3];

    private readonly Rect2[] _paces = new Rect2[2];
    private Rect2 _resume;
    private bool _canResume;

    /// <summary>
    /// Which of the three friendly modes was last picked, so coming back to Friends lands where it
    /// was left rather than resetting to the couch every time the venue is touched.
    /// </summary>
    private MatchSetup.Table _friends = MatchSetup.Table.Couch;

    private Vector2 _play;
    private float _button;
    private MenuSheet? _sheet;
    private bool _startAtOnce;
    private bool _needsGate;

    public override void _Ready()
    {
        // A development run goes on the laptop panel rather than over the top of whatever is on the
        // monitors. The first scene the process opens, so it happens before anything is drawn.
        Screens.ToThePanelIfAsked();

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // Before anything can play. A remembered mute that only takes effect once the player opens
        // the settings would let the game shout once on every launch.
        Options.Apply();

        _sheet = new MenuSheet();
        AddChild(_sheet);

        // The driver has nobody to press anything, so it walks straight through. Deferred by a
        // frame rather than done here, because changing scene from inside _Ready tears down the
        // tree that is still being built.
        _startAtOnce =
            Flags.Driven() || Flags.Host() || Flags.Matchmake() || Flags.Join() is not null;

        // Finding a game is what somebody opening the menu on their own wants, and until now the
        // default was the couch, which needs three other people in the room. A player with nobody
        // beside them had to notice a second table and move to it before the play button meant
        // anything, and the design's own worry about a thin population is not helped by hiding the
        // queue behind a press. Refused for an account that may not meet strangers, which is the one
        // case where the couch is genuinely the only table on offer.
        //
        // Not under the driver, which walks straight past this screen into whatever it finds
        // selected. A driven run that queued for strangers would sit in a pool waiting for three
        // people who are not coming, and every render check and perf sweep in the repo starts with
        // one of those runs.
        if (!Flags.Driven())
        {
            _where = Allows(MatchSetup.Table.Strangers)
                ? MatchSetup.Table.Strangers
                : MatchSetup.Table.Couch;
        }

        if (Flags.Host())
        {
            _where = MatchSetup.Table.Hosting;
        }
        else if (Flags.Matchmake())
        {
            _where = MatchSetup.Table.Strangers;
        }
        else if (Flags.Join() is not null)
        {
            _where = MatchSetup.Table.Joining;
        }

        if (Flags.Players() is int asked)
        {
            _players = Mathf.Clamp(asked, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);
        }

        // The design asks at first run, before anything else happens. Deferred by a frame for the same
        // reason the driver is: changing scene from inside _Ready tears down the tree being built.
        // The driver walks past it, because a test run has nobody to answer and the answer it would
        // need is not the thing under test.
        _needsGate = Player.NeedsGate && !Flags.Driven();

        // A match this device is already in outranks starting a new one, and the player has to be
        // able to get back to it without remembering a code they were told once.
        _canResume = Online.Remembers();

        if (Flags.WantsTouch())
        {
            Input.EmulateMouseFromTouch = false;
            Input.EmulateTouchFromMouse = true;
        }
    }

    public override void _Process(double delta)
    {
        if (_needsGate)
        {
            _needsGate = false;
            GetTree().CallDeferred(
                SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Gate.tscn");
            return;
        }

        if (_startAtOnce)
        {
            _startAtOnce = false;
            Start();
            return;
        }

        QueueRedraw();
    }

    // ---- Input -----------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                return;

            case InputEventScreenTouch { Pressed: true } touch:
                HandlePress(touch.Position);
                return;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                HandlePress(click.Position);
                return;

            default:
                return;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space:
            case Key.Enter:
            case Key.KpEnter:
                Start();
                break;

            case Key.Left:
            case Key.A:
                Choose(_players - 1);
                break;

            case Key.Right:
            case Key.D:
                Choose(_players + 1);
                break;

            case Key.Up:
            case Key.W:
                Sit(Along(-1));
                break;

            case Key.Down:
            case Key.S:
                Sit(Along(1));
                break;

            // Typing the number is input rather than reading it, so the wordless rule is intact.
            case Key.Key2:
                Choose(2);
                break;

            case Key.Key3:
                Choose(3);
                break;

            case Key.Key4:
                Choose(4);
                break;

            default:
                break;
        }
    }

    private void HandlePress(Vector2 at)
    {
        // The sheet is over everything, so while it is up nothing behind it can be pressed.
        if (_sheet?.Showing == true)
        {
            return;
        }

        if (_settings.HasPoint(at))
        {
            _sheet?.Show(MenuSheet.Page.Settings);
            return;
        }

        if (_credits.HasPoint(at))
        {
            _sheet?.Show(MenuSheet.Page.Credits);
            return;
        }

        // Resume first. Once both buttons are on screen their generous touch targets overlap in the
        // middle, and the one that would win by accident should not be the one that abandons a match
        // in progress.
        if (_canResume && _resume.HasPoint(at))
        {
            Continue();
            return;
        }

        if (at.DistanceTo(_play) <= _button * 1.35f)
        {
            Start();
            return;
        }

        // The venue first, then the mode under it. Pressing Friends puts the player back on
        // whichever of the three they were last on rather than on the couch, so touching the venue
        // to see what is under it does not quietly change the match they had set up.
        if (_venues[0].HasPoint(at))
        {
            Sit(_friends);
            return;
        }

        if (_venues[1].HasPoint(at))
        {
            Sit(MatchSetup.Table.Strangers);
            return;
        }

        if (AmongFriends)
        {
            for (int index = 0; index < _modes.Length; index++)
            {
                if (_modes[index].HasPoint(at))
                {
                    Sit((MatchSetup.Table)index);
                    return;
                }
            }
        }

        if (Paces())
        {
            for (int index = 0; index < _paces.Length; index++)
            {
                if (_paces[index].HasPoint(at))
                {
                    _pace = (MatchPace)index;
                    QueueRedraw();
                    return;
                }
            }
        }

        if (Picks())
        {
            for (int index = 0; index < _choices.Length; index++)
            {
                if (_choices[index].HasPoint(at))
                {
                    Choose(MatchSetup.FewestPlayers + index);
                    return;
                }
            }
        }
    }

    /// <summary>Whether this table lets the player choose how many are playing.</summary>
    /// <remarks>
    /// A joiner does not: the host already decided, and the lobby says so. Nor does anybody meeting
    /// strangers, where it is always four; see <see cref="Seats"/> for why.
    /// </remarks>
    private bool Picks() =>
        _where is MatchSetup.Table.Couch or MatchSetup.Table.Hosting;

    /// <summary>Whether this table also lets them choose the clock.</summary>
    /// <remarks>
    /// Hosting and strangers both do, and for the same reason: in each case this device is asking
    /// for a match to be made rather than joining one that exists, so the pace is still open. A
    /// joiner takes whatever the host chose, and a couch has no clock to choose.
    /// </remarks>
    private bool Paces() =>
        _where is MatchSetup.Table.Hosting or MatchSetup.Table.Strangers;

    /// <summary>
    /// Whether this device may sit at a table at all.
    /// </summary>
    /// <remarks>
    /// Only ever false for strangers, and only for an account under the threshold. The design is
    /// explicit that "nothing is taken away by that gate except strangers": the couch is open to
    /// everybody and needs no account, and a game code is open to every age because it arrives from
    /// somebody the player already knows.
    /// </remarks>
    private static bool Allows(MatchSetup.Table table) =>
        table != MatchSetup.Table.Strangers || Online.CanMeetStrangers;

    /// <summary>The next table along that this device may actually sit at.</summary>
    private MatchSetup.Table Along(int step)
    {
        MatchSetup.Table next = _where;

        // At most one lap, so a build where nothing is allowed cannot spin here for ever.
        for (int tried = 0; tried < Tables; tried++)
        {
            next = (MatchSetup.Table)Mathf.PosMod((int)next + step, Tables);

            if (Allows(next))
            {
                return next;
            }
        }

        return _where;
    }

    /// <summary>
    /// How many tables there are, which is not how many panels are on the top row.
    /// </summary>
    /// <remarks>
    /// Up and down still walk all four in the order the enum declares them, which is the order they
    /// appear in reading down the screen: local, host, join, online. The grouping into a venue and a
    /// mode is a thing the eye does, and binding the keys to it would mean a keyboard player needing
    /// two different keys to reach two options that look like a list.
    /// </remarks>
    private const int Tables = 4;

    private void Choose(int players)
    {
        _players = Mathf.Clamp(players, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);
        QueueRedraw();
    }

    private void Sit(MatchSetup.Table table)
    {
        if (!Allows(table))
        {
            // Refused rather than selected and then failed at the play button. A dimmed panel that
            // does nothing when pressed says "not this one" where a bright panel and a dead play
            // button says the game is broken.
            return;
        }

        _where = table;

        if (table != MatchSetup.Table.Strangers)
        {
            _friends = table;
        }

        QueueRedraw();
    }

    /// <summary>Whether the venue on the top row is the friendly one.</summary>
    private bool AmongFriends => _where != MatchSetup.Table.Strangers;

    /// <summary>
    /// How many seats the match actually asks for.
    /// </summary>
    /// <remarks>
    /// Four whenever this device is meeting strangers, and the count row is not offered there at
    /// all: a pool that has to fill before anybody plays is worse the more sizes it is split into,
    /// and four is the design's default and its ceiling. The remembered count is left alone rather
    /// than overwritten, so a player who set the couch to three and then went looking for a game
    /// still finds three waiting when they come back.
    /// </remarks>
    private int Seats() =>
        _where == MatchSetup.Table.Strangers ? MatchSetup.MostPlayers : _players;

    private void Start()
    {
        MatchSetup.PlayerCount = Seats();
        MatchSetup.Where = _where;
        MatchSetup.Pace = _pace;

        switch (_where)
        {
            case MatchSetup.Table.Hosting:
                Online.Host(Seats(), _pace, Flags.Window() ?? 0);
                break;

            case MatchSetup.Table.Strangers:
                Online.Matchmake(Seats(), _pace);
                break;

            case MatchSetup.Table.Joining when Flags.Join() is string prefilled:
                // A share link, or two clients on one desk. Skips the code screen entirely.
                Online.Join(prefilled);
                break;

            case MatchSetup.Table.Joining:
                GetTree().CallDeferred(
                    SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Code.tscn");
                return;

            default:
                // Going to the couch puts the online session down without giving the seat up. A
                // match in progress is still in progress, and the player can come back to it.
                Online.Drop();
                break;
        }

        // A rematch should be a different garden, so the seed moves. Under the driver it does not,
        // because a match that changed every run would break every comparison the render checks
        // are built on, and a named seed beats both. Online, the relay draws it: every client has
        // to be digging the same ground, and the relay is the only thing they all talk to.
        MatchSetup.Seed = Flags.Seed()
            ?? (Flags.Driven()
                ? MatchSetup.DrivenSeed
                : (ulong)(Time.GetUnixTimeFromSystem() * 1000d));

        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Match.tscn");
    }

    /// <summary>Goes back into the match this device was already in.</summary>
    private void Continue()
    {
        if (!Online.Resume())
        {
            _canResume = false;
            QueueRedraw();
            return;
        }

        MatchSetup.Where = MatchSetup.Table.Joining;

        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Match.tscn");
    }

    // ---- Drawing ---------------------------------------------------------------------

    public override void _Draw()
    {
        Vector2 viewport = Size;

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);
        MenuHill.Draw(this, viewport);

        _button = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.075f, 30f, 64f);

        DrawTitle(viewport);
        DrawVenues(viewport);
        DrawSecondRow(viewport);
        DrawPlay(viewport);
        DrawAside(viewport);
    }

    /// <summary>
    /// The game's name, in the sky above everything else.
    /// </summary>
    /// <remarks>
    /// This was four platoons coming up out of the hill in their four colours, on the grounds that it
    /// was as close to a title as a wordless game gets. There is a painted title now, with a mole
    /// coming out of a molehill in the middle of it, so the badge was saying the same thing twice
    /// over and one of the two was doing it better.
    ///
    /// Fitted to the band of sky between the top of the window and the row of tables rather than to
    /// a fraction of the width, and limited by both. Sized by width alone it grew past the tables on
    /// a wide window; sized by height alone it was a stamp on a tall one.
    /// </remarks>
    private void DrawTitle(Vector2 viewport)
    {
        Texture2D art = Art.MenuTitle;
        Vector2 sheet = art.GetSize();

        float top = viewport.Y * 0.05f;
        float floor = VenuesTop(viewport) - (_button * 0.5f);
        float band = Mathf.Max(floor - top, 1f);

        float wide = Mathf.Min(viewport.X * 0.55f, band * sheet.X / sheet.Y);
        float tall = wide * sheet.Y / sheet.X;

        DrawTextureRect(
            art,
            new Rect2(
                (viewport.X - wide) / 2f,
                top + ((band - tall) / 2f),
                wide,
                tall),
            false);
    }

    /// <summary>
    /// One platoon, as a face on a disc of its own colour.
    /// </summary>
    /// <remarks>
    /// The disc is where the team colour lives, because the face cannot carry one. Every other mole
    /// in the game is coloured by its trunks, and a head has no trunks; the cap is the only part of
    /// this artwork that could take a colour and it is the same white as the eyes, to the value. So
    /// the colour goes behind rather than on, which is what a team badge does anyway.
    ///
    /// A ring as well as a fill, a shade darker, so the disc reads as a badge rather than as a
    /// smudge behind the head.
    /// </remarks>
    private void Face(Vector2 at, float glyph, Color colour, bool chosen)
    {
        float radius = glyph * 0.62f;

        DrawCircle(at, radius, colour);
        DrawArc(at, radius, 0f, Mathf.Tau, 30, new Color(Palette.Ink, chosen ? 0.45f : 0.2f), 2f);

        Strip faces = Art.Faces;
        Vector2 box = faces.FrameSize * (glyph * 1.05f / faces.FrameSize.Y);

        faces.Draw(
            this,
            new Rect2(at.X - (box.X / 2f), at.Y - (box.Y / 2f), box),
            Art.Face.Level,
            mirrored: false,
            chosen ? Colors.White : new Color(1f, 1f, 1f, 0.55f));
    }

    /// <summary>Where the row of tables starts and how tall it is.</summary>
    /// <remarks>
    /// Named for the same reason the choices row's are: everything under it is placed by chaining
    /// off it, and the moment any of that was written out as its own fraction of the screen the
    /// rows started landing on top of each other whenever one of them changed height.
    /// </remarks>
    /// <summary>Where the venue row sits, and how tall it is.</summary>
    /// <remarks>
    /// Everything under it is placed off these two, so the whole stack moves together. The modes,
    /// the count and the paces each measure from the row above rather than from the top of the
    /// screen, which is what lets rows come and go without leaving a gap the height of a row that
    /// is not being drawn.
    /// </remarks>
    private float VenuesTop(Vector2 viewport) => viewport.Y * 0.44f;

    private float VenuesHeight() => _button * 1.4f;

    /// <summary>
    /// Who with: people you know, or people you do not.
    /// </summary>
    /// <remarks>
    /// Two venues where there were four tables. Hosting, joining and the couch are all a match with
    /// people you already know and differ only in where those people are sitting, so they moved
    /// under one panel and the row that used to hold four now holds the one distinction that
    /// actually changes the kind of match: whether the other three are friends or strangers.
    /// </remarks>
    private void DrawVenues(Vector2 viewport)
    {
        float height = VenuesHeight();
        float width = _button * 2.4f;
        float gap = _button * 0.42f;
        float left = (viewport.X - ((width * _venues.Length) + gap)) / 2f;
        float top = VenuesTop(viewport);

        for (int index = 0; index < _venues.Length; index++)
        {
            bool friendly = index == 0;
            bool chosen = friendly == AmongFriends;
            bool allowed = friendly || Allows(MatchSetup.Table.Strangers);

            _venues[index] = new Rect2(left, top, width, height);
            Panel(_venues[index], chosen);

            Vector2 middle = _venues[index].Position + (_venues[index].Size / 2f);

            // Dimmed rather than missing, the same way the dynamite button is when it is spent. An
            // option that vanishes reads as a layout that moved; one that is visibly there and
            // visibly unavailable reads as a rule, which is what it is.
            Color ink = allowed
                ? (chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.4f))
                : new Color(Palette.OnPanel, 0.16f);

            // The picture sits a little high in the panel, to leave room for its name underneath.
            // Neither of these two is unambiguous on its own: a pair of moles and two distant hills
            // are each a reasonable picture of their option and a word each settles it.
            Vector2 icon = middle - new Vector2(0f, height * 0.14f);

            if (friendly)
            {
                Glyphs.Friends(this, icon, height * 0.58f, ink);
            }
            else
            {
                Glyphs.Strangers(this, icon, height * 0.64f, ink);
            }

            Words.Under(
                this, friendly ? "Friends" : "Online",
                new Vector2(middle.X, _venues[index].Position.Y + (height * 0.62f)),
                height * 0.2f, ink);

            left += width + gap;
        }
    }

    /// <summary>
    /// Where the people you know are sitting: here, at your lobby, or at theirs.
    /// </summary>
    /// <remarks>
    /// Only under Friends, because it is the only venue with a choice to make. Meeting strangers is
    /// one thing, and the row that would ask about it would have one option in it.
    /// </remarks>
    private void DrawModes(Vector2 viewport)
    {
        float height = ModesHeight();
        float width = _button * 2.1f;
        float gap = _button * 0.42f;
        float left = (viewport.X - ((width * _modes.Length) + (gap * (_modes.Length - 1)))) / 2f;
        float top = ModesTop(viewport);

        for (int index = 0; index < _modes.Length; index++)
        {
            MatchSetup.Table mode = (MatchSetup.Table)index;
            bool chosen = _where == mode;

            _modes[index] = new Rect2(left, top, width, height);
            Panel(_modes[index], chosen);

            Vector2 middle = _modes[index].Position + (_modes[index].Size / 2f);
            Color ink = chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.4f);
            Vector2 icon = middle - new Vector2(0f, height * 0.14f);

            switch (mode)
            {
                case MatchSetup.Table.Couch:
                    Glyphs.Couch(this, icon, height * 0.6f, ink);
                    break;

                case MatchSetup.Table.Hosting:
                    Glyphs.Broadcast(this, icon, height * 0.6f, ink);
                    break;

                default:
                    Glyphs.Tiles(this, icon, height * 0.7f, ink);
                    break;
            }

            Words.Under(
                this, TableName(mode),
                new Vector2(middle.X, _modes[index].Position.Y + (height * 0.62f)),
                height * 0.2f, ink);

            left += width + gap;
        }
    }

    private float ModesTop(Vector2 viewport) =>
        VenuesTop(viewport) + VenuesHeight() + (_button * 0.3f);

    private float ModesHeight() => _button * 1.4f;

    /// <summary>
    /// Settings and credits, in the corner opposite the one that starts a match.
    /// </summary>
    /// <remarks>
    /// Opposite on purpose. Play is bottom right and is the only thing most people ever press here,
    /// so the two that are not play go as far from it as the screen allows: a thumb reaching for the
    /// corner it always reaches for cannot catch either of these by accident.
    ///
    /// Smaller than play, too, which is the other half of saying the same thing.
    /// </remarks>
    private void DrawAside(Vector2 viewport)
    {
        float button = _button * 0.62f;
        float margin = _button * 0.75f;
        float middle = viewport.Y - margin - _button;

        Vector2 gear = new Vector2(margin + button, middle);
        Vector2 thanks = new Vector2(gear.X + (button * 2.5f), middle);

        _settings = new Rect2(gear - (Vector2.One * button), Vector2.One * button * 2f);
        _credits = new Rect2(thanks - (Vector2.One * button), Vector2.One * button * 2f);

        Aside(gear, button, "settings");
        Aside(thanks, button, "heart");
    }

    /// <summary>
    /// One of the two corner buttons: a picture in a circle, and no word under it.
    /// </summary>
    /// <remarks>
    /// These had their names under them and have lost them. A cog and a heart are the two most
    /// settled icons in software, they are the only two buttons in that corner, and neither opens
    /// anything that cannot be closed again, so a wrong guess costs a press. Everywhere else on this
    /// screen a word is buying something: the venues and the modes are choices a player makes once
    /// and then plays a match under, and being wrong about one of those costs a match.
    /// </remarks>
    private void Aside(Vector2 at, float button, string icon)
    {
        DrawCircle(at, button, Palette.Panel);
        DrawArc(at, button, 0f, Mathf.Tau, 32, new Color(Palette.OnPanel, 0.5f), 2f);
        Glyphs.Icon(this, icon, at, button * 0.95f, Palette.OnPanel);
    }

    private Rect2 _settings;
    private Rect2 _credits;

    /// <summary>
    /// What each way of playing with friends is called, in as few words as it can be said in.
    /// </summary>
    /// <remarks>
    /// One word each, and now every one of them is one word. "Local" rather than "Here", because
    /// under a heading that already says Friends the question is how they are connected rather than
    /// where the player is standing, and Local is the word that answers it. "Host" and "Join" are
    /// the words every game in the genre uses for these two, and borrowing them costs nothing and
    /// saves explaining. Matchmaking was "Find game", which was the only label that needed two, and
    /// it is a venue now rather than a mode: it is called Online up on the row above.
    /// </remarks>
    private static string TableName(MatchSetup.Table table) => table switch
    {
        MatchSetup.Table.Couch => "Local",
        MatchSetup.Table.Hosting => "Host",
        _ => "Join",
    };

    /// <summary>
    /// Whichever of the three lower rows this venue actually has a question in.
    /// </summary>
    /// <remarks>
    /// Every one of them is conditional, and between them they are the whole of the setup. Friends
    /// asks how the three of them are connected; local play and hosting ask how many are playing;
    /// hosting and strangers ask how fast the clock runs. A joiner is asked nothing at all, because
    /// the host already answered all of it and the code is the only thing a joiner contributes.
    /// </remarks>
    private void DrawSecondRow(Vector2 viewport)
    {
        if (AmongFriends)
        {
            DrawModes(viewport);
        }

        if (Picks())
        {
            DrawChoices(viewport);
        }

        if (Paces())
        {
            DrawPaces(viewport);
        }
    }

    /// <summary>
    /// What each platoon will be planning with, under the platoon it belongs to.
    /// </summary>
    /// <remarks>
    /// The rule is the match's, not the menu's: seat zero takes the pointer and the keys, the next
    /// seats take a connected controller each, and whoever is left shares the pointer and plans
    /// when it reaches them. Read straight off the connected pads so the menu cannot disagree with
    /// what happens when the match starts.
    ///
    /// Worth saying out loud on this screen because it is the thing about local play that is least
    /// guessable. Four platoons on one keyboard all look equally ready, only one of them can act at
    /// a time, and until now nothing said so: a table of four would sit there waiting for three
    /// platoons that were never going to move.
    /// </remarks>
    private void DrawDevice(int seat, Vector2 at, float size, Color ink)
    {
        int pads = Input.GetConnectedJoypads().Count;

        if (seat == 0)
        {
            Glyphs.Pointer(this, at, size, ink);
            return;
        }

        if (seat - 1 < pads)
        {
            Glyphs.Pad(this, at, size, ink);
            return;
        }

        Glyphs.Passing(this, at, size, ink);
    }

    /// <summary>Where the row of platoon counts starts and how tall it is.</summary>
    /// <remarks>
    /// One place, because two things sit under it. The pace buttons had this height written out
    /// again as a number, so making room under each platoon for what it plans with pushed the row
    /// taller and left the paces overlapping it, which is exactly the kind of drift a second copy
    /// of a number is for.
    /// </remarks>
    private float ChoicesTop(Vector2 viewport) =>
        AmongFriends
            ? ModesTop(viewport) + ModesHeight() + (_button * 0.3f)
            : VenuesTop(viewport) + VenuesHeight() + (_button * 0.35f);

    private float ChoicesHeight() => _button * 2.05f;

    /// <summary>Two, three or four, each shown as that many platoons.</summary>
    private void DrawChoices(Vector2 viewport)
    {
        float glyph = _button * 0.62f;
        float height = ChoicesHeight();
        float gap = _button * 0.5f;
        float[] widths = new float[_choices.Length];
        float total = 0f;

        for (int index = 0; index < _choices.Length; index++)
        {
            widths[index] = (glyph * 1.05f * (MatchSetup.FewestPlayers + index)) + _button;
            total += widths[index] + gap;
        }

        float left = (viewport.X - total + gap) / 2f;
        float top = ChoicesTop(viewport);

        for (int index = 0; index < _choices.Length; index++)
        {
            int players = MatchSetup.FewestPlayers + index;
            bool chosen = players == _players;

            _choices[index] = new Rect2(left, top, widths[index], height);
            Panel(_choices[index], chosen);

            for (int seat = 0; seat < players; seat++)
            {
                float across =
                    left + (_button * 0.5f) + (glyph * 0.52f) + (seat * glyph * 1.05f);

                Color colour = chosen
                    ? Palette.Seat(seat)
                    : new Color(Palette.Seat(seat), 0.65f);

                Face(new Vector2(across, top + (height * 0.38f)), glyph, colour, chosen);

                DrawDevice(
                    seat,
                    new Vector2(across, top + (height * 0.75f)),
                    glyph * 0.86f,
                    chosen ? new Color(Palette.OnPanel, 0.85f) : new Color(Palette.OnPanel, 0.45f));
            }

            left += widths[index] + gap;
        }
    }

    /// <summary>
    /// An hourglass for a match played now, a moon for one played whenever people get to it.
    /// </summary>
    /// <remarks>
    /// "Anytime" rather than the "Daily" this used to say, and rather than the asynchronous the word
    /// actually means. Daily was wrong twice over: the window is a day at the outside rather than a
    /// day exactly, and it read as a commitment to turn up every day, which is precisely the thing
    /// this pace exists to avoid. Asynchronous is right and is not a word to put in front of a
    /// seven-year-old. Anytime is what the code has called it since the pace was named, and it is
    /// the friendlier word as well as the accurate one.
    /// </remarks>
    private void DrawPaces(Vector2 viewport)
    {
        float height = _button * 1.15f;
        float width = _button * 1.5f;
        float gap = _button * 0.4f;
        float left = (viewport.X - ((width * 2) + gap)) / 2f;

        // Under the count where there is one, and under whatever is above it where there is not.
        // Meeting strangers has no count row, so the paces would otherwise be drawn against a gap
        // the height of a row that is not there.
        float top = Picks()
            ? ChoicesTop(viewport) + ChoicesHeight() + (_button * 0.3f)
            : ModesTop(viewport) + (AmongFriends ? ModesHeight() + (_button * 0.3f) : 0f);

        for (int index = 0; index < _paces.Length; index++)
        {
            bool chosen = (int)_pace == index;

            _paces[index] = new Rect2(left, top, width, height);
            Panel(_paces[index], chosen);

            Vector2 middle = _paces[index].Position + (_paces[index].Size / 2f);
            Color ink = chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.4f);

            Words.Under(
                this,
                (MatchPace)index == MatchPace.Live ? "Real-time" : "Anytime",
                new Vector2(middle.X, _paces[index].Position.Y + (height * 0.6f)),
                height * 0.22f, ink);

            middle -= new Vector2(0f, height * 0.16f);

            if ((MatchPace)index == MatchPace.Live)
            {
                Glyphs.Time(this, middle, height * 0.56f, ink);
            }
            else
            {
                Glyphs.Moon(this, middle, height * 0.62f, ink);
            }

            left += width + gap;
        }
    }

    /// <summary>
    /// Start a match, and beside it the match already going, if there is one.
    /// </summary>
    /// <remarks>
    /// Resume sits next to play rather than up with the three tables, because it is not a kind of
    /// match to choose between. It is the one already being played, so it belongs with the button
    /// that starts things and not with the ones that describe them. It also had to move: drawn above
    /// the table row it landed squarely on the badge and hid two of the four moles.
    /// </remarks>
    /// <summary>
    /// Play, in the bottom right corner.
    /// </summary>
    /// <remarks>
    /// A corner rather than a fraction of the height. Everything else on this screen is placed as a
    /// fraction of the canvas, and an expanding stretch gives a wide window a short canvas, so a
    /// column placed that way compresses into itself and a button at the foot of it ends up in the
    /// row above. A corner is measured from two edges instead and cannot do that.
    ///
    /// It stays here rather than joining the head of the row of choices, which is where it briefly
    /// went. Reading order is not the only thing a menu owes a player: this is the button pressed at
    /// the end of every visit to this screen and the only one most people ever press, and a thumb
    /// that always reaches for the same corner is worth more than a sentence that reads left to
    /// right. It also keeps the choices to a row of choices, with nothing in it that acts.
    /// </remarks>
    private void DrawPlay(Vector2 viewport)
    {
        float margin = _button * 0.75f;
        float middle = viewport.Y - margin - _button;

        _play = new Vector2(viewport.X - margin - _button, middle);

        DrawCircle(_play, _button, Palette.Panel);
        DrawArc(_play, _button, 0, Mathf.Tau, 40, new Color(Palette.OnPanel, 0.55f), 3f);
        Glyphs.Play(this, _play + new Vector2(_button * 0.06f, 0), _button * 1.1f, Palette.OnPanel);

        if (!_canResume)
        {
            return;
        }

        // Carrying on beside starting again, in the same corner, because they are the same kind of
        // thing and a player looking for one is looking for the other. Inboard of it, since play is
        // against the right edge and there is no room outboard.
        Vector2 back = new Vector2(_play.X - (_button * 2.5f), middle);

        _resume = new Rect2(back - new Vector2(_button, _button), Vector2.One * _button * 2f);

        DrawCircle(back, _button, Palette.Panel);
        DrawArc(back, _button, 0, Mathf.Tau, 40, new Color(Palette.OnPanel, 0.55f), 3f);

        // The match you are in is an online one by definition, so the same glyph the hosting mode
        // uses says which button this is without needing a word for "continue".
        Glyphs.Broadcast(this, back, _button * 0.95f, Palette.OnPanel);
    }

    /// <summary>
    /// A control, chosen or not.
    /// </summary>
    /// <remarks>
    /// Both states are panels. The unselected ones were a fifteen percent wash over soil to begin
    /// with, which on a tan hillside is not a control at all.
    /// </remarks>
    private void Panel(Rect2 where, bool chosen)
    {
        DrawRect(where, chosen ? Palette.Panel : new Color(Palette.Ink, 0.5f));
        DrawRect(
            where,
            chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.25f), false,
            chosen ? 3f : 1.5f);
    }
}
