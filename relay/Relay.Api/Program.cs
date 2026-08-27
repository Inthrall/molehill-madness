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

WebApplication app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// ---- Lobbies --------------------------------------------------------------------------

app.MapPost("/lobbies", (OpenLobby request, MatchStore store) =>
{
    if (request.PlayerCount is < 2 or > 4)
    {
        return Results.BadRequest(new { error = "A match is two to four players." });
    }

    (Match match, Seat host) = store.Open(request.PlayerCount, request.Pace, DateTimeOffset.UtcNow);

    return Results.Created($"/lobbies/{match.Code}", Joined.From(match, host, seatsTaken: 1));
});

app.MapPost("/lobbies/{code}/seats", (string code, MatchStore store) =>
{
    // The code arrives from a human ear and a human thumb, so it is tidied before it is trusted.
    if (GameCode.Parse(code) is not string tidy)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    (Seat? seat, JoinRefusal refusal) = store.Join(tidy, DateTimeOffset.UtcNow);

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
    string code, int round, HttpRequest http, MatchStore store) =>
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

    if (!store.Submit(tidy, round, seat, payload, DateTimeOffset.UtcNow))
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

app.MapGet("/matches/{code}/rounds/{round:int}", (string code, int round, MatchStore store) =>
{
    if (GameCode.Parse(code) is not string tidy || store.Find(tidy) is not Match match)
    {
        return Results.NotFound(new { error = "No such game code." });
    }

    IReadOnlyList<Submission> submissions = store.Submissions(tidy, round);

    // Nothing is handed back until every seat is in. Releasing plans early would let the last
    // player to commit see what everybody else did first, which is the one thing simultaneous
    // turns exist to prevent.
    if (submissions.Count < match.PlayerCount)
    {
        return Results.Ok(new
        {
            round,
            complete = false,
            waitingOn = match.PlayerCount - submissions.Count,
        });
    }

    store.Advance(tidy, round + 1);

    return Results.Ok(new
    {
        round,
        complete = true,
        seed = match.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        plans = submissions.Select(s => new { seat = s.Seat, payload = Convert.ToBase64String(s.Payload) }),
    });
});

// ---- Determinism reports --------------------------------------------------------------

// Every participant simulated the same round from the same inputs, so their state hashes have to
// match. Collecting them is what turns a determinism bug on a stranger's phone into a bug report
// with a perfect reproduction attached, because the seed and every plan are already stored here.
app.MapPost("/matches/{code}/rounds/{round:int}/hash", (
    string code, int round, ReportHash report, HttpRequest http, MatchStore store) =>
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
    store.ReportHash(tidy, round, seat, hash, DateTimeOffset.UtcNow);

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
