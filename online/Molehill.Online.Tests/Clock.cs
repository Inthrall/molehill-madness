namespace Molehill.Online.Tests;

/// <summary>
/// A clock the test moves by hand.
/// </summary>
/// <remarks>
/// So that a day-long round window is something a test can step over rather than sit through. The
/// relay takes its time from a TimeProvider precisely so this can exist: without it the forfeit path
/// could only be tested by waiting out the shortest window the relay allows, which is a minute, and a
/// test nobody will run is not a test.
///
/// Hand-written rather than pulled from Microsoft.Extensions.TimeProvider.Testing, because two
/// methods is cheaper than a package and this needs nothing else.
/// </remarks>
internal sealed class Clock : TimeProvider
{
    private DateTimeOffset _now;

    public Clock(DateTimeOffset from) => _now = from;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves time forward. Nothing moves it back.</summary>
    public void Pass(TimeSpan much) => _now = _now.Add(much);
}
