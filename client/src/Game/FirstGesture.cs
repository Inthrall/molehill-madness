using Godot;

/// <summary>
/// A drawn paw that performs the one gesture, once, on a player's first planning screen.
/// </summary>
/// <remarks>
/// The design's whole tutorial, and the reason it can be this small is worth restating, because it is
/// the argument the rest of the game is built on. There are no solo modes, so there is no puzzle
/// ladder to hide a tutorial inside, and nothing is written down, so there is no text to explain
/// anything with. "The first round of a real match has to do the whole job."
///
/// It can, because everybody plans at the same time. A beginner gets forty-five unhurried seconds in
/// which nothing they do is real yet, three other people busy with their own screens, and no way to
/// embarrass themselves. Most of the teaching is already done by things that are not this: the plan is
/// previewed before it commits, the stamina bar falls off a cliff the moment a route enters dirt, and
/// the interface refuses rather than explains. What is left is that a first-time player has to work
/// out there is a gesture at all.
///
/// So: "on your first planning screen a drawn paw performs the drag once, slowly, and then gets out of
/// the way". Once ever, not once a match. And it gets out of the way the instant the player touches
/// anything, because a demonstration somebody is trying to interrupt has already finished its job.
/// </remarks>
public partial class FirstGesture : Control
{
    /// <summary>How long the paw takes to do it.</summary>
    /// <remarks>
    /// Slowly, as the design asks. Two seconds is slow enough to follow and short enough that it is
    /// over before a player who already knows the game gets annoyed by it.
    /// </remarks>
    private const double Takes = 2.0;

    /// <summary>How long it waits before starting, so it is not already moving when the round opens.</summary>
    private const double Settles = 0.6;

    /// <summary>How long it takes to fade, once it is done or has been interrupted.</summary>
    private const double Fades = 0.35;

    private double _elapsed;
    private double _faded;
    private bool _leaving;
    private Vector2 _from;
    private Vector2 _to;
    private bool _placed;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Never in the way of a press. The paw is a picture, not a control, and a beginner poking at
        // it should be steering their mole rather than pressing a demonstration.
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 80;
    }

    public override void _Process(double delta)
    {
        if (!_placed)
        {
            return;
        }

        _elapsed += delta;

        if (_leaving)
        {
            _faded += delta;

            if (_faded >= Fades)
            {
                QueueFree();
                return;
            }
        }
        else if (_elapsed >= Settles + Takes)
        {
            _leaving = true;
        }

        QueueRedraw();
    }

    /// <summary>
    /// Tells the paw where the gesture happens.
    /// </summary>
    /// <remarks>
    /// Handed in rather than worked out here, because what the gesture is depends on the layout: on a
    /// phone it is a push on the stick in the bottom left, and on a desktop it is a drag on the map
    /// itself. Both are a drag, which is the point the design is making when it says there is one
    /// gesture.
    /// </remarks>
    public void Demonstrate(Vector2 from, Vector2 to)
    {
        _from = from;
        _to = to;
        _placed = true;

        // The clock starts when the positions arrive, not when the node is created. The first version
        // read the touch layout in BeginRound, before LayOut had run, so the stick was still at the
        // origin and the paw performed its whole demonstration in the top-left corner of the screen.
        _elapsed = 0;
    }

    /// <summary>Whether it knows where the gesture happens yet.</summary>
    public bool Placed => _placed;

    /// <summary>
    /// Called when the player does something, at which point the paw has done its job.
    /// </summary>
    /// <remarks>
    /// Not a pause and not a restart. Somebody who has started steering has understood, and a
    /// demonstration that carries on over the top of them is now clutter.
    /// </remarks>
    public void Interrupted()
    {
        _leaving = true;
    }

    public override void _Draw()
    {
        if (!_placed)
        {
            return;
        }

        float alpha = _leaving
            ? Mathf.Max(0f, 1f - (float)(_faded / Fades))
            : Mathf.Min(1f, (float)(_elapsed / Settles));

        if (alpha <= 0f)
        {
            return;
        }

        // Eased, so the paw accelerates away and settles rather than sliding at a constant rate. A
        // linear drag reads as an animation; an eased one reads as a hand.
        float along = Mathf.Clamp((float)((_elapsed - Settles) / Takes), 0f, 1f);
        float eased = along * along * (3f - (2f * along));

        Vector2 at = _from.Lerp(_to, eased);
        float size = Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.075f, 34f, 70f);

        DrawTrail(alpha, size);

        // Twice: a dark one offset, then a light one on top. The first version drew it in ink only,
        // which put a dark paw on the dark stick it was demonstrating and made the game's entire
        // tutorial almost invisible. Drawn this way it reads on the panel, on the soil and on the sky,
        // and it has to, because the control it points at is dark and the space around it is not.
        float lift = Mathf.Max(2f, size * 0.055f);

        Glyphs.Pointing(
            this, at + new Vector2(lift, lift), size, new Color(Palette.Ink, alpha * 0.55f));
        Glyphs.Pointing(this, at, size, new Color(Palette.Paper, alpha));
    }

    /// <summary>
    /// The path the paw is taking, drawn behind it and fading in as it goes.
    /// </summary>
    /// <remarks>
    /// The trail is what makes this a demonstration of a drag rather than a picture of a paw moving.
    /// Without it a first-time player sees something slide across the screen; with it they see the
    /// shape of a gesture and where it starts.
    /// </remarks>
    private void DrawTrail(float alpha, float size)
    {
        const int steps = 18;
        float along = Mathf.Clamp((float)((_elapsed - Settles) / Takes), 0f, 1f);

        for (int step = 0; step < steps; step++)
        {
            float at = step / (float)(steps - 1);

            if (at > along)
            {
                break;
            }

            float eased = at * at * (3f - (2f * at));

            Vector2 dot = _from.Lerp(_to, eased);

            DrawCircle(dot, size * 0.1f, new Color(Palette.Ink, alpha * 0.35f));
            DrawCircle(dot, size * 0.07f, new Color(Palette.Paper, alpha * 0.75f));
        }

        // Where it started, marked, because a gesture with no origin is half a gesture.
        DrawArc(_from, size * 0.42f, 0, Mathf.Tau, 28, new Color(Palette.Ink, alpha * 0.45f), 4f);
        DrawArc(_from, size * 0.42f, 0, Mathf.Tau, 28, new Color(Palette.Paper, alpha * 0.9f), 2f);
    }
}
