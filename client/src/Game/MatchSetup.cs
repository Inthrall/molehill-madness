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

    /// <summary>
    /// How the match is being played: everybody here, or everybody apart.
    /// </summary>
    /// <remarks>
    /// The distinction the menu draws is not offline versus online, which is a word about plumbing.
    /// It is "all of us round one screen" versus "us in different places", and those are different
    /// games: on the couch every platoon plans on this device, and apart only one of them does.
    /// </remarks>
    public static Table Where { get; set; } = Table.Couch;

    /// <summary>Which pace a hosted match runs at. Ignored on the couch.</summary>
    public static Molehill.Online.MatchPace Pace { get; set; } =
        Molehill.Online.MatchPace.Live;

    public enum Table
    {
        /// <summary>One device, everybody round it, every platoon planning here.</summary>
        Couch = 0,

        /// <summary>This device opened the lobby and holds seat zero.</summary>
        Hosting = 1,

        /// <summary>This device took a seat in somebody else's lobby.</summary>
        Joining = 2,

        /// <summary>
        /// This device asked to be put with whoever else was asking.
        /// </summary>
        /// <remarks>
        /// The only one of the four that needs an account, because it is the only one that puts a
        /// player with somebody they did not invite. Couch play needs nothing, and a code arrives
        /// from somebody you already know.
        /// </remarks>
        Strangers = 3,
    }
}
