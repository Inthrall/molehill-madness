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
            int playerCount, MatchPace pace, CancellationToken cancel = default) =>
            Call(
                () =>
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/lobbies")
                    {
                        Content = Body($"{{\"playerCount\":{playerCount},\"pace\":\"{pace}\"}}"),
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
            string code, int round, CancellationToken cancel = default) =>
            Call(
                () => new HttpRequestMessage(
                    HttpMethod.Get, $"/matches/{Tidy(code)}/rounds/{round}"),
                element => ReadRound(element, round),
                cancel);

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
            catch (HttpRequestException)
            {
                return Reply.Bad<T>(RelayOutcome.Unreachable);
            }
            catch (TaskCanceledException)
            {
                // Which covers both a timeout and the caller giving up, and the difference does not
                // change what a client should do next.
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
                element.GetProperty("round").GetInt32());

        private static RoundRelease ReadRound(JsonElement element, int round)
        {
            bool complete = element.GetProperty("complete").GetBoolean();

            if (!complete)
            {
                return RoundRelease.Waiting(round, element.GetProperty("waitingOn").GetInt32());
            }

            List<byte[]> plans = new List<byte[]>();

            foreach (JsonElement plan in element.GetProperty("plans").EnumerateArray())
            {
                plans.Add(Convert.FromBase64String(
                    plan.GetProperty("payload").GetString() ?? string.Empty));
            }

            return new RoundRelease(round, complete: true, waitingOn: 0, plans);
        }

        private static MatchPace Pace(string? name) =>
            string.Equals(name, "Anytime", StringComparison.OrdinalIgnoreCase)
                ? MatchPace.Anytime
                : MatchPace.Live;

        private static HttpRequestMessage Signed(HttpMethod method, string path, string token)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Seat-Token", token);

            return request;
        }

        private static StringContent Body(string json) =>
            new StringContent(json, Encoding.UTF8, "application/json");

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
