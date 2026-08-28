using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Platform-level parental approval: what it takes to let an under-threshold account meet strangers.
/// </summary>
/// <remarks>
/// Every test here is a way somebody would try to get an approval they were not given, which is the
/// only useful way to test a safeguard. The design allows a child into the pool "with platform-level
/// parental approval", and the two words that carry the weight are platform-level: if a player can
/// cause this to be true, the age gate has a hole in it wearing the name of a protection, which is
/// worse than having no approval at all because it reads as safety to anybody auditing the feature.
/// </remarks>
[TestFixture]
public sealed class ApprovalTests
{
    private const string Platform = "teststore";

    private ECDsa _key = null!;
    private ECDsa _impostor = null!;
    private string _public = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _public = _key.ExportSubjectPublicKeyInfoPem();
        _now = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown()
    {
        _key.Dispose();
        _impostor.Dispose();
    }

    // ---- Reading a grant -------------------------------------------------------------------

    [Test]
    public void APlatformsOwnGrantIsBelieved()
    {
        string token = Approvals.Sign(_key, new Grant("account-one", Platform, _now));

        Grant? read = Approvals.Read(token, Keys(), "account-one", _now);

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Platform, Is.EqualTo(Platform));
        Assert.That(read.Account, Is.EqualTo("account-one"));
    }

    /// <summary>
    /// The one that matters. Anybody can write the payload; only the platform can sign it, and a
    /// relay that checked the contents without checking the signature would be a relay where the
    /// approval is a field the client fills in.
    /// </summary>
    [Test]
    public void AGrantSignedByAnybodyElseIsWorthNothing()
    {
        string forged = Approvals.Sign(_impostor, new Grant("account-one", Platform, _now));

        Assert.That(Approvals.Read(forged, Keys(), "account-one", _now), Is.Null);
    }

    /// <summary>
    /// The account is inside the signature and compared against the caller, so a grant issued for
    /// one child cannot be presented by another. Without this, one real approval would let every
    /// account in the game past the gate.
    /// </summary>
    [Test]
    public void AGrantAboutSomebodyElseIsWorthNothing()
    {
        string token = Approvals.Sign(_key, new Grant("account-one", Platform, _now));

        Assert.That(Approvals.Read(token, Keys(), "account-two", _now), Is.Null);
    }

    [Test]
    public void APlatformNobodyConfiguredIsAStranger()
    {
        string token = Approvals.Sign(_key, new Grant("account-one", "some-other-store", _now));

        Assert.That(Approvals.Read(token, Keys(), "account-one", _now), Is.Null);
    }

    /// <summary>
    /// Both directions. Old is a replay of something captured off a wire, and from the future is
    /// either a clock nobody set or somebody buying themselves a year of validity.
    /// </summary>
    [Test]
    public void AGrantIsOnlyGoodForAnHourEitherWay()
    {
        string token = Approvals.Sign(_key, new Grant("account-one", Platform, _now));

        Assert.That(
            Approvals.Read(token, Keys(), "account-one", _now + TimeSpan.FromMinutes(59)),
            Is.Not.Null);
        Assert.That(
            Approvals.Read(token, Keys(), "account-one", _now + TimeSpan.FromHours(2)), Is.Null);
        Assert.That(
            Approvals.Read(token, Keys(), "account-one", _now - TimeSpan.FromHours(2)), Is.Null);
    }

    [TestCase("")]
    [TestCase("nonsense")]
    [TestCase("one.two.three")]
    [TestCase("!!!.!!!")]
    public void RubbishIsWorthNothingAndDoesNotThrow(string token)
    {
        Assert.That(Approvals.Read(token, Keys(), "account-one", _now), Is.Null);
    }

    /// <summary>
    /// The default this relay ships in. No platform has been integrated with, so nobody is entitled
    /// to approve anything, and a relay that fell back to trusting the client when it had no keys
    /// would be one whose safeguard switched itself off exactly when it was least supervised.
    /// </summary>
    [Test]
    public void WithNoPlatformConfiguredNothingCanBeApproved()
    {
        string token = Approvals.Sign(_key, new Grant("account-one", Platform, _now));

        Assert.That(
            Approvals.Read(
                token, new Dictionary<string, string>(StringComparer.Ordinal), "account-one", _now),
            Is.Null);
    }

    // ---- What an approval buys ---------------------------------------------------------------

    /// <summary>
    /// The design's rule in full: adults always, children with an approval, and an account nobody
    /// has asked never, approved or not. An approval about a band nobody has established is an
    /// approval of nothing.
    /// </summary>
    [TestCase(AgeBand.Adult, false, true)]
    [TestCase(AgeBand.Adult, true, true)]
    [TestCase(AgeBand.Child, false, false)]
    [TestCase(AgeBand.Child, true, true)]
    [TestCase(AgeBand.Unknown, false, false)]
    [TestCase(AgeBand.Unknown, true, false)]
    public void AnApprovalOnlyMovesAChild(AgeBand band, bool approved, bool allowed)
    {
        Assert.That(Allowed.Matchmaking(band, approved), Is.EqualTo(allowed));
    }

    /// <summary>
    /// And it buys nothing at all where an address is concerned. The design gives under-threshold
    /// accounts no email collection, full stop; reading the approval as covering that too would be
    /// inventing consent out of an adjacent sentence.
    /// </summary>
    [Test]
    public void AnApprovalDoesNotBuyAnAddress()
    {
        Assert.That(Allowed.EmailCollection(AgeBand.Child), Is.False);
        Assert.That(Allowed.EmailCollection(AgeBand.Unknown), Is.False);
        Assert.That(Allowed.EmailCollection(AgeBand.Adult), Is.True);
    }

    // ---- Through the pipeline -----------------------------------------------------------------

    [Test]
    public async Task AnApprovedChildIsLetIntoThePool()
    {
        using RelayFactory relay = Relay($"approved-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened child = await Account(client, AgeBand.Child);

        Assert.That(
            (await Queue(client, child)).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden),
            "Unapproved, and therefore not going anywhere near a stranger.");

        HttpResponseMessage approved = await Present(
            client, child, Approvals.Sign(_key, new Grant(child.Id, Platform, DateTimeOffset.UtcNow)));

        Assert.That(approved.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await Queue(client, child)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task AForgedGrantChangesNothing()
    {
        using RelayFactory relay = Relay($"forged-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened child = await Account(client, AgeBand.Child);

        HttpResponseMessage refused = await Present(
            client,
            child,
            Approvals.Sign(_impostor, new Grant(child.Id, Platform, DateTimeOffset.UtcNow)));

        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Queue(client, child)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    /// <summary>
    /// One real approval must not become an approval for everybody. The grant names its account
    /// inside the signature, so passing somebody else's along changes nothing.
    /// </summary>
    [Test]
    public async Task SomebodyElsesApprovalIsNotYours()
    {
        using RelayFactory relay = Relay($"borrowed-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened approved = await Account(client, AgeBand.Child);
        Opened borrower = await Account(client, AgeBand.Child);

        string grant = Approvals.Sign(
            _key, new Grant(approved.Id, Platform, DateTimeOffset.UtcNow));

        Assert.That(
            (await Present(client, borrower, grant)).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Queue(client, borrower)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    /// <summary>
    /// A relay with no platform configured, which is how this one ships. The grant is perfectly
    /// genuine and there is nobody entitled to have issued it.
    /// </summary>
    [Test]
    public async Task AGenuineGrantIsRefusedWhereNoPlatformIsConfigured()
    {
        using RelayFactory relay = new RelayFactory($"nokeys-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened child = await Account(client, AgeBand.Child);

        HttpResponseMessage refused = await Present(
            client, child, Approvals.Sign(_key, new Grant(child.Id, Platform, DateTimeOffset.UtcNow)));

        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Dictionary<string, string> Keys() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { [Platform] = _public };

    private RelayFactory Relay(string name) =>
        new RelayFactory(
            name,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{Approvals.SettingSection}:{Platform}"] = _public,
            });

    private static async Task<Opened> Account(HttpClient client, AgeBand band)
    {
        HttpResponseMessage made = await client.PostAsJsonAsync("/accounts", new OpenAccount(band));

        return (await made.Content.ReadFromJsonAsync<Opened>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static Task<HttpResponseMessage> Present(
        HttpClient client, Opened account, string grant) =>
        client.SendAsync(Owned(
            new HttpRequestMessage(HttpMethod.Post, "/accounts/approval")
            {
                Content = JsonContent.Create(new Approve(grant)),
            },
            account));

    private static Task<HttpResponseMessage> Queue(HttpClient client, Opened account) =>
        client.SendAsync(Owned(
            new HttpRequestMessage(HttpMethod.Post, "/queue")
            {
                Content = JsonContent.Create(new JoinPool(2, Pace.Live)),
            },
            account));

    private static HttpRequestMessage Owned(HttpRequestMessage request, Opened account)
    {
        request.Headers.Add("X-Account", account.Id);
        request.Headers.Add("X-Account-Secret", account.Secret);

        return request;
    }
}
