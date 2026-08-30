using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Molehill.Online
{
    /// <summary>
    /// The only thing in the game that speaks to the relay.
    /// </summary>
    /// <remarks>
    /// One implementation, used by the game and by the tests, which is deliberate. A second transport
    /// written for tests would be a substitute that could happily agree with a bug in the real one,
    /// and the bugs this layer can cause are desyncs, which are the most expensive kind to find later.
    /// So the tests drive this exact class against a real relay.
    ///
    /// Plain HttpClient rather than Godot's HttpRequest node, so nothing here needs an engine and the
    /// whole online flow can be tested without booting one. Two things follow from that and are worth
    /// knowing before an Android build: the export needs the INTERNET permission, and Android blocks
    /// cleartext HTTP by default from API 28, so a deployed relay has to be HTTPS.
    ///
    /// Nothing in here understands a plan. It moves bytes that came out of PlanCodec and hands back
    /// bytes to go into it, which is the same contract the relay keeps on the other side.
    /// </remarks>
    public sealed class RelayClient : IDisposable
    {
        /// <summary>
        /// How long to wait before deciding the relay is unreachable.
        /// </summary>
        /// <remarks>
        /// Short, because a player on a train would rather see us retrying than watch a frozen screen
        /// for a minute, and every call here is safe to repeat: opening a lobby is the only one that
        /// is not idempotent, and a lobby nobody joins costs a row.
        /// </remarks>
        public static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        /// <summary>
        /// What the last transport failure actually said, for somebody holding a phone.
        /// </summary>
        /// <remarks>
        /// Kept because every transport failure comes back as one outcome, Unreachable, and on a
        /// device the causes are not remotely alike. A phone in a tunnel, a relay that is not running,
        /// a wrong address and Android refusing a cleartext HTTP request all look identical to a
        /// player: dots that never stop. The last one in particular is a trap worth naming, since
        /// Android blocks plain HTTP by default from API 28 and a build pointed at an http:// relay
        /// fails every call before it leaves the device.
        ///
        /// Not shown to a player, who has no use for it. It is for a log, so somebody testing a build
        /// on a phone can tell a network from a mistake.
        /// </remarks>
        public string Trouble { get; private set; } = string.Empty;

        /// <summary>
        /// Where this client points, so a socket can be pointed at the same relay.
        /// </summary>
        /// <remarks>
        /// Exposed rather than passed around separately, because the two addresses have to agree and
        /// a caller holding both is a caller who can get them out of step. The doorbell turns this
        /// into a ws or wss address itself.
        /// </remarks>
        public Uri? Relay => _http.BaseAddress;

        public RelayClient(Uri relay)
            : this(new HttpClient { BaseAddress = relay, Timeout = Patience }, ownsHttp: true)
        {
        }

        public RelayClient(HttpClient http, bool ownsHttp = false)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _ownsHttp = ownsHttp;
        }

        // ---- Lobbies ----------------------------------------------------------------

        public Task<Reply<Seating>> Host(
            int playerCount,
            MatchPace pace,
            int windowSeconds = 0,
            CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/lobbies")
                    {
                        Content = Body(
                            $"{{\"playerCount\":{playerCount},\"pace\":\"{pace}\","
                            + $"\"windowSeconds\":{windowSeconds.ToString(CultureInfo.InvariantCulture)}}}"),
                    };

                    return request;
                },
                ReadSeating,
                cancel);

        public Task<Reply<Seating>> Join(string code, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(HttpMethod.Post, $"/lobbies/{Tidy(code)}/seats"),
                ReadSeating,
                cancel,
                // The relay says 409 for every conflict, and on this call it means the lobby filled
                // while the code was being read out. A player needs that told apart from having
                // already committed, because one of them is somebody else's fault.
                conflictMeans: RelayOutcome.Full);

        /// <summary>
        /// Comes back to a match this device is already in.
        /// </summary>
        /// <remarks>
        /// Not the same call as joining, which would take a second seat or be refused as full. This is
        /// what a client does when it starts up holding a stored token: it recovers the seat, the seed
        /// and, the part it cannot have kept, the round the match has reached.
        /// </remarks>
        public Task<Reply<Seating>> Resume(
            string code, string token, CancellationToken cancel = default) =>
            Call(
                () => Signed(HttpMethod.Get, $"/matches/{Tidy(code)}/seat", token),
                ReadSeating,
                cancel);

        public Task<Reply<LobbyState>> Lobby(string code, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(HttpMethod.Get, $"/lobbies/{Tidy(code)}"),
                element => new LobbyState(
                    element.GetProperty("code").GetString() ?? string.Empty,
                    element.GetProperty("playerCount").GetInt32(),
                    Pace(element.GetProperty("pace").GetString()),
                    element.GetProperty("seated").GetInt32(),
                    element.GetProperty("started").GetBoolean(),
                    element.GetProperty("round").GetInt32()),
                cancel);

        // ---- Accounts and the pool ----------------------------------------------------

        /// <summary>
        /// Makes this device an account, and hands back the only copy of its secret.
        /// </summary>
        /// <remarks>
        /// Only ever needed to be let in among strangers. Couch play needs no account and joining by
        /// code needs none either, so nothing calls this until somebody presses the one button that
        /// pairs them with people they have not met.
        ///
        /// The band travels; the date of birth does not, and never has. It is typed once on the
        /// device, turned into one of three values there, and the value is all the relay ever sees.
        /// </remarks>
        public Task<Reply<AccountKey>> OpenAccount(
            AgeBand band, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(HttpMethod.Post, "/accounts")
                {
                    Content = Body($"{{\"band\":\"{band}\"}}"),
                },
                element => new AccountKey(
                    element.GetProperty("id").GetString() ?? string.Empty,
                    element.GetProperty("secret").GetString() ?? string.Empty),
                cancel);

        /// <summary>Tells the relay a player has had the birthday that moves their band.</summary>
        public Task<Reply<bool>> SetBand(
            AccountKey account, AgeBand band, CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request =
                        new HttpRequestMessage(HttpMethod.Put, "/accounts/band")
                        {
                            Content = Body($"{{\"band\":\"{band}\"}}"),
                        };

                    return Owned(request, account);
                },
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        /// <summary>
        /// Presents a platform's signed statement that a grown-up approved this account.
        /// </summary>
        /// <remarks>
        /// The grant comes from a store, not from here and not from the player. Nothing in this
        /// client can make one, which is the point: an approval a client could mint would be a hole
        /// in the age gate wearing the name of a safeguard.
        /// </remarks>
        public Task<Reply<bool>> Approve(
            AccountKey account, string grant, CancellationToken cancel = default) =>
            Call(
                () => Owned(
                    new HttpRequestMessage(HttpMethod.Post, "/accounts/approval")
                    {
                        Content = Body($"{{\"grant\":{Quoted(grant)}}}"),
                    },
                    account),
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        /// <summary>
        /// Asks for a code to be posted to an address, so it can be linked to this account.
        /// </summary>
        /// <remarks>
        /// Adults only, and refused by the relay rather than only by the screen that offers it: the
        /// design gives under-threshold accounts no email collection at all. The refusal arrives as
        /// TooYoung, the same outcome the pool uses, since it is the same rule.
        ///
        /// The address is escaped through the serializer rather than dropped into a string. It is
        /// typed by a person and this code has no business assuming what is in it.
        /// </remarks>
        public Task<Reply<bool>> ClaimEmail(
            AccountKey account, string address, CancellationToken cancel = default) =>
            Call(
                () => Owned(
                    new HttpRequestMessage(HttpMethod.Post, "/accounts/email")
                    {
                        Content = Body($"{{\"email\":{Quoted(address)}}}"),
                    },
                    account),
                _ => true,
                cancel,
                // The relay says 409 when the address already belongs to somebody, which is the one
                // conflict this call has.
                conflictMeans: RelayOutcome.Taken);

        /// <summary>Hands back the code that came out of the inbox.</summary>
        public Task<Reply<bool>> ProveEmail(
            AccountKey account, string code, CancellationToken cancel = default) =>
            Call(
                () => Owned(
                    new HttpRequestMessage(HttpMethod.Put, "/accounts/email")
                    {
                        Content = Body($"{{\"code\":{Quoted(code)}}}"),
                    },
                    account),
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        /// <summary>
        /// Joins the pool, and hands back the ticket to ask about it with.
        /// </summary>
        /// <remarks>
        /// Safe to call twice. A phone that sent this and lost signal before the reply gets the same
        /// ticket back rather than a second place in the queue, which is the one case where being
        /// idempotent is the difference between recovering and being stuck.
        /// </remarks>
        public Task<Reply<string>> JoinPool(
            AccountKey account,
            int playerCount,
            MatchPace pace,
            CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/queue")
                    {
                        Content = Body(
                            $"{{\"playerCount\":{playerCount.ToString(CultureInfo.InvariantCulture)},"
                            + $"\"pace\":\"{pace}\"}}"),
                    };

                    return Owned(request, account);
                },
                element => element.GetProperty("ticket").GetString() ?? string.Empty,
                cancel);

        /// <summary>Where a ticket has got to.</summary>
        public Task<Reply<Place>> Place(string ticket, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(HttpMethod.Get, $"/queue/{Uri.EscapeDataString(ticket)}"),
                element => element.GetProperty("waiting").GetBoolean()
                    ? new Place(
                        element.GetProperty("seconds").GetInt32(),
                        element.GetProperty("slow").GetBoolean(),
                        null)
                    : new Place(0, false, ReadSeating(element.GetProperty("seated"))),
                cancel);

        /// <summary>
        /// Gives up a place in the pool, or lets go of a ticket that has done its job.
        /// </summary>
        /// <remarks>
        /// The same call either way. A pool that kept finished tickets would grow for ever and would
        /// eventually try to seat somebody into a match they left a fortnight ago.
        /// </remarks>
        public Task<Reply<bool>> LeavePool(string ticket, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(
                    HttpMethod.Delete, $"/queue/{Uri.EscapeDataString(ticket)}"),
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        // ---- Rounds -----------------------------------------------------------------

        /// <summary>Hands one seat's plan over as the bytes PlanCodec produced.</summary>
        public Task<Reply<Committed>> Commit(
            string code, int round, string token, byte[] plan, CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = Signed(
                        HttpMethod.Post, $"/matches/{Tidy(code)}/rounds/{round}/plan", token);

                    ByteArrayContent body = new ByteArrayContent(plan);
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content = body;

                    return request;
                },
                element => new Committed(
                    element.GetProperty("seat").GetInt32(),
                    element.GetProperty("waitingOn").GetInt32()),
                cancel);

        /// <summary>
        /// Asks for a round, which answers with every plan in it or with how many are missing.
        /// </summary>
        public Task<Reply<RoundRelease>> Round(
            string code, string token, int round, CancellationToken cancel = default) =>
            Call(
                () => Signed(
                    HttpMethod.Get, $"/matches/{Tidy(code)}/rounds/{round}", token),
                element => ReadRound(element, round),
                cancel);

        /// <summary>
        /// Says something. An index into the wheel, and nothing else.
        /// </summary>
        /// <remarks>
        /// A refusal is expected and unremarkable: the relay limits how often one seat may speak, and
        /// a player who taps twice quickly has had the second tap dropped rather than anything having
        /// gone wrong. The caller does not need to know which.
        /// </remarks>
        public Task<Reply<bool>> Say(
            string code, string token, Emote emote, CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = Signed(
                        HttpMethod.Post, $"/matches/{Tidy(code)}/emote", token);

                    request.Content = Body(
                        $"{{\"emote\":{((int)emote).ToString(CultureInfo.InvariantCulture)}}}");

                    return request;
                },
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        /// <summary>Everything said since a given point, and where to carry on from.</summary>
        public Task<Reply<Chatter>> Listen(
            string code, long since, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/matches/{Tidy(code)}/emotes?since={since.ToString(CultureInfo.InvariantCulture)}"),
                element =>
                {
                    List<(int Seat, Emote Emote)> said = new List<(int, Emote)>();

                    foreach (JsonElement one in element.GetProperty("said").EnumerateArray())
                    {
                        said.Add((
                            one.GetProperty("seat").GetInt32(),
                            (Emote)one.GetProperty("emote").GetInt32()));
                    }

                    return new Chatter(element.GetProperty("since").GetInt64(), said);
                },
                cancel);

        /// <summary>
        /// Tells the relay where to reach this player when a round comes round to them.
        /// </summary>
        /// <remarks>
        /// Anytime pace does not work without this. A round window is a day long, so a client that
        /// only learns whose turn it is by polling would have to poll all day and drain a battery to
        /// do it, and the design allows at most one notification a day per match precisely because
        /// that one is meant to be enough.
        ///
        /// Obtaining the token is the platform's job and the part that is not done: it needs Firebase
        /// Cloud Messaging on the device, which on Android means a Godot plugin this project does not
        /// have yet. The relay side is finished and this call is what will feed it.
        /// </remarks>
        public Task<Reply<bool>> RegisterDevice(
            string code,
            string token,
            string deviceToken,
            string platform,
            CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = Signed(
                        HttpMethod.Put, $"/matches/{Tidy(code)}/device", token);

                    request.Content = Body(
                        $"{{\"token\":{Quoted(deviceToken)},\"platform\":{Quoted(platform)}}}");

                    return request;
                },
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        /// <summary>
        /// Reports what this client thought the world looked like at the end of a round.
        /// </summary>
        /// <remarks>
        /// Nothing depends on this succeeding, which is why the caller can ignore the reply. It is
        /// telemetry: if two participants report different hashes, determinism broke on real hardware
        /// and the match is its own reproduction. Losing one report costs a diagnosis, not a game.
        /// </remarks>
        public Task<Reply<bool>> ReportHash(
            string code, int round, string token, ulong hash, CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = Signed(
                        HttpMethod.Post, $"/matches/{Tidy(code)}/rounds/{round}/hash", token);

                    request.Content = Body(
                        $"{{\"hash\":\"{hash.ToString(CultureInfo.InvariantCulture)}\"}}");

                    return request;
                },
                _ => true,
                cancel,
                emptyBodyMeansSuccess: true);

        // ---- Plumbing ---------------------------------------------------------------

        /// <summary>
        /// Sends one request and turns whatever comes back into a Reply.
        /// </summary>
        /// <remarks>
        /// The status code is the whole error model, mapped once here so no caller has to know what
        /// 409 means. Anything that is not a status code at all, a socket closing or a timeout, is
        /// Unreachable rather than an exception, because on a phone that is Tuesday.
        /// </remarks>
        private async Task<Reply<T>> Call<T>(
            Func<HttpRequestMessage> build,
            Func<JsonElement, T> read,
            CancellationToken cancel,
            bool emptyBodyMeansSuccess = false,
            RelayOutcome conflictMeans = RelayOutcome.AlreadyCommitted)
        {
            HttpResponseMessage response;

            try
            {
                using HttpRequestMessage request = build();
                response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException trouble)
            {
                Trouble = trouble.Message;

                return Reply.Bad<T>(RelayOutcome.Unreachable);
            }
            catch (TaskCanceledException)
            {
                // Which covers both a timeout and the caller giving up, and the difference does not
                // change what a client should do next.
                Trouble = "The relay did not answer in time.";

                return Reply.Bad<T>(RelayOutcome.Unreachable);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Reply.Bad<T>(Failure(response.StatusCode, conflictMeans));
                }

                string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(body))
                {
                    return emptyBodyMeansSuccess
                        ? Reply.Good(read(default))
                        : Reply.Bad<T>(RelayOutcome.Refused);
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);

                    return Reply.Good(read(document.RootElement));
                }
                catch (JsonException)
                {
                    // Something answered on that address and it was not a relay.
                    return Reply.Bad<T>(RelayOutcome.Refused);
                }
                catch (KeyNotFoundException)
                {
                    return Reply.Bad<T>(RelayOutcome.Refused);
                }
            }
        }

        private static RelayOutcome Failure(HttpStatusCode status, RelayOutcome conflictMeans)
        {
            switch (status)
            {
                case HttpStatusCode.NotFound:
                    return RelayOutcome.NoSuchMatch;

                case HttpStatusCode.Unauthorized:
                    return RelayOutcome.NotYourSeat;

                // Only the pool ever says this, and only for one reason: the account behind the
                // request is not old enough to be put among strangers.
                case HttpStatusCode.Forbidden:
                    return RelayOutcome.TooYoung;

                // The relay says 409 for a full lobby, a second submission and the wrong round
                // alike, and which one it is depends entirely on what was asked. So the caller says.
                case HttpStatusCode.Conflict:
                    return conflictMeans;

                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.GatewayTimeout:
                    return RelayOutcome.Unreachable;

                default:
                    return RelayOutcome.Refused;
            }
        }

        private static Seating ReadSeating(JsonElement element) =>
            new Seating(
                element.GetProperty("code").GetString() ?? string.Empty,
                element.GetProperty("seat").GetInt32(),
                element.GetProperty("token").GetString() ?? string.Empty,
                element.GetProperty("playerCount").GetInt32(),
                Pace(element.GetProperty("pace").GetString()),
                ulong.Parse(
                    element.GetProperty("seed").GetString() ?? "0", CultureInfo.InvariantCulture),
                element.GetProperty("seated").GetInt32(),
                element.GetProperty("started").GetBoolean(),
                element.GetProperty("round").GetInt32(),
                element.TryGetProperty("windowSeconds", out JsonElement window) ? window.GetInt32() : 0,
                Deadline(element));

        private static RoundRelease ReadRound(JsonElement element, int round)
        {
            bool complete = element.GetProperty("complete").GetBoolean();
            DateTimeOffset? deadline = Deadline(element);

            if (!complete)
            {
                return RoundRelease.Waiting(
                    round, element.GetProperty("waitingOn").GetInt32(), deadline);
            }

            List<Submitted> plans = new List<Submitted>();

            foreach (JsonElement plan in element.GetProperty("plans").EnumerateArray())
            {
                plans.Add(new Submitted(
                    plan.GetProperty("seat").GetInt32(),
                    Convert.FromBase64String(
                        plan.GetProperty("payload").GetString() ?? string.Empty)));
            }

            List<int> forfeited = new List<int>();

            if (element.TryGetProperty("forfeited", out JsonElement gaveUp)
                && gaveUp.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement seat in gaveUp.EnumerateArray())
                {
                    forfeited.Add(seat.GetInt32());
                }
            }

            return new RoundRelease(round, complete: true, waitingOn: 0, plans, forfeited, deadline);
        }

        /// <summary>
        /// The deadline, if there is one. Live pace has none and sends null.
        /// </summary>
        private static DateTimeOffset? Deadline(JsonElement element) =>
            element.TryGetProperty("deadline", out JsonElement due)
                && due.ValueKind != JsonValueKind.Null
                && due.TryGetDateTimeOffset(out DateTimeOffset when)
                    ? when
                    : null;

        private static MatchPace Pace(string? name) =>
            string.Equals(name, "Anytime", StringComparison.OrdinalIgnoreCase)
                ? MatchPace.Anytime
                : MatchPace.Live;

        /// <summary>Signs a request with the account that owns it, rather than with a seat.</summary>
        private static HttpRequestMessage Owned(HttpRequestMessage request, AccountKey account)
        {
            request.Headers.Add("X-Account", account.Id);
            request.Headers.Add("X-Account-Secret", account.Secret);

            return request;
        }

        private static HttpRequestMessage Signed(HttpMethod method, string path, string token)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Seat-Token", token);

            return request;
        }

        private static StringContent Body(string json) =>
            new StringContent(json, Encoding.UTF8, "application/json");

        /// <summary>
        /// A string safely inside JSON.
        /// </summary>
        /// <remarks>
        /// The other bodies here are numbers and enum names, which cannot contain anything that needs
        /// escaping. A push token comes from a platform and is not this code's to make assumptions
        /// about, so it goes through the serializer rather than into a string.
        /// </remarks>
        private static string Quoted(string value) => JsonSerializer.Serialize(value);

        /// <summary>
        /// Strips a code down to the letters before it goes in a URL.
        /// </summary>
        /// <remarks>
        /// The relay forgives case and punctuation itself, but a raw code can arrive from a share
        /// link or a paste with a space in it, and a space in a path segment is a different kind of
        /// problem. Cheaper to clean here than to debug a 404 that is really a URL.
        /// </remarks>
        private static string Tidy(string code)
        {
            StringBuilder letters = new StringBuilder(code.Length);

            foreach (char character in code)
            {
                if (char.IsLetter(character))
                {
                    letters.Append(char.ToUpperInvariant(character));
                }
            }

            return letters.ToString();
        }

        public void Dispose()
        {
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }
    }
}
