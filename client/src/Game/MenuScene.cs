using Godot;
using Molehill.Online;

/// <summary>
/// The lobby: where the match is being played, how many platoons, and a button to start.
/// </summary>
/// <remarks>
/// The design's couch model is that whoever opens the lobby picks the player count up front, two,
/// three or four, with four the default and a hard ceiling rather than a first step. That is still
/// the spine of this screen; online adds one row above it.
///
/// The row is not "offline versus online", which is a word about plumbing. It is all of us round one
/// screen, us in different places with me opening the lobby, or me joining somebody else's, and
/// those are three genuinely different games rather than a setting. A joiner does not pick a player
/// count or a pace, because the host already did and the code is the only thing they contribute.
///
/// Wordless like the rest of it. The count is shown as that many platoon-coloured moles rather than
/// as a numeral, because the design spends its one numeral on damage, and a row of moles says "this
/// many of you" more directly than a digit would anyway.
/// </remarks>
public partial class MenuScene : Control
{
    private int _players = MatchSetup.MostPlayers;
    private MatchSetup.Table _where = MatchSetup.Table.Couch;
    private MatchPace _pace = MatchPace.Live;

    private readonly Rect2[] _choices = new Rect2[MatchSetup.MostPlayers - MatchSetup.FewestPlayers + 1];
    private readonly Rect2[] _tables = new Rect2[3];
    private readonly Rect2[] _paces = new Rect2[2];
    private Rect2 _resume;
    private bool _canResume;

    private Vector2 _play;
    private float _button;
    private bool _startAtOnce;
    private bool _needsGate;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // The driver has nobody to press anything, so it walks straight through. Deferred by a
        // frame rather than done here, because changing scene from inside _Ready tears down the
        // tree that is still being built.
        _startAtOnce = Flags.Driven() || Flags.Host() || Flags.Join() is not null;

        if (Flags.Host())
        {
            _where = MatchSetup.Table.Hosting;
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
                Sit((MatchSetup.Table)Mathf.PosMod((int)_where - 1, _tables.Length));
                break;

            case Key.Down:
            case Key.S:
                Sit((MatchSetup.Table)Mathf.PosMod((int)_where + 1, _tables.Length));
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

        for (int index = 0; index < _tables.Length; index++)
        {
            if (_tables[index].HasPoint(at))
            {
                Sit((MatchSetup.Table)index);
                return;
            }
        }

        if (_where == MatchSetup.Table.Hosting)
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
    /// <remarks>A joiner does not: the host already decided, and the lobby says so.</remarks>
    private bool Picks() => _where != MatchSetup.Table.Joining;

    private void Choose(int players)
    {
        _players = Mathf.Clamp(players, MatchSetup.FewestPlayers, MatchSetup.MostPlayers);
        QueueRedraw();
    }

    private void Sit(MatchSetup.Table table)
    {
        _where = table;
        QueueRedraw();
    }

    private void Start()
    {
        MatchSetup.PlayerCount = _players;
        MatchSetup.Where = _where;
        MatchSetup.Pace = _pace;

        switch (_where)
        {
            case MatchSetup.Table.Hosting:
                Online.Host(_players, _pace, Flags.Window() ?? 0);
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
                Online.Forget();
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

        DrawBadge(viewport);
        DrawTables(viewport);
        DrawSecondRow(viewport);
        DrawPlay(viewport);
    }

    /// <summary>
    /// Four platoons coming up out of the hill. As close to a title as a wordless game gets.
    /// </summary>
    private void DrawBadge(Vector2 viewport)
    {
        float size = Mathf.Clamp(viewport.X * 0.075f, 40f, viewport.Y * 0.16f);
        Color soil = Palette.Of(MoleSim.Terrain.Material.LooseSoil);

        for (int seat = 0; seat < MatchSetup.MostPlayers; seat++)
        {
            float across = 0.5f + ((seat - 1.5f) * size * 0.95f / viewport.X);
            float ground = MenuHill.SurfaceAt(across, viewport);
            Vector2 at = new Vector2(across * viewport.X, ground - (size * 0.34f));

            // Each one out of its own molehill, which is the game's whole silhouette.
            DrawColoredPolygon(
                new[]
                {
                    at + new Vector2(-size * 0.82f, size * 0.5f),
                    at + new Vector2(0, -size * 0.12f),
                    at + new Vector2(size * 0.82f, size * 0.5f),
                },
                soil);

            Glyphs.Mole(this, at, size * 0.62f, Palette.Seat(seat));
        }
    }

    /// <summary>Where the row of tables starts and how tall it is.</summary>
    /// <remarks>
    /// Named for the same reason the choices row's are: everything under it is placed by chaining
    /// off it, and the moment any of that was written out as its own fraction of the screen the
    /// rows started landing on top of each other whenever one of them changed height.
    /// </remarks>
    private float TablesTop(Vector2 viewport) => viewport.Y * 0.44f;

    private float TablesHeight() => _button * 1.4f;

    /// <summary>Here together, hosting, or joining.</summary>
    private void DrawTables(Vector2 viewport)
    {
        float height = TablesHeight();
        float width = _button * 2.1f;
        float gap = _button * 0.42f;
        float left = (viewport.X - ((width * _tables.Length) + (gap * (_tables.Length - 1)))) / 2f;
        float top = TablesTop(viewport);

        for (int index = 0; index < _tables.Length; index++)
        {
            bool chosen = (int)_where == index;

            _tables[index] = new Rect2(left, top, width, height);
            Panel(_tables[index], chosen);

            Vector2 middle = _tables[index].Position + (_tables[index].Size / 2f);
            Color ink = chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.4f);

            switch ((MatchSetup.Table)index)
            {
                case MatchSetup.Table.Couch:
                    Glyphs.Couch(this, middle, height * 0.72f, ink);
                    break;

                case MatchSetup.Table.Hosting:
                    Glyphs.Broadcast(this, middle, height * 0.72f, ink);
                    break;

                default:
                    Glyphs.Tiles(this, middle, height * 0.86f, ink);
                    break;
            }

            left += width + gap;
        }

    }

    /// <summary>
    /// Either how many are playing, or how fast a hosted match runs. A joiner gets neither.
    /// </summary>
    private void DrawSecondRow(Vector2 viewport)
    {
        if (_where == MatchSetup.Table.Joining)
        {
            return;
        }

        DrawChoices(viewport);

        if (_where == MatchSetup.Table.Hosting)
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
        TablesTop(viewport) + TablesHeight() + (_button * 0.35f);

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

                Glyphs.Mole(
                    this,
                    new Vector2(across, top + (height * 0.38f)),
                    glyph,
                    colour);

                DrawDevice(
                    seat,
                    new Vector2(across, top + (height * 0.75f)),
                    glyph * 0.86f,
                    chosen ? new Color(Palette.OnPanel, 0.85f) : new Color(Palette.OnPanel, 0.45f));
            }

            left += widths[index] + gap;
        }
    }

    /// <summary>An hourglass for a match played now, a moon for one played over days.</summary>
    private void DrawPaces(Vector2 viewport)
    {
        float height = _button * 1.15f;
        float width = _button * 1.5f;
        float gap = _button * 0.4f;
        float left = (viewport.X - ((width * 2) + gap)) / 2f;
        float top = ChoicesTop(viewport) + ChoicesHeight() + (_button * 0.3f);

        for (int index = 0; index < _paces.Length; index++)
        {
            bool chosen = (int)_pace == index;

            _paces[index] = new Rect2(left, top, width, height);
            Panel(_paces[index], chosen);

            Vector2 middle = _paces[index].Position + (_paces[index].Size / 2f);
            Color ink = chosen ? Palette.OnPanel : new Color(Palette.OnPanel, 0.4f);

            if ((MatchPace)index == MatchPace.Live)
            {
                Glyphs.Time(this, middle, height * 0.66f, ink);
            }
            else
            {
                Glyphs.Moon(this, middle, height * 0.72f, ink);
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
    /// Play, in the bottom left corner.
    /// </summary>
    /// <remarks>
    /// It used to sit centred at four fifths of the way down, directly under the stack of choices,
    /// which worked at the shape of the project's own window and nowhere else: everything on this
    /// screen is placed as a fraction of the canvas height, and an expanding stretch gives a wide
    /// window a short canvas, so the whole column compresses into itself and the button ends up in
    /// the row above it.
    ///
    /// A corner cannot do that. It is measured from the bottom and the left rather than from the
    /// middle, so it stays a fixed distance from two edges whatever shape the window is, and it is
    /// out of the way of a column that can be as tall as it likes.
    /// </remarks>
    private void DrawPlay(Vector2 viewport)
    {
        float margin = _button * 0.75f;
        float middle = viewport.Y - margin - _button;

        _play = new Vector2(margin + _button, middle);

        DrawCircle(_play, _button, Palette.Panel);
        DrawArc(_play, _button, 0, Mathf.Tau, 40, new Color(Palette.OnPanel, 0.55f), 3f);
        Glyphs.Play(this, _play + new Vector2(_button * 0.06f, 0), _button * 1.1f, Palette.OnPanel);

        if (!_canResume)
        {
            return;
        }

        // Carrying on beside starting again, in the same corner, because they are the same kind of
        // thing and a player looking for one is looking for the other.
        Vector2 back = new Vector2(_play.X + (_button * 2.5f), middle);

        _resume = new Rect2(back - new Vector2(_button, _button), Vector2.One * _button * 2f);

        DrawCircle(back, _button, Palette.Panel);
        DrawArc(back, _button, 0, Mathf.Tau, 40, new Color(Palette.OnPanel, 0.55f), 3f);

        // The match you are in is an online one by definition, so the same glyph the hosting table
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
