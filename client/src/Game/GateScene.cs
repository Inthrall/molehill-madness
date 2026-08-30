using System;
using Godot;

/// <summary>
/// The age gate. Asked once, at first run.
/// </summary>
/// <remarks>
/// The design calls for "a neutral date-of-birth gate at first run", and neutral is the whole
/// specification. It means three things here, each of which is a thing this screen deliberately does
/// not do.
///
/// It does not ask a question with a preferred answer. "Are you over 13?" is a yes button next to a
/// nothing button, and everybody presses yes, which is why the design asks for a date instead.
///
/// It does not start on a plausible adult year. A picker that opens on 1990 has answered on the
/// player's behalf, and a child who spins it two clicks has been shown where the line is. This one
/// opens on no year at all: the fields are empty and the button does nothing until all three are set.
///
/// And it does not say what any answer unlocks. There is no "over-13 accounts can play with
/// strangers" anywhere on the screen, because that sentence is an instruction to lie.
///
/// Words are allowed here. The design's wordless rule has named exceptions and this is one of them:
/// "words survive only outside the match: the store, where prices are legally required, plus
/// settings, the age gate, credits and legal text".
/// </remarks>
public partial class GateScene : Control
{
    /// <summary>Which of the three fields is being typed into.</summary>
    private enum Field
    {
        Day = 0,
        Month = 1,
        Year = 2,
    }

    private readonly int[] _typed = { -1, -1, -1 };
    private Field _at = Field.Day;
    private readonly Rect2[] _fields = new Rect2[3];
    private readonly Rect2[] _digits = new Rect2[10];
    private Rect2 _rub;
    private Rect2 _go;
    private float _unit;
    private bool _mistyped;

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

    /// <summary>Whether all three fields have something in them.</summary>
    private bool Complete => _typed[0] > 0 && _typed[1] > 0 && _typed[2] > 0;

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

            case Key.Enter:
            case Key.KpEnter:
                Go();
                return;

            case Key.Tab:
            case Key.Right:
                _at = (Field)Mathf.Min((int)_at + 1, 2);
                return;

            case Key.Left:
                _at = (Field)Mathf.Max((int)_at - 1, 0);
                return;

            default:
                if (key.Unicode >= '0' && key.Unicode <= '9')
                {
                    Type((int)key.Unicode - '0');
                }

                return;
        }
    }

    private void HandlePress(Vector2 at)
    {
        if (_rub.HasPoint(at))
        {
            Rub();
            return;
        }

        if (Complete && _go.HasPoint(at))
        {
            Go();
            return;
        }

        for (int field = 0; field < _fields.Length; field++)
        {
            if (_fields[field].HasPoint(at))
            {
                _at = (Field)field;
                QueueRedraw();
                return;
            }
        }

        for (int digit = 0; digit < _digits.Length; digit++)
        {
            if (_digits[digit].HasPoint(at))
            {
                Type(digit);
                return;
            }
        }
    }

    /// <summary>
    /// Adds a digit to the field being typed into, moving on when it is full.
    /// </summary>
    /// <remarks>
    /// Two digits for a day or a month and four for a year, which is what everybody expects and needs
    /// no label to explain. Moving on automatically is the difference between this and a form.
    /// </remarks>
    private void Type(int digit)
    {
        _mistyped = false;

        int room = _at == Field.Year ? 4 : 2;
        int now = _typed[(int)_at] < 0 ? 0 : _typed[(int)_at];
        int next = (now * 10) + digit;

        if (Width(now) >= room)
        {
            // The field is full. Start it again rather than silently ignoring the press, because a
            // player correcting a typo expects the new digits to be what sticks.
            next = digit;
        }

        _typed[(int)_at] = next;

        if (Width(next) >= room && _at != Field.Year)
        {
            _at = (Field)((int)_at + 1);
        }

        QueueRedraw();
    }

    private void Rub()
    {
        _mistyped = false;

        if (_typed[(int)_at] > 0)
        {
            int shorter = _typed[(int)_at] / 10;
            _typed[(int)_at] = shorter > 0 ? shorter : -1;
            QueueRedraw();
            return;
        }

        if (_at != Field.Day)
        {
            _at = (Field)((int)_at - 1);
            QueueRedraw();
        }
    }

    /// <summary>
    /// Takes the answer, if it is one.
    /// </summary>
    /// <remarks>
    /// A date that does not exist is not an answer, and the screen says so and waits rather than
    /// picking the nearest one that does. Nothing about which band the answer produced is shown,
    /// because that would tell the next person where the line is.
    /// </remarks>
    private void Go()
    {
        if (!Complete || !Reads(out DateTime born))
        {
            _mistyped = true;
            QueueRedraw();
            return;
        }

        Player.Answer(born);

        // The relay is told too, if this device has an account for it to be about. Recording the
        // answer only on the device is what left a child's account stuck as a child's account.
        Online.PushBand();

        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Menu.tscn");
    }

    private bool Reads(out DateTime born)
    {
        born = default;

        int day = _typed[0];
        int month = _typed[1];
        int year = _typed[2];

        if (year < 1900 || month < 1 || month > 12 || day < 1
            || day > DateTime.DaysInMonth(Mathf.Clamp(year, 1900, 9999), month))
        {
            return false;
        }

        born = new DateTime(year, month, day);

        return true;
    }

    private static int Width(int value) => value <= 0 ? 0 : value.ToString().Length;

    // ---- Drawing ---------------------------------------------------------------------

    public override void _Draw()
    {
        Vector2 viewport = Size;

        DrawRect(new Rect2(Vector2.Zero, viewport), Palette.Paper);
        MenuHill.Draw(this, viewport);

        _unit = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.07f, 28f, 62f);

        DrawAsk(viewport);
        DrawFields(viewport);
        DrawPad(viewport);
    }

    /// <summary>
    /// The question, and nothing else.
    /// </summary>
    /// <remarks>
    /// Six words, and none of them about what the answer does. A gate that explains its own
    /// consequences is a gate telling people how to get past it.
    /// </remarks>
    private void DrawAsk(Vector2 viewport)
    {
        Font font = ThemeDB.FallbackFont;
        int size = (int)(_unit * 0.62f);
        const string ask = "When were you born?";
        Vector2 measured = font.GetStringSize(ask, HorizontalAlignment.Left, -1, size);

        DrawString(
            font,
            new Vector2((viewport.X - measured.X) / 2f, viewport.Y * 0.16f),
            ask,
            HorizontalAlignment.Left,
            -1,
            size,
            Palette.Ink);

        if (!_mistyped)
        {
            return;
        }

        const string again = "That is not a date.";
        Vector2 note = font.GetStringSize(again, HorizontalAlignment.Left, -1, (int)(size * 0.7f));

        DrawString(
            font,
            new Vector2((viewport.X - note.X) / 2f, viewport.Y * 0.16f + (size * 1.5f)),
            again,
            HorizontalAlignment.Left,
            -1,
            (int)(size * 0.7f),
            Palette.Seat(3));
    }

    /// <summary>
    /// Three boxes: day, month, year. Empty until typed into, and empty is the point.
    /// </summary>
    private void DrawFields(Vector2 viewport)
    {
        float tall = _unit * 1.3f;
        float[] widths = { _unit * 1.6f, _unit * 1.6f, _unit * 2.6f };
        float gap = _unit * 0.4f;
        float total = widths[0] + widths[1] + widths[2] + (gap * 2);
        float left = (viewport.X - total) / 2f;
        float top = viewport.Y * 0.3f;

        string[] labels = { "Day", "Month", "Year" };
        Font font = ThemeDB.FallbackFont;

        for (int field = 0; field < _fields.Length; field++)
        {
            _fields[field] = new Rect2(left, top, widths[field], tall);
            bool here = (int)_at == field;

            DrawRect(_fields[field], Palette.Panel);
            DrawRect(
                _fields[field],
                here ? Palette.OnPanel : new Color(Palette.OnPanel, 0.3f),
                false,
                here ? 3f : 1.5f);

            if (_typed[field] > 0)
            {
                Middled(
                    _typed[field].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _fields[field],
                    tall * 0.56f,
                    Palette.OnPanel);
            }

            // Under the box rather than in it, so an empty field looks empty rather than pre-filled.
            int small = (int)(_unit * 0.34f);
            Vector2 measured = font.GetStringSize(labels[field], HorizontalAlignment.Left, -1, small);

            DrawString(
                font,
                new Vector2(
                    left + ((widths[field] - measured.X) / 2f),
                    top + tall + (small * 1.2f)),
                labels[field],
                HorizontalAlignment.Left,
                -1,
                small,
                new Color(Palette.Ink, 0.7f));

            left += widths[field] + gap;
        }
    }

    /// <summary>A number pad, because a phone with no keyboard still has to answer this.</summary>
    private void DrawPad(Vector2 viewport)
    {
        const int columns = 5;
        float wide = Mathf.Min(viewport.X * 0.1f, _unit * 1.25f);
        float gap = wide * 0.18f;
        float left = (viewport.X - ((wide * columns) + (gap * (columns - 1)))) / 2f;
        float top = viewport.Y * 0.52f;

        for (int digit = 0; digit < 10; digit++)
        {
            int column = digit % columns;
            int row = digit / columns;

            _digits[digit] = new Rect2(
                left + (column * (wide + gap)), top + (row * (wide + gap)), wide, wide);

            DrawRect(_digits[digit], Palette.Panel);
            DrawRect(_digits[digit], new Color(Palette.OnPanel, 0.3f), false, 1.5f);
            Middled(
                digit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _digits[digit],
                wide * 0.56f,
                Palette.OnPanel);
        }

        float below = top + (2 * (wide + gap)) + (gap * 0.6f);

        _rub = new Rect2(left, below, (wide * 2) + gap, wide * 0.9f);
        DrawRect(_rub, Palette.Panel);
        DrawRect(_rub, new Color(Palette.OnPanel, 0.3f), false, 1.5f);
        Glyphs.Back(this, _rub.Position + (_rub.Size / 2f), wide * 0.7f, Palette.OnPanel);

        _go = new Rect2(
            left + (wide * columns) + (gap * (columns - 1)) - ((wide * 2) + gap),
            below,
            (wide * 2) + gap,
            wide * 0.9f);

        Color panel = Complete ? Palette.Panel : new Color(Palette.Ink, 0.35f);
        Color ink = Complete ? Palette.OnPanel : new Color(Palette.OnPanel, 0.25f);

        DrawRect(_go, panel);
        DrawRect(_go, new Color(ink, 0.4f), false, Complete ? 3f : 1.5f);
        Glyphs.Play(this, _go.Position + (_go.Size / 2f), wide * 0.78f, ink);
    }

    private void Middled(string text, Rect2 inside, float size, Color ink)
    {
        Font font = ThemeDB.FallbackFont;
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
