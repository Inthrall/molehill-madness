using System.Net.Http.Json;
using Molehill.Online;
using MoleSim.Match;

namespace Molehill.Online.Tests;

/// <summary>
/// The emote wheel: the only communication channel in the game.
/// </summary>
/// <remarks>
/// Two properties matter here and they pull in opposite directions. It has to work, because the design
/// leans on it to "carry the social load" now that text chat is gone. And it has to be incapable of
/// affecting a match, because the moment an emote could change an outcome it would be an input, and
/// inputs belong in plans where every client agrees on them.
/// </remarks>
[TestFixture]
public sealed class EmoteTests
{
    private const int MostPolls = 2000;
    private const double Frame = 30.0;

    private Clock _clock = null!;
    private TestRelay _relay = null!;
    private RelayClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new Clock(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        _relay = new TestRelay($"emote-{TestContext.CurrentContext.Test.ID}", _clock);
        _client = _relay.Client();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _relay.Dispose();
    }

    // ---- The wheel ----------------------------------------------------------------------

    [Test]
    public void TheWheelAndTheRelayAgreeOnHowManyThingsCanBeSaid()
    {
        Assert.That(
            Wheel.Count,
            Is.EqualTo(Relay.Api.EmoteRate.OnTheWheel),
            "The relay refuses an index that is not on the wheel, so the two have to match.");
    }

    [Test]
    public void EveryEmoteIsOnTheWheelExactlyOnce()
    {
        Emote[] all = Enum.GetValues<Emote>();

        Assert.That(Wheel.Order, Is.EquivalentTo(all));
        Assert.That(Wheel.Order.Distinct().Count(), Is.EqualTo(Wheel.Order.Length));
    }

    // ---- Saying and hearing -------------------------------------------------------------

    [Test]
    public async Task SomethingSaidByOnePlatoonReachesTheOther()
    {
        Player[] table = Seated(2);

        Assert.That(
            (await _client.Say(
                table[1].Online.Code, table[1].Online.Seating!.Token, Emote.AfterYou)).Ok,
            Is.True);

        RunAllUntil(table[..1], player => player.Online.Chat.From(1, player.Online.Elapsed) is not null);

        Said? heard = table[0].Online.Chat.From(1, table[0].Online.Elapsed);

        Assert.That(heard, Is.Not.Null);
        Assert.That(heard!.Emote, Is.EqualTo(Emote.AfterYou));
    }

    /// <summary>
    /// The wheel has to respond to the tap, not to the round trip. A control that waits for a server
    /// before showing anything feels broken, and this is the one part of the game where nothing
    /// depends on every client agreeing.
    /// </summary>
    [Test]
    public void YourOwnEmoteAppearsImmediately()
    {
        Player[] table = Seated(2);

        table[0].Online.Say(Emote.Laughing);

        Said? mine = table[0].Online.Chat.From(0, table[0].Online.Elapsed);

        Assert.That(mine, Is.Not.Null);
        Assert.That(mine!.Emote, Is.EqualTo(Emote.Laughing));
    }

    [Test]
    public void APictureGoesAwayOnItsOwn()
    {
        Conversation chat = new Conversation();

        chat.Heard(2, Emote.NiceShot, at: 10.0);

        Assert.That(chat.From(2, 10.5), Is.Not.Null);
        Assert.That(chat.From(2, 10.0 + Conversation.Lingers - 0.1), Is.Not.Null);
        Assert.That(chat.From(2, 10.0 + Conversation.Lingers + 0.1), Is.Null);
    }

    /// <summary>
    /// A platoon has one mole to hang a bubble over, so only the last thing it said can be shown. It
    /// also means a burst that got past the rate limit costs one drawn picture rather than a stack.
    /// </summary>
    [Test]
    public void OnlyTheLastThingASeatSaidIsKept()
    {
        Conversation chat = new Conversation();

        chat.Heard(1, Emote.Oops, at: 1.0);
        chat.Heard(1, Emote.Truce, at: 2.0);

        Assert.That(chat.From(1, 2.5)!.Emote, Is.EqualTo(Emote.Truce));
    }

    // ---- The rate limit -----------------------------------------------------------------

    /// <summary>
    /// The wheel is the only thing available to somebody who wants to be annoying. A fixed set of
    /// pictures cannot carry abuse, but eight taps a second of a sarcastic bow is harassment
    /// assembled out of parts that are individually fine.
    /// </summary>
    [Test]
    public async Task ASecondEmoteTooSoonIsRefused()
    {
        Player[] table = Seated(2);
        string code = table[0].Online.Code;
        string token = table[0].Online.Seating!.Token;

        Assert.That((await _client.Say(code, token, Emote.NiceShot)).Ok, Is.True);
        Assert.That((await _client.Say(code, token, Emote.NiceShot)).Ok, Is.False);
    }

    [Test]
    public async Task OnceTheGapHasPassedTheyCanSpeakAgain()
    {
        Player[] table = Seated(2);
        string code = table[0].Online.Code;
        string token = table[0].Online.Seating!.Token;

        await _client.Say(code, token, Emote.NiceShot);
        _clock.Pass(Relay.Api.EmoteRate.Gap + TimeSpan.FromMilliseconds(1));

        Assert.That((await _client.Say(code, token, Emote.WellPlayed)).Ok, Is.True);
    }

    /// <summary>The limit is per seat, so one loud player does not silence the table.</summary>
    [Test]
    public async Task OneSeatBeingRateLimitedDoesNotStopAnother()
    {
        Player[] table = Seated(2);

        await _client.Say(table[0].Online.Code, table[0].Online.Seating!.Token, Emote.Laughing);

        Assert.That(
            (await _client.Say(
                table[1].Online.Code, table[1].Online.Seating!.Token, Emote.AfterYou)).Ok,
            Is.True);
    }

    [Test]
    public async Task SomethingNotOnTheWheelIsRefused()
    {
        Player[] table = Seated(2);

        using HttpClient http = _relay.CreateClient();
        HttpRequestMessage request = new HttpRequestMessage(
            HttpMethod.Post, $"/matches/{table[0].Online.Code}/emote")
        {
            Content = JsonContent.Create(new { emote = 99 }),
        };
        request.Headers.Add("X-Seat-Token", table[0].Online.Seating!.Token);

        HttpResponseMessage response = await http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task SpeakingWithoutASeatTokenIsRefused()
    {
        Player[] table = Seated(2);

        Reply<bool> reply = await _client.Say(
            table[0].Online.Code, "not-a-token", Emote.NiceShot);

        Assert.That(reply.Outcome, Is.EqualTo(RelayOutcome.NotYourSeat));
    }

    // ---- The invariant ------------------------------------------------------------------

    /// <summary>
    /// The property that keeps emotes safe to send out of band: they cannot reach the simulation.
    /// </summary>
    /// <remarks>
    /// An emote does not go through PlanCodec, is not in a plan, and is never handed to a MoleMatch.
    /// If any of that changed it would become an input, and an input that only some clients received
    /// is a desync. So a whole round is played out with a conversation going on over the top of it and
    /// the state hashes are compared against a match where nobody said anything.
    /// </remarks>
    [Test]
    public void AConversationCannotChangeWhatHappensInARound()
    {
        Player[] table = Seated(2);

        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        table[0].Online.Say(Emote.AfterYou);
        table[1].PlanAndCommit(WeaponId.BeetleLauncher, power: 190);
        table[1].Online.Say(Emote.Laughing);

        RunAllUntil(table, player => player.Online.Stage == OnlineStage.RoundReady);

        ulong seed = table[0].Online.Seed;
        Plan[] plans = table[0].Online.Plans.ToArray();
        ulong[] chatty = table.Select(player => player.TakeRound()).ToArray();

        Assert.That(chatty.Distinct().Count(), Is.EqualTo(1), "The two players diverged.");

        // The same round again, in silence.
        MoleMatch quiet = MoleMatch.Create(2, seed, 400, 240);
        RoundFeeder.Feed(quiet, plans);
        quiet.ResolveRound();

        Assert.That(
            quiet.StateHash(),
            Is.EqualTo(chatty[0]),
            "Saying something changed the outcome, which makes an emote an input.");
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Player[] Seated(int playerCount)
    {
        Player host = Player.Hosting(_client, playerCount, MatchPace.Live);
        RunUntil(host, () => host.Online.Stage != OnlineStage.Arriving);

        List<Player> table = new List<Player> { host };

        for (int seat = 1; seat < playerCount; seat++)
        {
            Player joiner = Player.Joining(_client, host.Online.Code);
            RunUntil(joiner, () => joiner.Online.Stage != OnlineStage.Arriving);
            table.Add(joiner);
        }

        Player[] seated = table.ToArray();
        RunAllUntil(seated, player => player.Online.Stage == OnlineStage.Planning);

        foreach (Player player in seated)
        {
            player.BuildWorld();
        }

        return seated;
    }

    private static void RunAllUntil(Player[] table, Func<Player, bool> done)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            bool everybody = true;

            foreach (Player player in table)
            {
                player.Online.Poll(Frame);

                if (!done(player))
                {
                    everybody = false;
                }
            }

            if (everybody)
            {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail(
            "Never got there. Stages: "
            + string.Join(", ", table.Select(player => player.Online.Stage)));
    }

    private static void RunUntil(Player player, Func<bool> done)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            if (done())
            {
                return;
            }

            player.Online.Poll(Frame);
            Thread.Sleep(1);
        }

        Assert.Fail($"Stuck at {player.Online.Stage} ({player.Online.Trouble}).");
    }
}
