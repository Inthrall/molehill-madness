using Godot;

/// <summary>
/// Whose turn it is, on a shared device, waiting for them to say they are ready.
/// </summary>
/// <remarks>
/// Passing one phone round a table has a problem no split screen has: the game changes hands and
/// nothing on screen changes, so the clock starts running for somebody who has not been handed the
/// phone yet and the first thing they see is a turn already half gone. Play testing asked for a
/// colour and a tick, which is exactly right: a colour says who, and a tick means the handover
/// happened rather than merely being announced.
///
/// It holds the clock rather than counting against it. A handover that ate the turn it was announcing
/// would be worse than no handover at all.
///
/// Only for a shared device. One player alone, a split screen where everybody can see their own pane,
/// and an online match all know whose turn it is already, and a card in front of them would be a
/// press for nothing.
/// </remarks>
public partial class TurnCard : Control
{
    private int _seat = -1;
    private bool _ready = true;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Stops the map being dragged through the card, which would let somebody play the turn
        // without ever picking the phone up.
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 95;
        Visible = false;
    }

    /// <summary>Whether the card is up and waiting to be dismissed.</summary>
    public bool Waiting => Visible && !_ready;

    /// <summary>
    /// Puts the card up for a platoon, unless it is already up for that platoon.
    /// </summary>
    public void Hand(int seat)
    {
        if (_seat == seat && Visible)
        {
            return;
        }

        _seat = seat;
        _ready = false;
        Visible = true;
        QueueRedraw();
    }

    /// <summary>Takes the card down, which is the handover being acknowledged.</summary>
    public void Taken()
    {
        _ready = true;
        Visible = false;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent what)
    {
        if (_ready)
        {
            return;
        }

        // Anywhere, not only on the tick. The tick says what to do and the whole card does it,
        // because a thumb aiming for a target it has only just seen should not be able to miss.
        bool pressed =
            (what is InputEventMouseButton mouse && mouse.Pressed)
            || what is InputEventScreenTouch { Pressed: true }
            || (what is InputEventKey key && key.Pressed);

        if (pressed)
        {
            Taken();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        if (_seat < 0)
        {
            return;
        }

        Vector2 viewport = Size;
        Color seat = Palette.Seat(_seat);

        // A wash of the platoon's colour over a dimmed map, so the colour is the first thing read
        // and the map is still recognisable underneath it.
        DrawRect(new Rect2(Vector2.Zero, viewport), new Color(0f, 0f, 0f, 0.72f));
        DrawRect(new Rect2(Vector2.Zero, viewport), new Color(seat, 0.16f));

        Vector2 middle = viewport / 2f;
        float unit = Mathf.Min(viewport.X, viewport.Y);
        float face = unit * 0.3f;

        // The mole, in their colour. The face artwork rather than a glyph: it is the same animal the
        // player is about to steer, which is the whole message.
        DrawCircle(middle - new Vector2(0f, unit * 0.06f), face * 0.62f, new Color(seat, 0.9f));
        DrawArc(
            middle - new Vector2(0f, unit * 0.06f), face * 0.62f, 0f, Mathf.Tau, 48,
            new Color(Palette.Paper, 0.85f), Mathf.Max(unit * 0.006f, 2f));

        Strip faces = Art.Faces;
        Vector2 at = middle - new Vector2(0f, unit * 0.06f);

        faces.Draw(
            this,
            new Rect2(at.X - (face / 2f), at.Y - (face / 2f), face, face),
            Art.Face.Level,
            mirrored: false);

        // The tick, below, in the platoon's colour on paper so it reads as the thing to press.
        float button = unit * 0.11f;
        Vector2 tick = middle + new Vector2(0f, unit * 0.26f);

        DrawCircle(tick, button, new Color(Palette.Paper, 0.92f));
        DrawArc(tick, button, 0f, Mathf.Tau, 40, seat, Mathf.Max(unit * 0.008f, 2f));
        Glyphs.Committed(this, tick, button * 1.5f, seat);
    }
}
