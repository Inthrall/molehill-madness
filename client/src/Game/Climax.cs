using Godot;

/// <summary>The moment of a round worth slowing down for.</summary>
/// <remarks>
/// The design lists slow motion on the final impact alongside the in-world damage numbers and the
/// instant replay, both of which were already built. It is the cheapest thing that makes a replay
/// feel like a highlight rather than a log, and it costs nothing to find: the round resolved
/// before its first frame was drawn, so which moment was the moment is knowable up front, like
/// everything else the replay is cut from.
///
/// The last knockout if there was one, because a knockout is what a round is about and knockouts
/// tend to come at the end, which is the "final" the design asks for. Failing that the heaviest
/// hit, so a round where nobody went out still ends on something. Failing both, nothing at all: a
/// round where four moles walked about and missed does not deserve a slow motion replay of the
/// missing.
/// </remarks>
public readonly struct Climax
{
    public static Climax None => new Climax(-1, -1);

    public Climax(int tick, int slot)
    {
        Tick = tick;
        Slot = slot;
    }

    /// <summary>When it lands.</summary>
    public int Tick { get; }

    /// <summary>Which mole it happens to, so the right camera pushes in on it.</summary>
    public int Slot { get; }

    public bool Exists => Tick >= 0 && Slot >= 0;

    /// <summary>
    /// How much of the moment is on, from nothing to all of it, at a given point in the replay.
    /// </summary>
    /// <remarks>
    /// Ramped rather than switched. Slamming from full speed to a third and back is a stutter;
    /// easing into it over half a second and out over rather longer is the shape a broadcast uses,
    /// and the way out is the slower half because the eye wants to keep watching the aftermath.
    ///
    /// Measured in ticks rather than seconds because everything else about a recording is, and
    /// because the whole point is to land on one particular tick.
    /// </remarks>
    public float Weight(float atTick)
    {
        if (!Exists)
        {
            return 0f;
        }

        float since = atTick - Tick;

        if (since < -(HoldBefore + RampIn) || since > HoldAfter + RampOut)
        {
            return 0f;
        }

        if (since < -HoldBefore)
        {
            return Eased((since + HoldBefore + RampIn) / RampIn);
        }

        return since <= HoldAfter ? 1f : Eased(1f - ((since - HoldAfter) / RampOut));
    }

    /// <summary>Ticks spent easing in, which at thirty a second is about a third of a second.</summary>
    private const float RampIn = 10f;

    /// <summary>Ticks held at full before the moment, so the wind-up is already slow.</summary>
    private const float HoldBefore = 3f;

    /// <summary>Ticks held at full after it, which is where the pratfall actually happens.</summary>
    private const float HoldAfter = 11f;

    private const float RampOut = 16f;

    private static float Eased(float amount)
    {
        float clamped = Mathf.Clamp(amount, 0f, 1f);

        return clamped * clamped * (3f - (2f * clamped));
    }
}
