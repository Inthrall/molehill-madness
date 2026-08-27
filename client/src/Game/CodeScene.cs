using System.Text;
using Godot;

/// <summary>
/// Typing in the five letters somebody read out.
/// </summary>
/// <remarks>
/// The plan fixes the shape of this: "the code entry screen is wordless too: five letter tiles". So
/// there is no prompt, no label and no error message. Five empty tiles fill up as letters are chosen,
/// and the only other thing on the screen is the grid to choose them from.
///
/// The grid is the relay's own alphabet, which is twenty-four letters rather than twenty-six: I and O
/// are missing because said aloud or read off a screen they are one and zero often enough to lose
/// somebody a game. Leaving them out of the grid is better than accepting and rejecting them, because
/// a player who cannot find the I has learnt the rule in one second without being told it.
///
/// A hardware keyboard works too, since this is also how two people on desktops get into a match, and
/// somebody with a keyboard will always try to type.
/// </remarks>
public partial class CodeScene : Control
{
    /// <summary>
    /// The letters a code can contain, in the relay's own order.
    /// </summary>
    /// <remarks>
    /// Duplicated from the relay rather than fetched, deliberately: it is the alphabet of a code
    /// somebody says out loud, so it changes roughly never, and a screen that cannot draw itself
    /// until a network call returns is a worse trade than a constant in two places.
    /// </remarks>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";

    private const int Letters = 5;
    private const int Columns = 6;

    private readonly StringBuilder _typed = new StringBuilder(Letters);
    private readonly Rect2[] _keys = new Rect2[Alphabet.Length];
    private Rect2 _back;
    private Rect2 _goHit;
    private Vector2 _go;
    private float _button;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        if (Flags.WantsTouch())
        {
            Input.EmulateMouseFromTouch = false;
            Input.EmulateTouchFromMouse = true;
        }
    }

    public override void _Process(double delta) => QueueRedraw();

    private bool Full => _typed.Length >= Letters;

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
            case Key.Backspace:
                Rub();
                return;

            case Key.Escape:
                Leave();
                return;

            case Key.Enter:
            case Key.KpEnter:
            case Key.Space:
                Go();
                return;

            default:
                // Whatever they pressed, judged against the alphabet rather than against the key
                // layout, so a letter that is not in a code is simply ignored.
                Type((char)key.Unicode);
                return;
        }
    }

    private void HandlePress(Vector2 at)
    {
        if (_back.HasPoint(at))
        {
            Rub();
            return;
        }

        if (Full && _goHit.HasPoint(at))
        {
            Go();
            return;
        }

        for (int index = 0; index < _keys.Length; index++)
        {
            if (_keys[index].HasPoint(at))
            {
                Type(Alphabet[index]);
                return;
            }
        }
    }

    private void Type(char letter)
    {
        char upper = char.ToUpperInvariant(letter);

        if (Full || !Alphabet.Contains(upper))
        {
            return;
        }

        _typed.Append(upper);
        QueueRedraw();
    }

    private void Rub()
    {
        if (_typed.Length == 0)
        {
            // Rubbing out an empty code is how you get back, because there is no other button and
            // adding a second one to a screen this simple would be the only thing on it to explain.
            Leave();
            return;
        }

        _typed.Length--;
        QueueRedraw();
    }

    private void Go()
    {
        if (!Full)
        {
            return;
        }

        Online.Join(_typed.ToString());
        MatchSetup.Where = MatchSetup.Table.Joining;

        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Match.tscn");
    }

    private void Leave()
    {
        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
    }

    // ---- Drawing ---------------------------------------------------------------------

    public override void _Draw()
    {
        Vector2 viewport = Size;

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);
        MenuHill.Draw(this, viewport);

        _button = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.075f, 30f, 64f);

        DrawTiles(viewport);

        // The grid and the two buttons under it share one budget, worked out from the height that
        // is actually left. Sizing the keys from the button size instead ran the bottom row and the
        // rub-out clean off a short screen, and put the go button on top of the letters.
        int rows = (Alphabet.Length + Columns - 1) / Columns;
        float top = viewport.Y * 0.32f;
        float floor = viewport.Y * 0.94f;
        float gapShare = 0.16f;
        float unit = (floor - top) / (rows + 1f + ((rows + 1f) * gapShare));
        float wide = Mathf.Min(viewport.X * 0.13f, unit);
        float gap = wide * gapShare;
        float left = (viewport.X - ((wide * Columns) + (gap * (Columns - 1)))) / 2f;

        DrawKeys(left, top, wide, gap, rows);
        DrawFooter(left, top + (rows * (wide + gap)), wide, gap);
    }

    /// <summary>The five tiles, filling up as letters arrive.</summary>
    private void DrawTiles(Vector2 viewport)
    {
        float wide = Mathf.Min(viewport.X * 0.105f, viewport.Y * 0.105f);
        float tall = wide * 1.28f;
        float gap = wide * 0.18f;
        float left = (viewport.X - ((wide * Letters) + (gap * (Letters - 1)))) / 2f;
        float top = viewport.Y * 0.10f;

        for (int tile = 0; tile < Letters; tile++)
        {
            Rect2 where = new Rect2(left + (tile * (wide + gap)), top, wide, tall);
            bool filled = tile < _typed.Length;

            DrawRect(where, filled ? Palette.Panel : new Color(Palette.Ink, 0.5f));
            DrawRect(
                where,
                filled ? Palette.OnPanel : new Color(Palette.OnPanel, 0.3f),
                false,
                filled ? 3f : 1.5f);

            if (filled)
            {
                Letter(_typed[tile], where, tall * 0.62f, Palette.OnPanel);
            }
        }

        // Where the next letter is going, so a half-typed code does not look stalled. In ink
        // rather than the panel colour, which is a pale cream and vanishes against the sky.
        if (!Full)
        {
            DrawRect(
                new Rect2(
                    left + (_typed.Length * (wide + gap)),
                    top + tall + (gap * 0.7f),
                    wide,
                    Mathf.Max(3f, tall * 0.06f)),
                Palette.Ink);
        }
    }

    /// <summary>The alphabet, as a grid of keys.</summary>
    private void DrawKeys(float left, float top, float wide, float gap, int rows)
    {
        for (int index = 0; index < Alphabet.Length; index++)
        {
            int column = index % Columns;
            int row = index / Columns;

            _keys[index] = new Rect2(
                left + (column * (wide + gap)), top + (row * (wide + gap)), wide, wide);

            // Dimmed once the code is full, because pressing one then would do nothing and a
            // control that does nothing should not look like one that does.
            Color ink = Full ? new Color(Palette.OnPanel, 0.3f) : Palette.OnPanel;

            DrawRect(_keys[index], Palette.Panel);
            DrawRect(_keys[index], new Color(Palette.OnPanel, 0.3f), false, 1.5f);
            Letter(Alphabet[index], _keys[index], wide * 0.56f, ink);
        }

        _ = rows;
    }

    /// <summary>
    /// Rub out on the left, go on the right, on one row under the grid.
    /// </summary>
    /// <remarks>
    /// Side by side rather than the go button floating in the middle of the screen, which had it
    /// sitting on top of the last row of letters on anything short.
    /// </remarks>
    private void DrawFooter(float left, float top, float wide, float gap)
    {
        float height = wide * 0.9f;
        float width = (wide * 2f) + gap;
        float right = left + (wide * Columns) + (gap * (Columns - 1)) - width;

        _back = new Rect2(left, top, width, height);

        DrawRect(_back, Palette.Panel);
        DrawRect(_back, new Color(Palette.OnPanel, 0.3f), false, 1.5f);
        Glyphs.Back(this, _back.Position + (_back.Size / 2f), height * 0.72f, Palette.OnPanel);

        // Only a control once there is something to do with it.
        Rect2 go = new Rect2(right, top, width, height);
        Color panel = Full ? Palette.Panel : new Color(Palette.Ink, 0.35f);
        Color ink = Full ? Palette.OnPanel : new Color(Palette.OnPanel, 0.25f);

        _go = go.Position + (go.Size / 2f);
        _goHit = go;

        DrawRect(go, panel);
        DrawRect(go, new Color(ink, 0.4f), false, Full ? 3f : 1.5f);
        Glyphs.Play(this, _go + new Vector2(height * 0.04f, 0), height * 0.78f, ink);
    }

    /// <summary>
    /// One letter, centred in a box.
    /// </summary>
    /// <remarks>
    /// The fallback font, which is the same one the damage numerals use. A five-letter code is
    /// letters by definition, so this is the one place the wordless rule cannot apply, and it is
    /// still not a word.
    /// </remarks>
    private void Letter(char letter, Rect2 inside, float size, Color ink)
    {
        Font font = ThemeDB.FallbackFont;
        string text = letter.ToString();
        Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, (int)size);

        DrawString(
            font,
            inside.Position + new Vector2(
                (inside.Size.X - measured.X) / 2f,
                (inside.Size.Y + (measured.Y * 0.62f)) / 2f),
            text,
            HorizontalAlignment.Left,
            -1,
            (int)size,
            ink);
    }
}
