using Microsoft.Extensions.Logging.Abstractions;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Who gets told it is their turn, and how often they are allowed to be told.
/// </summary>
/// <remarks>
/// The rule the design fixes is "one notification a day per match at most", and it is a real
/// constraint rather than a nicety: an Anytime match runs for a fortnight with four players in it,
/// and a game that pushes on every resolution is a game people mute and then stop playing.
///
/// This is the half of push notifications worth being certain about. Whether a message reaches a
/// phone is Firebase's problem; whether the right people are chosen and the throttle holds is ours,
/// and it is entirely testable without a Firebase project in the loop.
/// </remarks>
[TestFixture]
public sealed class NudgeTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _store = MatchStore.InMemory($"nudge-{TestContext.CurrentContext.Test.ID}");
        _now = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- Devices ------------------------------------------------------------------------

    [Test]
    public void ASeatsDeviceIsRemembered()
    {
        Match match = Started(2);

        _store.RegisterDevice(match.Code, 0, "device-zero", "android", _now);

        IReadOnlyList<Device> devices = _store.Devices(match.Code);

        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].Seat, Is.EqualTo(0));
        Assert.That(devices[0].Token, Is.EqualTo("device-zero"));
        Assert.That(devices[0].Platform, Is.EqualTo("android"));
    }

    /// <summary>
    /// A player who reinstalls has a new push token and the old one is dead. Keeping both would mean
    /// sending every notification twice and half of them nowhere.
    /// </summary>
    [Test]
    public void ReregisteringReplacesTheOldDeviceRatherThanAddingOne()
    {
        Match match = Started(2);

        _store.RegisterDevice(match.Code, 0, "old", "android", _now);
        _store.RegisterDevice(match.Code, 0, "new", "android", _now.AddDays(3));

        IReadOnlyList<Device> devices = _store.Devices(match.Code);

        Assert.That(devices, Has.Count.EqualTo(1));
        Assert.That(devices[0].Token, Is.EqualTo("new"));
    }

    // ---- Deciding -----------------------------------------------------------------------

    [Test]
    public void EverybodyWithADeviceIsToldAboutANewRound()
    {
        Match match = Started(3);
        Devices(match.Code, 0, 1, 2);

        IReadOnlyList<Nudge> decided = Nudges.Decide(_store, match.Code, round: 2, _now);

        Assert.That(decided.Select(nudge => nudge.Seat), Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(decided.Select(nudge => nudge.Round), Is.All.EqualTo(2));
    }

    [Test]
    public void ASeatWithNoDeviceIsNotTold()
    {
        Match match = Started(3);
        Devices(match.Code, 0, 2);

        IReadOnlyList<Nudge> decided = Nudges.Decide(_store, match.Code, round: 2, _now);

        Assert.That(decided.Select(nudge => nudge.Seat), Is.EqualTo(new[] { 0, 2 }));
    }

    /// <summary>
    /// Telling somebody it is their turn when they have already taken it is the fastest way to teach
    /// them to ignore the notifications.
    /// </summary>
    [Test]
    public void ASeatThatHasAlreadyCommittedIsNotTold()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);

        // On round two before anybody submits for it. The store only takes a plan for the round the
        // match is on, which is what the endpoint has always enforced.
        _store.Advance(match.Code, 2, _now);
        _store.Submit(match.Code, 2, 0, new byte[] { 1 }, _now);

        IReadOnlyList<Nudge> decided = Nudges.Decide(_store, match.Code, round: 2, _now);

        Assert.That(decided.Select(nudge => nudge.Seat), Is.EqualTo(new[] { 1 }));
    }

    // ---- The throttle -------------------------------------------------------------------

    /// <summary>
    /// The design's rule. A Live match can run a dozen rounds in an evening, and a player should hear
    /// about it once.
    /// </summary>
    [Test]
    public void NobodyIsToldTwiceAboutTheSameMatchWithinADay()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);

        Assert.That(Nudges.Decide(_store, match.Code, 2, _now), Has.Count.EqualTo(2));

        Assert.That(
            Nudges.Decide(_store, match.Code, 3, _now.AddHours(1)),
            Is.Empty,
            "An hour later is well inside the quiet period.");

        Assert.That(
            Nudges.Decide(_store, match.Code, 4, _now.AddHours(23)),
            Is.Empty);
    }

    [Test]
    public void OnceTheDayIsUpTheyCanBeToldAgain()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);

        Nudges.Decide(_store, match.Code, 2, _now);

        IReadOnlyList<Nudge> later = Nudges.Decide(
            _store, match.Code, 3, _now + Nudges.Quiet + TimeSpan.FromMinutes(1));

        Assert.That(later, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Per match and per seat, not per device. Somebody playing three matches at once still hears
    /// about all three: it is one match being noisy that the rule is about.
    /// </summary>
    [Test]
    public void ADifferentMatchIsNotThrottledByThisOne()
    {
        Match first = Started(2);
        Match second = Started(2);

        Devices(first.Code, 0);
        Devices(second.Code, 0);

        Nudges.Decide(_store, first.Code, 2, _now);

        Assert.That(Nudges.Decide(_store, second.Code, 2, _now.AddMinutes(1)), Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Deciding is driven off round reads, and every client reads, so it happens repeatedly for the
    /// same round and must not queue the same notification more than once.
    /// </summary>
    [Test]
    public void DecidingTwiceForOneRoundQueuesOneNotification()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);

        Nudges.Decide(_store, match.Code, 2, _now);
        Nudges.Decide(_store, match.Code, 2, _now);

        Assert.That(_store.PendingNudges(), Has.Count.EqualTo(2));
    }

    [Test]
    public void ALobbyStillFillingNudgesNobody()
    {
        (Match match, Seat _) = _store.Open(3, Pace.Anytime, _now);

        _store.RegisterDevice(match.Code, 0, "device-zero", "android", _now);

        Assert.That(Nudges.Decide(_store, match.Code, 1, _now), Is.Empty);
    }

    // ---- The outbox ---------------------------------------------------------------------

    [Test]
    public async Task DrainingSendsEveryPendingNudgeOnce()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);
        Nudges.Decide(_store, match.Code, 2, _now);

        Counting sender = new Counting();
        NudgeDrain drain = new NudgeDrain(
            _store, sender, TimeProvider.System, NullLogger<NudgeDrain>.Instance);

        Assert.That(await drain.Drain(), Is.EqualTo(2));
        Assert.That(_store.PendingNudges(), Is.Empty);

        // And a second pass has nothing left to do, so nobody is buzzed twice.
        Assert.That(await drain.Drain(), Is.EqualTo(0));
        Assert.That(sender.Asked, Is.EqualTo(2));
    }

    /// <summary>
    /// A notification service being down should delay a buzz, not lose it. The nudge stays pending
    /// and is tried again, which is the whole reason this is an outbox rather than a call.
    /// </summary>
    [Test]
    public async Task ANudgeThatCouldNotBeSentStaysPending()
    {
        Match match = Started(2);
        Devices(match.Code, 0, 1);
        Nudges.Decide(_store, match.Code, 2, _now);

        Counting sender = new Counting { Answer = Delivery.Deferred };
        NudgeDrain drain = new NudgeDrain(
            _store, sender, TimeProvider.System, NullLogger<NudgeDrain>.Instance);

        Assert.That(await drain.Drain(), Is.EqualTo(0));
        Assert.That(_store.PendingNudges(), Has.Count.EqualTo(2));

        sender.Answer = Delivery.Sent;

        Assert.That(await drain.Drain(), Is.EqualTo(2));
        Assert.That(_store.PendingNudges(), Is.Empty);
    }

    /// <summary>
    /// A phone that has been wiped or reinstalled is gone for good, and the outbox has to be told
    /// apart from a service that is merely busy: retrying a dead token is a pass through the loop
    /// that can never succeed, once every five seconds, for the fortnight the match lasts.
    /// </summary>
    [Test]
    public async Task ADeadPhoneIsForgottenAndItsNudgeIsFinishedWith()
    {
        Match match = Started(2);
        Devices(match.Code, 0);
        Nudges.Decide(_store, match.Code, 2, _now);

        Counting sender = new Counting { Answer = Delivery.Unregistered };
        NudgeDrain drain = new NudgeDrain(
            _store, sender, TimeProvider.System, NullLogger<NudgeDrain>.Instance);

        // Nothing was delivered, so nothing is counted as sent.
        Assert.That(await drain.Drain(), Is.EqualTo(0));

        Assert.That(_store.PendingNudges(), Is.Empty);
        Assert.That(_store.Devices(match.Code), Is.Empty);
    }

    /// <summary>
    /// A message the service will not take is not the same as a phone that is not there. One is a
    /// nudge to give up on and the other is a player to give up on, and only one of them should cost
    /// the player the rest of the match's notifications.
    /// </summary>
    [Test]
    public async Task ARefusedMessageLeavesTheDeviceAlone()
    {
        Match match = Started(2);
        Devices(match.Code, 0);
        Nudges.Decide(_store, match.Code, 2, _now);

        Counting sender = new Counting { Answer = Delivery.Dropped };
        NudgeDrain drain = new NudgeDrain(
            _store, sender, TimeProvider.System, NullLogger<NudgeDrain>.Instance);

        Assert.That(await drain.Drain(), Is.EqualTo(0));

        Assert.That(_store.PendingNudges(), Is.Empty);
        Assert.That(_store.Devices(match.Code), Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A player who reinstalls between a nudge being decided and it failing has a working device
    /// registered under the same seat by the time the failure arrives. Forgetting that one because
    /// the token it replaced is dead would lose them for the rest of the match.
    /// </summary>
    [Test]
    public void ForgettingADeviceOnlyTakesTheOneThatDied()
    {
        Match match = Started(2);

        _store.RegisterDevice(match.Code, 0, "old", "android", _now);
        _store.RegisterDevice(match.Code, 0, "new", "android", _now);

        _store.ForgetDevice(match.Code, 0, "old");

        IReadOnlyList<Device> left = _store.Devices(match.Code);

        Assert.That(left, Has.Count.EqualTo(1));
        Assert.That(left[0].Token, Is.EqualTo("new"));

        _store.ForgetDevice(match.Code, 0, "new");

        Assert.That(_store.Devices(match.Code), Is.Empty);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Match Started(int players)
    {
        (Match opened, Seat _) = _store.Open(players, Pace.Anytime, _now);

        for (int seat = 1; seat < players; seat++)
        {
            _store.Join(opened.Code, _now);
        }

        return _store.Find(opened.Code)!;
    }

    private void Devices(string code, params int[] seats)
    {
        foreach (int seat in seats)
        {
            _store.RegisterDevice(code, seat, $"device-{seat}", "android", _now);
        }
    }

    /// <summary>A sender that counts what it was asked to send, and answers however it is told to.</summary>
    private sealed class Counting : INudgeSender
    {
        public int Asked { get; private set; }

        public Delivery Answer { get; set; } = Delivery.Sent;

        public Task<Delivery> Send(Nudge nudge, CancellationToken cancel = default)
        {
            Asked++;

            return Task.FromResult(Answer);
        }
    }
}
