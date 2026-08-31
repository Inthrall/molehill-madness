using Godot;
using Molehill.Online;

/// <summary>
/// The wheel: tap to open, tap a picture to say it.
/// </summary>
/// <remarks>
/// A radial menu because that is the shape a thumb can hit without looking, which matters more than
/// it sounds: the moments worth saying something are while everybody is planning, and a player
/// hunting for a button is a player not planning.
///
/// Closed by default and one tap from open. It is deliberately small and out of the way, because this
/// is chatter and the map is the game. Sending is fire and forget: the picture appears over your own
/// mole the instant you tap, without waiting for the relay, since nothing about an emote depends on
/// every client agreeing.
/// </remarks>
public partial class EmoteWheel : Control
{
    private OnlineMatch? _online;
    private bool _open;
    private Vector2 _middle;
    private Vector2 _ring;
    private float _button;
    private readonly Vector2[] _spots = new Vector2[Wheel.Count];
    private float _spot;

    /// <summary>How long after saying something the wheel stays shut.</summary>
    /// <remarks>
    /// Matches the relay's own limit, so the control is not offering a tap that would be refused.
    /// A button that does nothing teaches a player that the game is unreliable.
    /// </remarks>
    private const double Rests = 2.0;

    private double _rested = Rests;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Presses have to reach this before they reach the map, or opening the wheel would also
        // steer a mole. Stop rather than Ignore, and only while it matters: see _GuiInput.
        MouseFilter = MouseFilterEnum.Pass;
        ZIndex = 95;
    }

    public override void _Process(double delta)
    {
        _rested += delta;
        QueueRedraw();
    }

    /// <summary>Which match this is talking in, or null on the couch where the room is the channel.</summary>
    public void Watch(OnlineMatch? online)
    {
        _online = online;
        Visible = online is not null;

        if (online is null)
        {
            _open = false;
        }
    }

    private bool Ready => _rested >= Rests;

    // ---- Input -----------------------------------------------------------------------

    /// <summary>
    /// Handles a press, and says whether it was ours.
    /// </summary>
    /// <remarks>
    /// Called by the match scene before it does anything else with a press, rather than relying on
    /// Godot's input ordering. The map is a Node2D that reads raw events, so a Control sitting on top
    /// of it does not naturally get first refusal, and a tap that both opened the wheel and steered a
    /// mole is the kind of thing that only shows up in a playtest.
    /// </remarks>
    public bool Pressed(Vector2 at)
    {
        if (_online is null)
        {
            return false;
        }

        if (_open)
        {
            for (int spot = 0; spot < _spots.Length; spot++)
            {
                if (at.DistanceTo(_spots[spot]) <= _spot)
                {
                    Say(Wheel.Order[spot]);
                    return true;
                }
            }

            // Anywhere else shuts it. A radial menu that needs a separate close button is a menu
            // people leave open.
            _open = false;
            QueueRedraw();

            return true;
        }

        if (at.DistanceTo(_middle) <= _button * 1.2f)
        {
            _open = Ready;
            QueueRedraw();

            return true;
        }

        return false;
    }

    private void Say(Emote emote)
    {
        _online!.Say(emote);
        _open = false;
        _rested = 0;
        QueueRedraw();
    }

    // ---- Drawing ---------------------------------------------------------------------

    public override void _Draw()
    {
        if (_online is null)
        {
            return;
        }

        Vector2 viewport = Size;

        _button = ButtonFor(viewport);
        _spot = _button * 0.92f;

        // Bottom right, with the rest of the furniture a thumb reaches for.
        //
        // It was bottom left first, which is where the touch stick lives, and an open wheel
        // blanketed the stick completely; then top left, in the sky, which fixed that and put the
        // one chatty control on the screen as far from the hand as a corner can be. The thumbs own
        // the bottom corners, and this is a thing said with a thumb, so the touch layout gives up
        // the corner instead: see TouchControls.CornerSpoken, which is fed from Reach below.
        _middle = viewport - new Vector2(_button * 2.2f, _button * 2.2f);

        // Where the open ring is centred, which is not where its button is. Eight spots at a
        // radius of three buttons do not fit in a corner, and the first version simply drew them
        // off the screen: at 1280 by 720 the topmost emote of a wheel opened in the top left corner
        // sat a whole button above the top edge, so one of the eight could never be tapped. The
        // ring springs inward far enough to fit, and the wash over the map is what makes that read
        // as one control rather than two.
        float clearance = (_button * OpenReach) + _spot + (_button * 0.4f);

        _ring = new Vector2(
            Mathf.Clamp(_middle.X, clearance, Mathf.Max(clearance, viewport.X - clearance)),
            Mathf.Clamp(_middle.Y, clearance, Mathf.Max(clearance, viewport.Y - clearance)));

        if (_open)
        {
            DrawOpen();
        }

        DrawButton();
    }

    /// <summary>How far out the open wheel's spots sit, in buttons.</summary>
    private const float OpenReach = 3.1f;

    /// <summary>How big the button is on a screen of this size.</summary>
    /// <remarks>
    /// A function of the screen rather than a field written while drawing, so <see cref="Reach"/>
    /// can answer before the first frame has been drawn. Reading the field there left the touch
    /// layout believing the corner was free for exactly one frame.
    /// </remarks>
    private static float ButtonFor(Vector2 viewport) =>
        Mathf.Clamp(Mathf.Min(viewport.X, viewport.Y) * 0.045f, 22f, 40f);

    /// <summary>
    /// How much of its corner this needs kept clear, in pixels.
    /// </summary>
    /// <remarks>
    /// Read by the touch layout, which lifts its own corner buttons by this much. The wheel gets
    /// first refusal on every press in the match scene, so anything it sits on top of is a control
    /// that has silently stopped working, and "end turn opens the emotes" is not a fault anybody
    /// would think to look for.
    ///
    /// Zero while the wheel is not on the screen, which is every match that is not online. The
    /// touch layout then keeps its corner, which is what it had before there was a wheel to give it
    /// up to.
    /// </remarks>
    public float Reach => Visible ? ButtonFor(Size) * 2.6f : 0f;

    private void DrawButton()
    {
        // Dimmed while the rate limit is still running, so the wheel never offers a tap the relay
        // would refuse.
        Color panel = Ready ? Palette.Panel : new Color(Palette.Panel, 0.5f);
        Color ink = Ready ? Palette.OnPanel : new Color(Palette.OnPanel, 0.35f);

        DrawCircle(_middle, _button, panel);
        DrawArc(_middle, _button, 0, Mathf.Tau, 32, new Color(ink, 0.5f), 2f);

        // The wheel's own icon is a speech bubble with nothing in it, which is the honest picture of
        // a channel that carries no words.
        Glyphs.Say(this, Emote.Thinking, _middle, _button * 1.05f, ink);
    }

    private void DrawOpen()
    {
        float reach = _button * OpenReach;

        // A wash over the map, so the open wheel reads as a thing in front of the game rather than
        // eight buttons floating on it.
        DrawCircle(_ring, reach + _spot, new Color(Palette.Ink, 0.35f));

        for (int spot = 0; spot < _spots.Length; spot++)
        {
            // Clockwise from the top, matching the order the wheel declares, so the layout and the
            // list cannot drift apart.
            float angle = Mathf.DegToRad(-90f + (spot * 360f / _spots.Length));

            _spots[spot] = _ring + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach);

            DrawCircle(_spots[spot], _spot, Palette.Panel);
            DrawArc(_spots[spot], _spot, 0, Mathf.Tau, 28, new Color(Palette.OnPanel, 0.45f), 2f);
            Glyphs.Say(this, Wheel.Order[spot], _spots[spot], _spot * 1.15f, Palette.OnPanel);
        }
    }
}
