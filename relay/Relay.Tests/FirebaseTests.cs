using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Talking to Firebase Cloud Messaging: the signature, the bearer, and what a refusal means.
/// </summary>
/// <remarks>
/// There is no Firebase project behind this repository, so the one thing these cannot claim is that
/// Google likes the bytes. Everything up to the socket is real: a genuine RSA key signs a genuine
/// assertion, the assertion is verified here the way Google would verify it, the bearer that comes
/// back is used and reused and thrown away when it is refused, and every documented error is put in
/// front of the sender to see which of the four answers it gives.
///
/// The stub is a message handler rather than a substitute for the sender, because most of what could
/// be wrong with this lives in the bytes: the URL the project id is spliced into, the header the
/// bearer rides in, the shape of the message, and which field of an error body carries the reason.
/// None of that exists when a fake is asked whether it was called.
/// </remarks>
[TestFixture]
public sealed class FirebaseTests
{
    private const string Project = "molehill-madness";
    private const string Email = "relay@molehill-madness.iam.gserviceaccount.com";

    private static readonly Uri Tokens = new Uri("https://oauth2.example/token");
    private static readonly Uri Messaging = new Uri("https://fcm.example/");

    private RSA _key = null!;
    private ServiceAccount _account = null!;
    private Stub _stub = null!;
    private HttpClient _http = null!;
    private Stopped _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _key = RSA.Create(2048);
        _account = new ServiceAccount(Project, Email, _key.ExportPkcs8PrivateKeyPem(), Tokens);
        _stub = new Stub();
        _http = new HttpClient(_stub, disposeHandler: false);
        _clock = new Stopped { Now = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero) };
    }

    [TearDown]
    public void TearDown()
    {
        _http.Dispose();
        _stub.Dispose();
        _key.Dispose();
    }

    // ---- The key file -------------------------------------------------------------------

    [Test]
    public void AServiceAccountKeyIsReadForTheFourFieldsThatMatter()
    {
        ServiceAccount? read = ServiceAccount.Read(
            """
            {
              "type": "service_account",
              "project_id": "molehill-madness",
              "private_key_id": "abc123",
              "private_key": "-----BEGIN PRIVATE KEY-----\nnot a real one\n-----END PRIVATE KEY-----\n",
              "client_email": "relay@molehill-madness.iam.gserviceaccount.com",
              "token_uri": "https://oauth2.googleapis.com/token"
            }
            """);

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.ProjectId, Is.EqualTo(Project));
        Assert.That(read.ClientEmail, Is.EqualTo(Email));
        Assert.That(read.TokenUri, Is.EqualTo(new Uri("https://oauth2.googleapis.com/token")));

        // The escaped newlines have to survive into the PEM, or nothing can import it.
        Assert.That(read.PrivateKeyPem, Does.Contain("\n"));
    }

    /// <summary>
    /// A key file missing the parts that identify it is not a key file. Half-reading one and failing
    /// later would put the failure at the first notification rather than at startup, which is the
    /// whole reason this is read up front.
    /// </summary>
    [Test]
    public void SomethingThatIsNotAKeyFileIsRefusedRatherThanHalfRead()
    {
        Assert.That(ServiceAccount.Read("not json at all"), Is.Null);
        Assert.That(ServiceAccount.Read("[1, 2, 3]"), Is.Null);
        Assert.That(ServiceAccount.Read("""{"project_id":"p"}"""), Is.Null);
        Assert.That(
            ServiceAccount.Read("""{"project_id":"p","client_email":"e","private_key":""}"""),
            Is.Null);
    }

    /// <summary>Google's own default, for a key file written without one.</summary>
    [Test]
    public void AKeyWithNoTokenUriUsesGoogles()
    {
        ServiceAccount? read = ServiceAccount.Read(
            """{"project_id":"p","client_email":"e","private_key":"k"}""");

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.TokenUri, Is.EqualTo(ServiceAccount.DefaultTokenUri));
    }

    // ---- The assertion ------------------------------------------------------------------

    /// <summary>
    /// The signature is the whole of the authentication, so it is checked the way Google checks it:
    /// take the two encoded halves as they were sent, verify them against the account's public key,
    /// and read the claims out of what was signed rather than out of what was meant.
    /// </summary>
    [Test]
    public async Task TheAssertionIsSignedWithTheAccountKeyAndSaysWhoWantsWhat()
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        await Sender().Send(Nudge("BADGE", 1, 4));

        string assertion = _stub.Form(Tokens)["assertion"];
        string[] parts = assertion.Split('.');

        Assert.That(parts, Has.Length.EqualTo(3));

        Assert.That(
            _key.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Base64Url.DecodeFromChars(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1),
            Is.True,
            "Google would reject the assertion.");

        using JsonDocument header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
        using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));

        Assert.That(header.RootElement.GetProperty("alg").GetString(), Is.EqualTo("RS256"));
        Assert.That(claims.RootElement.GetProperty("iss").GetString(), Is.EqualTo(Email));
        Assert.That(claims.RootElement.GetProperty("aud").GetString(),
            Is.EqualTo(Tokens.AbsoluteUri));
        Assert.That(claims.RootElement.GetProperty("scope").GetString(),
            Is.EqualTo(GoogleTokens.Scope));

        long issued = claims.RootElement.GetProperty("iat").GetInt64();
        long expires = claims.RootElement.GetProperty("exp").GetInt64();

        Assert.That(issued, Is.EqualTo(_clock.Now.ToUnixTimeSeconds()));
        Assert.That(expires, Is.GreaterThan(issued));
        Assert.That(expires - issued, Is.LessThanOrEqualTo(3600), "Google's ceiling is an hour.");

        Assert.That(
            _stub.Form(Tokens)["grant_type"],
            Is.EqualTo("urn:ietf:params:oauth:grant-type:jwt-bearer"));
    }

    // ---- The message --------------------------------------------------------------------

    [Test]
    public async Task TheMessageGoesToTheProjectWithTheBearerAndTheDeviceToken()
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        Delivery answer = await Sender().Send(Nudge("BADGE", 1, 4));

        Assert.That(answer, Is.EqualTo(Delivery.Sent));

        HttpRequestMessage sent = _stub.Last(Send());

        Assert.That(sent.RequestUri, Is.EqualTo(Send()));
        Assert.That(sent.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
        Assert.That(sent.Headers.Authorization?.Parameter, Is.EqualTo("first"));

        using JsonDocument body = JsonDocument.Parse(_stub.Body(Send()));
        JsonElement message = body.RootElement.GetProperty("message");

        Assert.That(message.GetProperty("token").GetString(), Is.EqualTo("device-1"));

        // The data is the deep link: which match, which seat, which round to open.
        JsonElement data = message.GetProperty("data");

        Assert.That(data.GetProperty("code").GetString(), Is.EqualTo("BADGE"));
        Assert.That(data.GetProperty("seat").GetString(), Is.EqualTo("1"));
        Assert.That(data.GetProperty("round").GetString(), Is.EqualTo("4"));

        // And something a phone can show when the game has not been open since yesterday.
        Assert.That(message.GetProperty("notification").GetProperty("body").GetString(),
            Does.Contain("BADGE"));
    }

    /// <summary>
    /// One match, one line in the notification shade, and a message that stops being worth
    /// delivering after a day because by then the next one is due.
    /// </summary>
    [Test]
    public async Task AMatchCollapsesOntoItsOwnNotificationAndExpiresInADay()
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        await Sender().Send(Nudge("BADGE", 1, 4));

        using JsonDocument body = JsonDocument.Parse(_stub.Body(Send()));
        JsonElement android = body.RootElement.GetProperty("message").GetProperty("android");

        Assert.That(android.GetProperty("collapse_key").GetString(), Is.EqualTo("BADGE"));
        Assert.That(android.GetProperty("ttl").GetString(), Is.EqualTo("86400s"));
        Assert.That(android.GetProperty("priority").GetString(), Is.EqualTo("normal"));
    }

    // ---- The bearer ---------------------------------------------------------------------

    [Test]
    public async Task TheBearerIsMintedOnceAndUsedAgain()
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        FirebaseNudgeSender sender = Sender();

        await sender.Send(Nudge("BADGE", 0, 1));
        await sender.Send(Nudge("BADGE", 1, 1));

        Assert.That(_stub.Count(Tokens), Is.EqualTo(1));
        Assert.That(_stub.Count(Send()), Is.EqualTo(2));
    }

    /// <summary>
    /// A bearer is dropped a minute before it expires, so one cannot go stale between the check that
    /// says it is good and the request that uses it.
    /// </summary>
    [Test]
    public async Task AnExpiringBearerIsReplacedBeforeItExpires()
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        FirebaseNudgeSender sender = Sender();

        await sender.Send(Nudge("BADGE", 0, 1));

        // Fifty nine and a half minutes into an hour's bearer: half a minute of life left, which is
        // not enough to be worth starting a request with.
        _clock.Now += TimeSpan.FromMinutes(59.5);
        _stub.Answer(Tokens, Bearer("second", lasts: 3600));

        await sender.Send(Nudge("BADGE", 1, 1));

        Assert.That(_stub.Count(Tokens), Is.EqualTo(2));
        Assert.That(_stub.Last(Send()).Headers.Authorization?.Parameter, Is.EqualTo("second"));
    }

    /// <summary>
    /// A bearer can stop working before it expires: a revoked key, a lost role, clocks that
    /// disagree. Keeping the rejected one until the hour was up would mean an hour of silence.
    /// </summary>
    [Test]
    public async Task ARefusedBearerIsThrownAwayAndTheNextSendMintsAFreshOne()
    {
        _stub.Answer(Tokens, Bearer("stale", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.Unauthorized, """{"error":{"status":"UNAUTHENTICATED"}}""");

        FirebaseNudgeSender sender = Sender();

        Assert.That(await sender.Send(Nudge("BADGE", 0, 1)), Is.EqualTo(Delivery.Deferred));

        _stub.Answer(Tokens, Bearer("fresh", lasts: 3600));
        _stub.Answer(Send(), HttpStatusCode.OK, "{}");

        Assert.That(await sender.Send(Nudge("BADGE", 0, 1)), Is.EqualTo(Delivery.Sent));
        Assert.That(_stub.Count(Tokens), Is.EqualTo(2));
        Assert.That(_stub.Last(Send()).Headers.Authorization?.Parameter, Is.EqualTo("fresh"));
    }

    /// <summary>
    /// Nothing to send with is not a reason to lose the notification. The outbox exists so that a
    /// service being unreachable delays a buzz instead of dropping it.
    /// </summary>
    [Test]
    public async Task ANudgeIsKeptWhenNoBearerCanBeMintedAtAll()
    {
        _stub.Answer(Tokens, HttpStatusCode.ServiceUnavailable, string.Empty);

        Assert.That(await Sender().Send(Nudge("BADGE", 0, 1)), Is.EqualTo(Delivery.Deferred));
        Assert.That(_stub.Count(Send()), Is.EqualTo(0), "Nothing should have been sent.");
    }

    // ---- What a refusal means -----------------------------------------------------------

    /// <summary>
    /// The four answers, against the errors Firebase documents. Getting these backwards is how an
    /// outbox either spins on a dead phone for ever or gives up on a service that was merely busy.
    /// </summary>
    [TestCase(404, "UNREGISTERED", Delivery.Unregistered)]
    [TestCase(403, "SENDER_ID_MISMATCH", Delivery.Unregistered)]
    [TestCase(400, "INVALID_ARGUMENT", Delivery.Dropped)]
    [TestCase(429, "QUOTA_EXCEEDED", Delivery.Deferred)]
    [TestCase(503, "UNAVAILABLE", Delivery.Deferred)]
    [TestCase(500, "INTERNAL", Delivery.Deferred)]
    public async Task FirebasesOwnReasonDecidesWhatHappensToTheNudge(
        int status, string reason, Delivery expected)
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), (HttpStatusCode)status, Error(status, reason));

        Assert.That(await Sender().Send(Nudge("BADGE", 0, 1)), Is.EqualTo(expected));
    }

    /// <summary>
    /// Not everything that refuses a request is the service itself. A proxy, a load balancer or a
    /// name that does not resolve answers with a status and no body it knows how to write, so the
    /// status has to be enough on its own.
    /// </summary>
    [TestCase(502, Delivery.Deferred)]
    [TestCase(504, Delivery.Deferred)]
    [TestCase(429, Delivery.Deferred)]
    [TestCase(404, Delivery.Unregistered)]
    [TestCase(400, Delivery.Dropped)]
    public async Task AnAnswerWithNoReasonInItFallsBackToItsStatus(int status, Delivery expected)
    {
        _stub.Answer(Tokens, Bearer("first", lasts: 3600));
        _stub.Answer(Send(), (HttpStatusCode)status, string.Empty);

        Assert.That(await Sender().Send(Nudge("BADGE", 0, 1)), Is.EqualTo(expected));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private FirebaseNudgeSender Sender() =>
        new FirebaseNudgeSender(
            _http, _account, _clock, NullLogger<FirebaseNudgeSender>.Instance, Messaging);

    private static Uri Send() =>
        new Uri($"https://fcm.example/v1/projects/{Project}/messages:send");

    private static Nudge Nudge(string code, int seat, int round) =>
        new Nudge(code, seat, round, $"device-{seat}", DateTimeOffset.UnixEpoch);

    private static string Bearer(string token, int lasts) =>
        $$"""{"access_token":"{{token}}","expires_in":{{lasts}},"token_type":"Bearer"}""";

    /// <summary>An error body shaped the way Firebase shapes one.</summary>
    private static string Error(int status, string reason) =>
        $$"""
        {
          "error": {
            "code": {{status}},
            "message": "something went wrong",
            "status": "FAILED",
            "details": [
              {
                "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                "errorCode": "{{reason}}"
              }
            ]
          }
        }
        """;

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class Stopped : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The far end, answering by URL and keeping what it was asked.
    /// </summary>
    /// <remarks>
    /// Both endpoints go through one handler because both are the same conversation: the token
    /// exchange and the send are two halves of one notification, and a test that primed them
    /// separately could not catch the sender putting the bearer from one on the other.
    /// </remarks>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Dictionary<Uri, (HttpStatusCode Status, string Body)> _answers = new();
        private readonly List<(HttpRequestMessage Request, string Body)> _asked = new();

        public void Answer(Uri where, string body) => Answer(where, HttpStatusCode.OK, body);

        public void Answer(Uri where, HttpStatusCode status, string body) =>
            _answers[where] = (status, body);

        public int Count(Uri where) =>
            _asked.Count(asked => asked.Request.RequestUri == where);

        public HttpRequestMessage Last(Uri where) =>
            _asked.Last(asked => asked.Request.RequestUri == where).Request;

        public string Body(Uri where) =>
            _asked.Last(asked => asked.Request.RequestUri == where).Body;

        /// <summary>The last form posted to somewhere, as its fields.</summary>
        public Dictionary<string, string> Form(Uri where)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string pair in Body(where).Split('&'))
            {
                string[] halves = pair.Split('=', 2);

                fields[Uri.UnescapeDataString(halves[0])] =
                    Uri.UnescapeDataString(halves[1].Replace('+', ' '));
            }

            return fields;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            _asked.Add((request, body));

            (HttpStatusCode status, string answer) = _answers.TryGetValue(
                request.RequestUri!, out (HttpStatusCode Status, string Body) found)
                    ? found
                    : (HttpStatusCode.NotImplemented, string.Empty);

            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }
}
