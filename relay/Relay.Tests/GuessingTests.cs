using System.Net;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// That the code space cannot be read out at speed.
/// </summary>
/// <remarks>
/// The measurement that prompted this: against the relay without a limiter, one keep-alive
/// connection managed three thousand code lookups a second, which is the whole twenty-four to the
/// fifth space in about three quarters of an hour. Every live lobby in the game, found from a
/// laptop, joinable by anyone, with no account and no age gate in the way.
///
/// These tests are about the shape of the defence rather than the exact numbers, which are tuned
/// against what a real client does. What has to stay true is that a burst is allowed, that sustained
/// sweeping is not, and that being refused is a refusal rather than a queue.
/// </remarks>
[TestFixture]
public sealed class GuessingTests
{
    /// <summary>
    /// Sweeping runs out of tokens. Without a limiter this loop would have returned 404 four hundred
    /// times and told the caller four hundred codes were free.
    /// </summary>
    [Test]
    public async Task SweepingTheCodeSpaceIsCutOff()
    {
        using RelayFactory relay = new RelayFactory($"sweep-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        int refused = 0;

        for (int attempt = 0; attempt < Guessing.Burst * 4; attempt++)
        {
            HttpResponseMessage answer = await client.GetAsync($"/lobbies/{Made(attempt)}");

            if (answer.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused++;
            }
        }

        Assert.That(
            refused, Is.GreaterThan(0),
            "A sweep of the code space has to be stopped, not merely slowed.");
    }

    /// <summary>
    /// And a burst still gets through, because a person typing a code in and a client waiting in a
    /// lobby both make several requests in a row and neither is an attack.
    /// </summary>
    [Test]
    public async Task AnOrdinaryBurstIsNotRefused()
    {
        using RelayFactory relay = new RelayFactory($"burst-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        HttpResponseMessage opened = await client.PostAsJsonAsync(
            "/lobbies", new OpenLobby(4, Pace.Anytime));

        Joined host = await opened.AsJoined();

        for (int look = 0; look < 10; look++)
        {
            HttpResponseMessage answer = await client.GetAsync($"/lobbies/{host.Code}");

            Assert.That(
                answer.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "Watching one lobby fill is not sweeping the space.");
        }
    }

    /// <summary>A code of the right shape, so the route matches and the lookup is a real one.</summary>
    private static string Made(int attempt)
    {
        char[] letters = new char[GameCode.Length];

        for (int at = 0; at < letters.Length; at++)
        {
            letters[at] = GameCode.Alphabet[(attempt + (at * 7)) % GameCode.Alphabet.Length];
        }

        return new string(letters);
    }
}
