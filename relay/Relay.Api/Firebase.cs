using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Relay.Api;

/// <summary>
/// The half of a Google service account key the relay needs to speak to Firebase.
/// </summary>
/// <remarks>
/// A key file has a dozen fields and four of them matter: which project to send to, who is asking,
/// the key that proves it, and where to exchange that for a bearer. The rest is metadata for tools
/// that are not this one.
///
/// Read once at startup rather than at the point of use, so a broken key file is one failure while
/// the process is coming up rather than a surprise at three in the morning when the first
/// notification of the day is due.
/// </remarks>
public sealed record ServiceAccount(
    string ProjectId, string ClientEmail, string PrivateKeyPem, Uri TokenUri)
{
    /// <summary>Where Google exchanges a signed assertion for a bearer, absent anything else.</summary>
    public static readonly Uri DefaultTokenUri = new Uri("https://oauth2.googleapis.com/token");

    /// <summary>
    /// Reads a service account key file, or says it is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for a file that is simply not a key, because what an unreadable
    /// key means depends on who is asking: at startup it should stop the process, and in a test it
    /// is an assertion.
    /// </remarks>
    public static ServiceAccount? Read(string json)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? project = Text(parsed.RootElement, "project_id");
            string? email = Text(parsed.RootElement, "client_email");
            string? key = Text(parsed.RootElement, "private_key");
            string? token = Text(parsed.RootElement, "token_uri");

            if (project is null || email is null || key is null)
            {
                return null;
            }

            if (token is null)
            {
                return new ServiceAccount(project, email, key, DefaultTokenUri);
            }

            return Uri.TryCreate(token, UriKind.Absolute, out Uri? where)
                ? new ServiceAccount(project, email, key, where)
                : null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement found)
            && found.ValueKind == JsonValueKind.String
            && found.GetString() is string value
            && value.Length > 0
                ? value
                : null;
}

/// <summary>
/// Where the relay finds its Firebase credentials, if it has any.
/// </summary>
public static class Firebase
{
    /// <summary>The relay's own setting: a path to a service account key file.</summary>
    public const string SettingName = "Relay:Firebase:ServiceAccount";

    /// <summary>Google's own convention, honoured so a container can be wired the usual way.</summary>
    public const string EnvironmentName = "GOOGLE_APPLICATION_CREDENTIALS";

    /// <summary>
    /// The configured service account, or null if the relay has not been given one.
    /// </summary>
    /// <remarks>
    /// Null means "no Firebase project, write the notifications down instead", which is the right
    /// answer for a development run and the wrong one for a deployment. A key that was configured
    /// and cannot be used is therefore not null: it throws. An operator who set the path and got
    /// silence would have no way to tell a missing key from a quiet day, and a quiet day is what
    /// anybody would assume.
    /// </remarks>
    public static ServiceAccount? Configured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? path = configuration[SettingName];

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable(EnvironmentName);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"No service account key at '{path}'. Unset {SettingName} to log notifications instead.");
        }

        return ServiceAccount.Read(File.ReadAllText(path))
            ?? throw new InvalidOperationException(
                $"'{path}' is not a Google service account key: it wants project_id, client_email and private_key.");
    }
}

/// <summary>
/// Bearers for the messaging API, minted from the service account key and kept until they go stale.
/// </summary>
/// <remarks>
/// Firebase's v1 API takes an OAuth2 bearer rather than the old server key, and the way to get one
/// without a Google client library is this: sign a short JWT with the account's private key, hand it
/// to Google's token endpoint, receive an hour's bearer. That is the whole of what a library would
/// do for this one case, and pulling one in for it would put a dependency the size of a house into a
/// service whose entire point is that it is a handful of files.
///
/// Cached, because a bearer fetched per notification would be a round trip and an RSA signature for
/// every buzz, and dropped a minute early so one cannot expire between the check and the send. Minted
/// one at a time behind a semaphore rather than a lock, since minting is a network call and a lock
/// held across one of those is how a thread pool starves.
/// </remarks>
public sealed class GoogleTokens : IDisposable
{
    /// <summary>What the relay is asking to be allowed to do, and nothing else.</summary>
    public const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    /// <summary>How long an assertion claims to be good for. Google's ceiling is an hour.</summary>
    private static readonly TimeSpan AssertionLife = TimeSpan.FromMinutes(30);

    /// <summary>How far before expiry a cached bearer stops being offered.</summary>
    private static readonly TimeSpan Early = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly ServiceAccount _account;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    private string? _bearer;
    private DateTimeOffset _stale;

    public GoogleTokens(ServiceAccount account, HttpClient http, TimeProvider clock)
    {
        _account = account;
        _http = http;
        _clock = clock;
    }

    /// <summary>
    /// A usable bearer, minted if the held one has gone stale, or null if Google would not issue one.
    /// </summary>
    public async Task<string?> Bearer(CancellationToken cancel = default)
    {
        if (_bearer is string held && _clock.GetUtcNow() < _stale)
        {
            return held;
        }

        await _gate.WaitAsync(cancel).ConfigureAwait(false);

        try
        {
            // Checked again inside the gate. Several nudges can arrive at an empty cache together,
            // and the second one through should use what the first one minted.
            DateTimeOffset now = _clock.GetUtcNow();

            if (_bearer is string fresh && now < _stale)
            {
                return fresh;
            }

            (string? minted, TimeSpan life) = await Mint(now, cancel).ConfigureAwait(false);

            if (minted is null)
            {
                return null;
            }

            _bearer = minted;
            _stale = now + (life > Early ? life - Early : life);

            return minted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Throws the held bearer away, for when the far end has just refused it.
    /// </summary>
    /// <remarks>
    /// A bearer can stop working before it expires: the key can be revoked, the service account can
    /// lose the role, the clocks can disagree. Without this the relay would keep presenting the same
    /// rejected token until the hour was up, and every notification in that hour would be late.
    /// </remarks>
    public void Forget()
    {
        _bearer = null;
        _stale = default;
    }

    private async Task<(string? Bearer, TimeSpan Life)> Mint(
        DateTimeOffset now, CancellationToken cancel)
    {
        using FormUrlEncodedContent form = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>(
                    "grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", Assertion(now)),
            });

        using HttpResponseMessage reply =
            await _http.PostAsync(_account.TokenUri, form, cancel).ConfigureAwait(false);

        if (!reply.IsSuccessStatusCode)
        {
            return (null, TimeSpan.Zero);
        }

        string body = await reply.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, TimeSpan.Zero);
        }

        using (parsed)
        {
            if (!parsed.RootElement.TryGetProperty("access_token", out JsonElement token)
                || token.GetString() is not string bearer)
            {
                return (null, TimeSpan.Zero);
            }

            TimeSpan life = parsed.RootElement.TryGetProperty("expires_in", out JsonElement seconds)
                && seconds.TryGetInt32(out int lasts)
                    ? TimeSpan.FromSeconds(lasts)
                    : AssertionLife;

            return (bearer, life);
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>The signed claim that the account is who it says it is and wants what it wants.</summary>
    private string Assertion(DateTimeOffset now)
    {
        long issued = now.ToUnixTimeSeconds();

        string header = Wire.Url(Wire.Json(writer =>
        {
            writer.WriteString("alg", "RS256");
            writer.WriteString("typ", "JWT");
        }));

        string claims = Wire.Url(Wire.Json(writer =>
        {
            writer.WriteString("iss", _account.ClientEmail);
            writer.WriteString("scope", Scope);
            writer.WriteString("aud", _account.TokenUri.AbsoluteUri);
            writer.WriteNumber("iat", issued);
            writer.WriteNumber("exp", issued + (long)AssertionLife.TotalSeconds);
        }));

        string signing = $"{header}.{claims}";

        using RSA key = RSA.Create();
        key.ImportFromPem(_account.PrivateKeyPem);

        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signing}.{Wire.Url(signature)}";
    }
}

/// <summary>
/// Sends a decided nudge to Firebase Cloud Messaging.
/// </summary>
/// <remarks>
/// The queue was always the interesting half and it was built first: nudges are decided, throttled
/// against the design's "one a day per match" rule, and recorded. This only empties the box. What is
/// worth knowing about it is which answers from the far end mean try again and which mean stop,
/// because getting that backwards is how an outbox either loses notifications or spins on a dead
/// phone for ever.
///
/// One thing here has never met the real service, and it is worth naming rather than leaving to be
/// discovered: there is no Firebase project behind this repository, so the request is built from the
/// published contract and verified against a stub. Everything up to the socket is exercised, the
/// signature and the token exchange and the URL and the headers and the body and every branch of the
/// error handling. Whether Google likes the bytes is the one claim the tests cannot make.
/// </remarks>
public sealed partial class FirebaseNudgeSender : INudgeSender, IDisposable
{
    /// <summary>Where the v1 API lives.</summary>
    public static readonly Uri Messaging = new Uri("https://fcm.googleapis.com/");

    /// <summary>
    /// How long a notification is worth delivering.
    /// </summary>
    /// <remarks>
    /// A day, which is the same as the quiet period between nudges for one seat, and deliberately so.
    /// If a phone has been off for longer than that the next notification is already due, and telling
    /// somebody about yesterday's round on the way to today's is noise.
    /// </remarks>
    private const string Ttl = "86400s";

    private readonly HttpClient _http;
    private readonly ServiceAccount _account;
    private readonly GoogleTokens _tokens;
    private readonly Uri _endpoint;
    private readonly ILogger<FirebaseNudgeSender> _log;

    public FirebaseNudgeSender(
        HttpClient http,
        ServiceAccount account,
        TimeProvider clock,
        ILogger<FirebaseNudgeSender> log)
        : this(http, account, clock, log, Messaging)
    {
    }

    /// <summary>The same, pointed somewhere else, which is how a test reaches it.</summary>
    public FirebaseNudgeSender(
        HttpClient http,
        ServiceAccount account,
        TimeProvider clock,
        ILogger<FirebaseNudgeSender> log,
        Uri messaging)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(messaging);

        _http = http;
        _account = account;
        _tokens = new GoogleTokens(account, http, clock);
        _log = log;
        _endpoint = new Uri(messaging, $"v1/projects/{account.ProjectId}/messages:send");
    }

    /// <summary>
    /// Lets go of the bearer cache.
    /// </summary>
    /// <remarks>
    /// Not the HttpClient, which belongs to whoever handed it over. The container owns the one the
    /// running relay uses and disposes it at shutdown; a test owns the one it made.
    /// </remarks>
    public void Dispose() => _tokens.Dispose();

    public async Task<Delivery> Send(Nudge nudge, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(nudge);

        string? bearer = await _tokens.Bearer(cancel).ConfigureAwait(false);

        if (bearer is null)
        {
            NoBearer(_account.ClientEmail);

            return Delivery.Deferred;
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new ByteArrayContent(Body(nudge));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage reply = await _http
            .SendAsync(request, cancel)
            .ConfigureAwait(false);

        if (reply.IsSuccessStatusCode)
        {
            return Delivery.Sent;
        }

        string body = await reply.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        return Read(reply.StatusCode, body, nudge);
    }

    /// <summary>
    /// What a refusal means for the nudge that caused it.
    /// </summary>
    /// <remarks>
    /// Firebase names its own reason in the body, in a details entry, and that name is worth more
    /// than the status code it arrives with: a 400 is a malformed message and a malformed token
    /// both, and only one of those is a reason to forget where a player was. The code is the fallback
    /// for the answers that carry no body, which are mostly the ones from in front of the service
    /// rather than from it.
    /// </remarks>
    private Delivery Read(HttpStatusCode code, string body, Nudge nudge)
    {
        string? reason = Reason(body);

        switch (reason)
        {
            case "UNREGISTERED":
            case "SENDER_ID_MISMATCH":
                DeviceGone(nudge.Code, nudge.Seat, reason);

                return Delivery.Unregistered;

            case "UNAVAILABLE":
            case "INTERNAL":
            case "QUOTA_EXCEEDED":
                return Delivery.Deferred;

            default:
                break;
        }

        if (code == HttpStatusCode.Unauthorized)
        {
            // The bearer was refused rather than the message. Throwing it away costs one signature
            // and gets the next pass a fresh one; keeping it would mean an hour of silence.
            _tokens.Forget();

            return Delivery.Deferred;
        }

        if (code == HttpStatusCode.NotFound)
        {
            DeviceGone(nudge.Code, nudge.Seat, "NOT_FOUND");

            return Delivery.Unregistered;
        }

        if (code == HttpStatusCode.TooManyRequests || (int)code >= 500)
        {
            return Delivery.Deferred;
        }

        Refused(nudge.Code, nudge.Seat, (int)code, reason ?? "no reason given");

        return Delivery.Dropped;
    }

    private static string? Reason(string body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body);

            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (error.TryGetProperty("details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement detail in details.EnumerateArray())
                {
                    if (detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("errorCode", out JsonElement named)
                        && named.GetString() is string errorCode)
                    {
                        return errorCode;
                    }
                }
            }

            return error.TryGetProperty("status", out JsonElement status)
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The message: a notification the phone can show, and the data to come back to.
    /// </summary>
    /// <remarks>
    /// Both halves, deliberately. A data-only message is handled by the app, and in an Anytime match
    /// the app has not been open since yesterday, so the notification block is the only part that
    /// reaches somebody who is not already playing. The data rides along for the deep link: it is
    /// what tells the client which match and which round to open when the notification is tapped.
    ///
    /// The words are the exception that proves the wordless rule. Everything the game draws is a
    /// picture, and this is not drawn by the game: it is a line in somebody's notification shade next
    /// to their email, and there is no picture that says "it is your turn" there. When the game is
    /// localised these become loc keys against the app's own strings, rather than text sent by a
    /// server that has no idea what language the phone is in.
    /// </remarks>
    private static byte[] Body(Nudge nudge)
    {
        return Wire.Json(writer =>
        {
            writer.WriteStartObject("message");
            writer.WriteString("token", nudge.DeviceToken);

            writer.WriteStartObject("data");
            writer.WriteString("code", nudge.Code);
            writer.WriteString("seat", nudge.Seat.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("round", nudge.Round.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("notification");
            writer.WriteString("title", "Molehill Madness");
            writer.WriteString("body", $"Your move in {nudge.Code}.");
            writer.WriteEndObject();

            writer.WriteStartObject("android");

            // Normal rather than high. The round has hours left on it, and a game that wakes a
            // sleeping phone for a turn nobody is waiting on is a game whose notifications get
            // turned off.
            writer.WriteString("priority", "normal");
            writer.WriteString("ttl", Ttl);

            // One match, one line in the shade. A second nudge for the same match replaces the
            // first rather than stacking under it.
            writer.WriteString("collapse_key", nudge.Code);

            writer.WriteStartObject("notification");
            writer.WriteString("tag", nudge.Code);
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "Firebase would not issue a bearer for {Account}. The outbox keeps its nudges.")]
    private partial void NoBearer(string account);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Information,
        Message = "Match {Code} seat {Seat} has no device any more ({Reason}). Forgetting it.")]
    private partial void DeviceGone(string code, int seat, string reason);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Error,
        Message = "Firebase refused the nudge for match {Code} seat {Seat}: {Status}, {Reason}.")]
    private partial void Refused(string code, int seat, int status, string reason);
}

/// <summary>Turning things into the shapes a wire wants, without a serialiser's opinions.</summary>
internal static class Wire
{
    /// <summary>Writes an object, so a caller writes only what goes inside it.</summary>
    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new MemoryStream();

        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Base64 as a URL and a JWT want it: no padding, two characters swapped.</summary>
    public static string Url(byte[] bytes) => Base64Url.EncodeToString(bytes);
}
