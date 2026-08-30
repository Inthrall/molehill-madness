using Godot;

/// <summary>
/// What escape does.
/// </summary>
/// <remarks>
/// Nothing, until now, which is a gap rather than a decision. A match started when the scene did
/// and the only ways out of it were to finish it or to close the process, and a player who wanted
/// to stop had no way of asking. The design's couch lobby is a menu the game can be got back to,
/// and this is the door to it.
///
/// Three buttons and no words, which the icons can carry on their own: a triangle to carry on, a
/// speaker to shut it up, and an arrow to leave. Those are the three things anybody presses escape
/// to do, and a fourth would be a settings screen that does not exist yet.
///
/// It stops the clock while it is open. A pause menu that lets the planning clock run down is not a
/// pause menu, and on a shared eight second turn it would be a way of losing somebody else's round
/// for them.
/// </remarks>
public partial class PauseMenu : Control
{
    /// <summary>What a press on the menu asked for.</summary>
    public enum Choice
    {
        /// <summary>The press missed everything.</summary>
        Nothing = 0,

        Resume = 1,

        Sound = 2,

        Menu = 3,
    }

    private Vector2[] _buttons = System.Array.Empty<Vector2>();
    private float _button;

    public PauseMenu()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Fills the layer, so Size is the rectangle it is actually drawn into. Laid out against
        // the viewport instead, an expanding stretch gives a canvas a different shape from the
        // window and everything anchored near an edge ends up off it.
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
    }

    /// <summary>
    /// Whether the sound is on.
    /// </summary>
    /// <remarks>
    /// This used to be a field of its own, and its comment said what it did: it "only decides which
    /// speaker is drawn". It drew a crossed-out speaker and left the audio playing, which is worse
    /// than not having the button, because a control that acknowledges a press and then does nothing
    /// reads as a broken game rather than a missing feature.
    ///
    /// It is the real setting now, shared with the one on the menu, and it is written down between
    /// runs. One setting, two places to reach it.
    /// </remarks>
    public bool Sounding
    {
        get => Options.Sound;
        set => Options.Sound = value;
    }

    /// <summary>Whether the menu is up, which is also whether the match is frozen.</summary>
    public bool Showing => Visible;

    public void Toggle()
    {
        Visible = !Visible;
        QueueRedraw();
    }

    public void Close()
    {
        Visible = false;
    }

    /// <summary>What a press landed on, if anything.</summary>
    public Choice Pressed(Vector2 at)
    {
        for (int index = 0; index < _buttons.Length; index++)
        {
            if (at.DistanceTo(_buttons[index]) <= _button)
            {
                return (Choice)(index + 1);
            }
        }

        // A press anywhere else is deliberately not a dismissal. Escape opens this and escape
        // closes it; a stray click on the scrim putting a player back into a live turn they were
        // not looking at is the kind of thing that loses a round.
        return Choice.Nothing;
    }

    public override void _Draw()
    {
        Vector2 viewport = Size;

        if (viewport.X <= 0f || viewport.Y <= 0f)
        {
            return;
        }

        // Over everything, dark enough that the match reads as suspended rather than as still going.
        DrawRect(new Rect2(Vector2.Zero, viewport), new Color(Palette.Ink, 0.62f));

        _button = Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.075f, 34f, 64f);

        float spacing = _button * 2.9f;
        Vector2 middle = viewport / 2f;

        _buttons = new[]
        {
            middle + new Vector2(-spacing, 0f),
            middle,
            middle + new Vector2(spacing, 0f),
        };

        foreach (Vector2 at in _buttons)
        {
            DrawCircle(at, _button, Palette.Panel);
            DrawArc(at, _button, 0f, Mathf.Tau, 48, new Color(Palette.OnPanel, 0.25f), 2f);
        }

        // Carry on. Nudged right by a fraction of its own size, because a triangle's visual centre
        // is not its bounding box's.
        Glyphs.Play(
            this, _buttons[0] + new Vector2(_button * 0.06f, 0f), _button * 1.1f, Palette.OnPanel);

        Glyphs.Icon(this, Sounding ? "sound" : "mute", _buttons[1], _button * 0.95f, Palette.OnPanel);
        Glyphs.Back(this, _buttons[2], _button * 1.15f, Palette.OnPanel);
    }
}
