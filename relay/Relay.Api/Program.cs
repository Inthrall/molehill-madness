using System.Globalization;
using System.Text.Json.Serialization;
using Relay.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Pace travels as "Live" or "Anytime" rather than 0 or 1. The client is the game and the game reads
// its own wire format, so a number that has to be looked up in an enum somewhere else is a trap that
// only shows up when somebody adds a third pace in the middle of the list.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// One store for the process, holding one connection open. See MatchStore for why.
builder.Services.AddSingleton(_ =>
    builder.Configuration["Relay:Database"] is string path && path.Length > 0
        ? new MatchStore($"Data Source={path}")
        : MatchStore.InMemory());

// The clock, from the container rather than read straight off the wall. Every deadline in the game
// hangs off it, and a test that had to wait a real minute to see a forfeit would not get written.
builder.Services.AddSingleton(TimeProvider.System);

// The forfeit sweep. One player who loses interest must not be able to end a match for three other
// people by never opening the game again, and the client that would notice is the one that is waiting.
builder.Services.AddHostedService<ForfeitSweeper>();

// Notifications are decided when a round resolves and drained separately. Which sender drains them
// depends on whether the relay has been given a Firebase service account: with one it sends, without
// one it writes them down. A development run wants the log, since the question there is whether the
// right people are told at the right times and a log line answers it exactly as well as a phone
// buzzing would. A deployment that meant to have a key and does not is a different matter, so a key
// that is configured and unusable stops the process rather than quietly falling back to the log.
if (Firebase.Configured(builder.Configuration) is ServiceAccount account)
{
    builder.Services.AddSingleton(account);

    // One client for the process, over a handler that lets its connections go every so often, which
    // is the part of IHttpClientFactory that matters to a service with one outbound host. The
    // container owns it and disposes it at shutdown.
    builder.Services.AddSingleton(_ => new HttpClient(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));

    builder.Services.AddSingleton<INudgeSender, FirebaseNudgeSender>();
}
else
{
    builder.Services.AddSingleton<INudgeSender, LoggingNudgeSender>();
}

builder.Services.AddHostedService<NudgeDrain>();

WebApplication app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// ---- Lobbies --------------------------------------------------------------------------

app.MapPost("/lobbies", (OpenLobby request, MatchStore store, TimeProvider clock) =>
{
    if (request.PlayerCount is < 2 or > 4)
    {
        return Results.BadRequest(new { error = "A match is two to four players." });
    }

    (Match match, Seat host) = store.Open(
        request.PlayerCount, request.Pace, clock.GetUtcNow(), request.WindowSeconds);

    return Results.Created($"/lobbies/{match.Code}", Joined.From(match, host, seatsTaken: 1));
});

app.MapPost("/lobbies/{code}/seats", (string code, MatchStore store, TimeProvider clock) =>
{
    // The code arrives from a human ear and a human thumb, so it is tidied before it is trusted.
    if (GameCode.Parse(code) is not string tidy)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    (Seat? seat, JoinRefusal refusal) = store.Join(tidy, clock.GetUtcNow());

    if (refusal == JoinRefusal.NoSuchMatch)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (refusal == JoinRefusal.Full || seat is null)
    {
        return Results.Conflict(new { error = "That lobby is full." });
    }

    Match match = store.Find(tidy)!;

    return Results.Ok(Joined.From(match, seat, store.SeatsTaken(tidy)));
});

app.MapGet("/lobbies/{code}", (string code, MatchStore store) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    return Results.Ok(new
    {
        code = match.Code,
        playerCount = match.PlayerCount,
        pace = match.Pace.ToString(),
        seated = store.SeatsTaken(tidy),
        started = match.Started,
        round = match.Round,
        windowSeconds = match.WindowSeconds,
        deadline = match.Deadline,
    });
});

// A player coming back to a match they are already in, which is not the same as joining one.
// Joining would hand them a second seat or refuse them as full, and neither is what somebody
// reopening the game after their phone put it to sleep wants. They kept their token; this gives
// them back the seat it owns and the seed the world grows from.
app.MapGet("/matches/{code}/seat", (string code, HttpRequest http, MatchStore store) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (store.SeatOf(tidy, http.Headers["X-Seat-Token"].ToString()) is not int number)
    {
        return Results.Unauthorized();
    }

    string token = http.Headers["X-Seat-Token"].ToString();

    return Results.Ok(Joined.From(
        match, new Seat(tidy, number, token, match.OpenedAt), store.SeatsTaken(tidy)));
});

// ---- Rounds ---------------------------------------------------------------------------

// The payload is read as bytes and stored as bytes. The relay never parses a plan, never validates
// one and never simulates. Every client's own simulation decides whether a plan was legal, which is
// what keeps a second implementation of the rules from existing in here.
app.MapPost("/matches/{code}/rounds/{round:int}/plan", async (
    string code, int round, HttpRequest http, MatchStore store, TimeProvider clock) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (http.Headers["X-Seat-Token"].ToString() is not string token
        || store.SeatOf(tidy, token) is not int seat)
    {
        return Results.Unauthorized();
    }

    if (round != match.Round)
    {
        return Results.Conflict(new { error = $"That match is on round {match.Round}.", round = match.Round });
    }

    using MemoryStream buffer = new MemoryStream();
    await http.Body.CopyToAsync(buffer);
    byte[] payload = buffer.ToArray();

    if (payload.Length == 0)
    {
        return Results.BadRequest(new { error = "A plan cannot be empty." });
    }

    if (payload.Length > Limits.LargestPlan)
    {
        return Results.BadRequest(new { error = "That plan is too large to be one." });
    }

    if (!store.Submit(tidy, round, seat, payload, clock.GetUtcNow()))
    {
        // Simultaneous turns are the whole game, so a second plan for the same round is refused
        // rather than replacing the first.
        return Results.Conflict(new { error = "That seat has already committed this round." });
    }

    return Results.Accepted($"/matches/{tidy}/rounds/{round}", new
    {
        seat,
        waitingOn = match.PlayerCount - store.Submissions(tidy, round).Count,
    });
});

app.MapGet("/matches/{code}/rounds/{round:int}", (
    string code, int round, MatchStore store, TimeProvider clock) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    DateTimeOffset now = clock.GetUtcNow();

    // Swept here as well as on the timer, so a reader arriving the moment a window closes gets the
    // resolved round rather than being told to wait up to another half a minute for the sweeper.
    // The forfeit insert is idempotent, so doing it in both places is free.
    if (round == match.Round)
    {
        Forfeits.Sweep(store, tidy, now);
    }

    IReadOnlyList<Submission> submissions = store.Submissions(tidy, round);
    IReadOnlyList<int> forfeited = store.Forfeited(tidy, round);

    // Nothing is handed back until every seat has answered. Releasing plans early would let the
    // last player to commit see what everybody else did first, which is the one thing simultaneous
    // turns exist to prevent. Forfeiting counts as answering: otherwise one player losing interest
    // would end the match for the other three by never opening the game again.
    if (!Forfeits.Settled(match.PlayerCount, submissions.Count, forfeited.Count))
    {
        return Results.Ok(new
        {
            round,
            complete = false,
            waitingOn = match.PlayerCount - submissions.Count - forfeited.Count,
            deadline = match.Deadline,
        });
    }

    bool moved = match.Round == round;

    store.Advance(tidy, round + 1, now);

    // Told once, by whoever reads the resolved round first, and only about a round that was actually
    // current: a client re-reading an old round for a replay must not wake three phones up.
    if (moved)
    {
        Nudges.Decide(store, tidy, round + 1, now);
    }

    return Results.Ok(new
    {
        round,
        complete = true,
        seed = match.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        plans = submissions.Select(s => new { seat = s.Seat, payload = Convert.ToBase64String(s.Payload) }),
        // The seats that did nothing. Clients feed the simulation nothing for these, which is not
        // the same as feeding it an empty plan and is why the relay never has to build one.
        forfeited,
    });
});

// ---- Saying something -----------------------------------------------------------------

// The only communication channel in the game. What travels is an index into a fixed wheel, which is
// the whole of the design's safety argument: there is no string here for anybody to put a phone
// number in, and the set of things that can be said is decided at build time.
app.MapPost("/matches/{code}/emote", (
    string code, SendEmote request, HttpRequest http, MatchStore store, TimeProvider clock) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match _)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (store.SeatOf(tidy, http.Headers["X-Seat-Token"].ToString()) is not int seat)
    {
        return Results.Unauthorized();
    }

    if (!EmoteRate.OnIt(request.Emote))
    {
        return Results.BadRequest(new { error = "That is not on the wheel." });
    }

    // Refused rather than queued. A rate limit that delayed emotes would deliver the whole burst a
    // few seconds later, which is the same harassment arriving on a timer.
    if (!store.Emote(tidy, seat, request.Emote, clock.GetUtcNow(), EmoteRate.Gap))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    return Results.NoContent();
});

app.MapGet("/matches/{code}/emotes", (string code, long since, MatchStore store) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match _)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    IReadOnlyList<Emoted> said = store.Emotes(tidy, since);

    return Results.Ok(new
    {
        // Where to carry on from. Sent back rather than left to the client to work out, so an empty
        // reply does not reset a cursor and replay the whole conversation.
        since = said.Count > 0 ? said[^1].Id : since,
        said = said.Select(one => new { seat = one.Seat, emote = one.Emote }),
    });
});

// ---- Being told it is your turn -------------------------------------------------------

// Where to reach this player when a round comes round to them. One device per seat, latest wins,
// because a player who reinstalls has a new push token and the old one is dead.
app.MapPut("/matches/{code}/device", (
    string code, RegisterDevice request, HttpRequest http, MatchStore store, TimeProvider clock) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match _)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (store.SeatOf(tidy, http.Headers["X-Seat-Token"].ToString()) is not int seat)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > Limits.LongestDeviceToken)
    {
        return Results.BadRequest(new { error = "That is not a device token." });
    }

    store.RegisterDevice(tidy, seat, request.Token, request.Platform ?? "unknown", clock.GetUtcNow());

    return Results.NoContent();
});

// ---- Determinism reports --------------------------------------------------------------

// Every participant simulated the same round from the same inputs, so their state hashes have to
// match. Collecting them is what turns a determinism bug on a stranger's phone into a bug report
// with a perfect reproduction attached, because the seed and every plan are already stored here.
app.MapPost("/matches/{code}/rounds/{round:int}/hash", (
    string code, int round, ReportHash report, HttpRequest http, MatchStore store,
    TimeProvider clock) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match _)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    if (store.SeatOf(tidy, http.Headers["X-Seat-Token"].ToString()) is not int seat)
    {
        return Results.Unauthorized();
    }

    if (!ulong.TryParse(report.Hash, NumberStyles.None, CultureInfo.InvariantCulture, out ulong hash))
    {
        return Results.BadRequest(new { error = "A hash is an unsigned 64-bit number as a string." });
    }

    // First report for a seat and round stands. A client that reports twice has restarted or
    // retried, and quietly replacing the first answer would erase exactly the disagreement this
    // exists to catch.
    store.ReportHash(tidy, round, seat, hash, clock.GetUtcNow());

    return Results.Accepted();
});

app.MapGet("/matches/{code}/hashes", (string code, MatchStore store) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    IReadOnlyList<RoundAgreement> rounds = Agreement.Of(store.Hashes(tidy));

    return Results.Ok(new
    {
        code = tidy,
        playerCount = match.PlayerCount,
        // The headline: did anybody's simulation disagree with anybody else's, ever.
        diverged = rounds.Any(round => !round.Agreed),
        rounds,
    });
});

app.Run();
