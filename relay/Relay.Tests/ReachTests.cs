using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// What a code on its own is worth, and what it is not.
/// </summary>
/// <remarks>
/// A code has to be enough to take a seat, because that is the whole joining model: somebody reads
/// five letters out and anybody of any age can use them. It was also enough to read every plan of
/// every settled round, the seed, the forfeits and the emote stream, none of which anybody outside
/// the match has any business seeing.
/// </remarks>
[TestFixture]
public sealed class ReachTests
{
    /// <summary>
    /// Reading a round is for the seats in it. It hands back every plan and the seed, and it is not
    /// even a read: it sweeps forfeits, advances the match and decides who gets a notification.
    /// </summary>
    [Test]
    public async Task ReadingARoundNeedsASeatInTheMatch()
    {
        using RelayFactory relay = new RelayFactory($"reach-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client);

        HttpResponseMessage stranger = await client.GetAsync($"/matches/{host.Code}/rounds/1");

        Assert.That(stranger.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        HttpResponseMessage seated = await client.GetRound(host.Code, 1, host.Token);

        Assert.That(seated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>A token from a different match is not a token for this one.</summary>
    [Test]
    public async Task SomebodyElsesSeatTokenDoesNotOpenThisMatch()
    {
        using RelayFactory relay = new RelayFactory($"reach-other-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined mine = await Open(client);
        Joined theirs = await Open(client);

        HttpResponseMessage answer = await client.GetRound(mine.Code, 1, theirs.Token);

        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// The limit that protects the person who owns the inbox rather than the person asking. Accounts
    /// are free and unlimited, so metering the account alone let a handful of fresh ones aim as much
    /// mail as they liked at one address: measured at about fifty thousand a minute.
    /// </summary>
    [Test]
    public async Task OneAddressGetsOneCodeHoweverManyAccountsAsk()
    {
        using Posted post = new Posted();
        using RelayFactory relay = new RelayFactory(
            $"reach-mail-{Guid.NewGuid():N}", services: services => services.AddSingleton<IEmailSender>(post));
        using HttpClient client = relay.CreateClient();

        const string victim = "somebody@example.com";
        int accepted = 0;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            Opened account = await Account(client);

            HttpRequestMessage asked =
                new HttpRequestMessage(HttpMethod.Post, "/accounts/email")
                {
                    Content = JsonContent.Create(new ClaimEmail(victim)),
                };

            asked.Headers.Add("X-Account", account.Id);
            asked.Headers.Add("X-Account-Secret", account.Secret);

            if ((await client.SendAsync(asked)).StatusCode == HttpStatusCode.Accepted)
            {
                accepted++;
            }
        }

        Assert.That(
            accepted, Is.EqualTo(1),
            "A fresh account per request must not buy a fresh code to somebody else's inbox.");
        Assert.That(post.Sent, Has.Count.EqualTo(1));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static async Task<Joined> Open(HttpClient client)
    {
        HttpResponseMessage opened = await client.PostAsJsonAsync(
            "/lobbies", new OpenLobby(2, Pace.Anytime));

        return await opened.AsJoined();
    }

    private static async Task<Opened> Account(HttpClient client)
    {
        HttpResponseMessage made = await client.PostAsJsonAsync(
            "/accounts", new OpenAccount(AgeBand.Adult));

        return (await made.Content.ReadFromJsonAsync<Opened>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private sealed class Posted : IEmailSender, IDisposable
    {
        public List<string> Sent { get; } = new List<string>();

        public Task<bool> Send(string address, string code, CancellationToken cancel = default)
        {
            Sent.Add(address);

            return Task.FromResult(true);
        }

        public void Dispose() => Sent.Clear();
    }
}
