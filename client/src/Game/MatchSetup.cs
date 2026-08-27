/// <summary>What the menu decided, for the match scene to pick up.</summary>
/// <remarks>
/// Static, because a Godot scene change tears down the old tree and builds the new one before
/// anything gets a chance to hand it anything, so there is no constructor to pass this to. An
/// autoload would be the heavier alternative and would buy nothing: this is two values written by
/// one screen and read by the next.
///
/// The seed is here rather than baked into the match so a rematch is a different garden. It stays
/// fixed under <c>--demo</c>, because a driven match that changed every run would break every
/// comparison the render checks depend on.
/// </remarks>
public static class MatchSetup
{
    /// <summary>The design's default, and its hard ceiling.</summary>
    public const int MostPlayers = 4;

    /// <summary>Two is the fewest the design allows to sit down.</summary>
    public const int FewestPlayers = 2;

    /// <summary>The seed every driven match plays, so recorded runs stay comparable.</summary>
    public const ulong DrivenSeed = 20260826UL;

    public static int PlayerCount { get; set; } = MostPlayers;

    public static ulong Seed { get; set; } = DrivenSeed;
}
