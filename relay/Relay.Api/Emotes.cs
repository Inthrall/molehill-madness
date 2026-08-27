namespace Relay.Api;

/// <summary>One picture, sent by one seat, at one moment.</summary>
/// <remarks>
/// The relay knows an emote is a small number and knows nothing else about it. That is not the same
/// evasion as it not parsing plans: an emote genuinely is a number, because the design's whole safety
/// argument is that communication is "a fixed wheel of emotes and canned phrases, which cannot be
/// used to harass, groom or leak personal information". A fixed wheel means the wire carries an index
/// into it and never a string, so there is nothing here for anybody to put a phone number in.
/// </remarks>
public sealed record Emoted(long Id, string Code, int Seat, int Emote, DateTimeOffset At);

/// <summary>
/// How often one seat may say something.
/// </summary>
/// <remarks>
/// The emote wheel is the only communication channel in the game, which makes it the only thing
/// available to somebody who wants to be annoying. A fixed set of pictures cannot carry abuse, but it
/// can absolutely carry spam, and eight taps a second of a sarcastic bow is harassment assembled out
/// of parts that are individually fine.
///
/// So the rate limit is a safety feature rather than a capacity one. It is enforced on the relay and
/// not in the client, because the client is the thing a determined person would change.
/// </remarks>
public static class EmoteRate
{
    /// <summary>The shortest gap between one seat's emotes.</summary>
    /// <remarks>
    /// Two seconds. Slow enough that nobody can drum with it, fast enough for the exchange the design
    /// wants: a shot lands, somebody says nice shot, somebody else says after you.
    /// </remarks>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    /// <summary>How many the wheel has, so the relay can refuse an index that is not on it.</summary>
    /// <remarks>
    /// The relay does have to know this one number. Storing an emote index nothing can draw would
    /// leave the client deciding what to do with a value from the future, and "ignore it" and "draw
    /// the wrong picture" look the same from here.
    /// </remarks>
    public const int OnTheWheel = 8;

    public static bool OnIt(int emote) => emote >= 0 && emote < OnTheWheel;
}
