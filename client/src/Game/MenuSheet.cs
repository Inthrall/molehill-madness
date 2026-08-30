using Godot;

/// <summary>
/// A panel over the menu: the settings, or the credits.
/// </summary>
/// <remarks>
/// One class for both, because they are the same object with different rows in it. A sheet with a
/// heading, some rows, and a way back is not two designs, and building it twice would guarantee the
/// two drifted apart in the ways that show: different margins, different back button, different
/// idea of what a row is.
///
/// This is also the first thing in the game made mostly of words, which is worth being uneasy about.
/// The credits have to be words, because names are words. The settings could have been pictures and
/// are not, because a settings screen full of unlabelled icons is a quiz.
/// </remarks>
public partial class MenuSheet : Control
{
    /// <summary>Which sheet this is.</summary>
    public enum Page
    {
        Settings = 0,
        Credits = 1,
    }

    private readonly System.Collections.Generic.List<Rect2> _rows =
        new System.Collections.Generic.List<Rect2>();

    private Page _page;
    private Rect2 _back;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 80;
        Visible = false;
    }

    /// <summary>Whether the sheet is covering the menu.</summary>
    public bool Showing => Visible;

    public void Show(Page page)
    {
        _page = page;
        Visible = true;
        QueueRedraw();
    }

    public void Close()
    {
        Visible = false;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent what)
    {
        Vector2 at;

        if (what is InputEventMouseButton { Pressed: true } click)
        {
            at = click.Position;
        }
        else if (what is InputEventScreenTouch { Pressed: true } touch)
        {
            at = touch.Position;
        }
        else
        {
            // Escape closes it, which is what escape does everywhere else in this game.
            if (what is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                Close();
                AcceptEvent();
            }

            return;
        }

        AcceptEvent();

        if (_back.HasPoint(at))
        {
            Close();
            return;
        }

        // Only the settings sheet has anything to press. The credits are a list of names.
        if (_page == Page.Settings && _rows.Count > 0 && _rows[0].HasPoint(at))
        {
            Options.Sound = !Options.Sound;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        Vector2 viewport = Size;
        float unit = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.075f, 30f, 64f);

        DrawRect(new Rect2(Vector2.Zero, viewport), new Color(0f, 0f, 0f, 0.62f));

        // A sheet rather than the whole screen, so the hill and the title stay visible round the
        // edges and it reads as something on top of the menu rather than somewhere else.
        float wide = Mathf.Min(viewport.X * 0.78f, unit * 12f);

        // Sized to the page rather than to one height for both. The settings are a heading and a
        // single row, and in a sheet built to hold the credits they sat in the top fifth of an
        // otherwise empty rectangle, which reads as a screen still loading.
        float wanted = _page == Page.Settings ? unit * 5.2f : unit * 7.4f;
        float tall = Mathf.Min(viewport.Y * 0.72f, wanted);
        Rect2 sheet = new Rect2((viewport.X - wide) / 2f, (viewport.Y - tall) / 2f, wide, tall);

        DrawRect(sheet, Palette.Paper);
        DrawRect(sheet, new Color(Palette.OnPanel, 0.06f));

        Words.Under(
            this,
            _page == Page.Settings ? "Settings" : "Credits",
            new Vector2(sheet.Position.X + (wide / 2f), sheet.Position.Y + (unit * 0.35f)),
            unit * 0.44f,
            Palette.Ink);

        _rows.Clear();

        float rowTop = sheet.Position.Y + (unit * 1.7f);

        if (_page == Page.Settings)
        {
            DrawSettings(sheet, unit, rowTop);
        }
        else
        {
            DrawCredits(sheet, unit, rowTop);
        }

        // Back, bottom right of the sheet, where the play button is on the menu behind it.
        float button = unit * 0.8f;
        // Inset by its own radius and a margin, so it sits inside the sheet. Placed at the corner
        // it straddled the edge, with half the button on the dimmed background behind, which reads
        // as a button that missed rather than one deliberately in the corner.
        Vector2 back = new Vector2(
            sheet.End.X - button - (unit * 0.45f),
            sheet.End.Y - button - (unit * 0.45f));

        _back = new Rect2(back - (Vector2.One * button), Vector2.One * button * 2f);

        DrawCircle(back, button, Palette.Panel);
        DrawArc(back, button, 0f, Mathf.Tau, 32, new Color(Palette.OnPanel, 0.55f), 2f);
        Glyphs.Back(this, back, button * 1.15f, Palette.OnPanel);
    }

    private void DrawSettings(Rect2 sheet, float unit, float top)
    {
        float height = unit * 1.3f;
        Rect2 row = new Rect2(
            sheet.Position.X + (unit * 0.7f), top, sheet.Size.X - (unit * 1.4f), height);

        _rows.Add(row);

        DrawRect(row, Palette.Panel);

        Vector2 middle = new Vector2(row.Position.X + (unit * 0.85f), row.Position.Y + (height / 2f));

        Glyphs.Icon(
            this, Options.Sound ? "sound" : "mute", middle, unit * 0.8f, Palette.OnPanel);

        Words.Under(
            this,
            Options.Sound ? "Sound on" : "Sound off",
            new Vector2(row.Position.X + (row.Size.X / 2f) + (unit * 0.5f), middle.Y - (unit * 0.2f)),
            unit * 0.34f,
            Palette.OnPanel);
    }

    private void DrawCredits(Rect2 sheet, float unit, float top)
    {
        // Attribution none of these licences require. Kenney's own licence calls crediting
        // appreciated rather than mandatory, CC0 asks for nothing at all, and the font's licence asks
        // only that its own terms travel with it. They are here because the work was free and saying
        // so costs a line each.
        string[] lines =
        {
            "Sound effects",
            "Kenney  (CC0)",
            "rubberduck  (CC0)",
            "Type",
            "Titan One by Rodrigo Fuenzalida  (OFL)",
        };

        bool[] heading = { true, false, false, true, false };

        float line = unit * 0.62f;
        float at = top;

        for (int index = 0; index < lines.Length; index++)
        {
            Words.Under(
                this,
                lines[index],
                new Vector2(sheet.Position.X + (sheet.Size.X / 2f), at),
                heading[index] ? unit * 0.34f : unit * 0.28f,
                heading[index] ? Palette.Ink : new Color(Palette.Ink, 0.72f));

            at += heading[index] ? line * 1.15f : line;
        }
    }
}
