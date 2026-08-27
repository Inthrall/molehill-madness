using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Relay.Api;

/// <summary>
/// Everything the relay remembers, in SQLite.
/// </summary>
/// <remarks>
/// SQLite because the plan says "SQLite first, Postgres when it hurts", and it will not hurt for a
/// long time: a whole four-player match is a seed and a handful of payloads, on the order of sixty
/// kilobytes, and a match is over in an afternoon or a fortnight depending on the pace.
///
/// Raw ADO rather than an object mapper. There are three tables and no queries worth the name, so a
/// mapper would be a dependency and a layer earning nothing. The connection is held open for the
/// lifetime of the store, which is also what lets the tests run the real SQL against an in-memory
/// database rather than against a substitute that could agree with a bug.
///
/// One lock around all of it, which is not laziness. The store is a singleton and the web server
/// answers requests in parallel, so two things need serialising and a lock is the honest way to get
/// both. A SqliteConnection is not thread safe, so concurrent commands on the one held here would
/// fail or worse. And several operations are a read followed by a write that depends on it: Join
/// counts the seats and then claims the next number, and two players accepting an invitation in the
/// same instant would otherwise both read the same count and race for the same seat. Throughput is
/// not a consideration at this scale, where a whole match is a seed and a handful of payloads.
///
/// The lock is taken at the public methods rather than around each command, because per-command
/// locking would make every individual statement safe while leaving exactly the read-then-write
/// races above wide open, which is the more expensive kind of wrong: it looks protected.
/// </remarks>
public sealed class MatchStore : IDisposable
{
    private readonly Lock _gate = new Lock();
    private readonly SqliteConnection _connection;

    public MatchStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Migrate();
    }

    /// <summary>
    /// A store that lives and dies with the process. For tests and for a local run.
    /// </summary>
    /// <remarks>
    /// The name matters more than it looks: an in-memory SQLite database is identified by it, so two
    /// stores sharing a name share their tables. That is what makes a local run survive a request,
    /// and it is also why tests that want isolation pass their own name.
    /// </remarks>
    public static MatchStore InMemory(string name = "molehill-relay") =>
        new MatchStore($"Data Source={name};Mode=Memory;Cache=Shared");

    private void Migrate()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS matches (
                code         TEXT PRIMARY KEY,
                player_count INTEGER NOT NULL,
                pace         INTEGER NOT NULL,
                seed         TEXT NOT NULL,
                opened_at    TEXT NOT NULL,
                round        INTEGER NOT NULL,
                started      INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS seats (
                code      TEXT NOT NULL,
                number    INTEGER NOT NULL,
                token     TEXT NOT NULL,
                joined_at TEXT NOT NULL,
                PRIMARY KEY (code, number)
            );

            CREATE TABLE IF NOT EXISTS submissions (
                code    TEXT NOT NULL,
                round   INTEGER NOT NULL,
                seat    INTEGER NOT NULL,
                payload BLOB NOT NULL,
                at      TEXT NOT NULL,
                PRIMARY KEY (code, round, seat)
            );

            CREATE TABLE IF NOT EXISTS hashes (
                code  TEXT NOT NULL,
                round INTEGER NOT NULL,
                seat  INTEGER NOT NULL,
                hash  TEXT NOT NULL,
                at    TEXT NOT NULL,
                PRIMARY KEY (code, round, seat)
            );
            """);
    }

    // ---- Matches --------------------------------------------------------------------

    /// <summary>
    /// Opens a lobby and seats the host in it.
    /// </summary>
    /// <remarks>
    /// The relay draws the seed, because every client has to agree on the ground they are fighting
    /// over and the relay is the only thing they all talk to. That is not simulating: it is one
    /// number, and what it grows into is each client's own business.
    ///
    /// Codes are retried on collision rather than checked first, because the insert is the only
    /// check that cannot race another host opening a lobby at the same moment.
    /// </remarks>
    public (Match Match, Seat Host) Open(int playerCount, Pace pace, DateTimeOffset now)
    {
        if (playerCount is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerCount), "A match is two to four players.");
        }

        lock (_gate)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                string code = GameCode.Draw();
                ulong seed = NextSeed();

                using SqliteCommand insert = _connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO matches (code, player_count, pace, seed, opened_at, round, started)
                    VALUES ($code, $count, $pace, $seed, $opened, 1, 0);
                    """;
                insert.Parameters.AddWithValue("$code", code);
                insert.Parameters.AddWithValue("$count", playerCount);
                insert.Parameters.AddWithValue("$pace", (int)pace);
                insert.Parameters.AddWithValue("$seed", seed.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$opened", Stamp(now));

                if (insert.ExecuteNonQuery() == 0)
                {
                    continue;
                }

                Seat host = Claim(code, 0, now);

                // Not started: a lobby with only its host in it is a lobby. Filling the last seat is
                // what starts a match, and Join is where that happens.
                return (
                    new Match(code, playerCount, pace, seed, now, Round: 1, Started: false),
                    host);
            }
        }

        throw new InvalidOperationException("Could not find a free game code.");
    }

    public Match? Find(string code)
    {
        lock (_gate)
        {
            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT player_count, pace, seed, opened_at, round, started
                FROM matches WHERE code = $code;
                """;
            read.Parameters.AddWithValue("$code", code);

            using SqliteDataReader row = read.ExecuteReader();

            if (!row.Read())
            {
                return null;
            }

            return new Match(
                code,
                row.GetInt32(0),
                (Pace)row.GetInt32(1),
                ulong.Parse(row.GetString(2), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(row.GetString(3), CultureInfo.InvariantCulture),
                row.GetInt32(4),
                row.GetInt32(5) != 0);
        }
    }

    /// <summary>
    /// Seats a joiner, or reports why not.
    /// </summary>
    /// <remarks>
    /// Seat numbers are handed out in join order and never reused, which is what makes them safe to
    /// use as the platoon index every client agrees on. Counting the seats and claiming the next one
    /// happen under the same lock for that reason: two joiners arriving together must not both be
    /// told they are seat one.
    /// </remarks>
    public (Seat? Seat, JoinRefusal Refusal) Join(string code, DateTimeOffset now)
    {
        lock (_gate)
        {
            Match? match = Find(code);

            if (match is null)
            {
                return (null, JoinRefusal.NoSuchMatch);
            }

            int taken = SeatsTaken(code);

            if (taken >= match.PlayerCount)
            {
                return (null, JoinRefusal.Full);
            }

            Seat seat = Claim(code, taken, now);

            if (taken + 1 == match.PlayerCount)
            {
                Execute("UPDATE matches SET started = 1 WHERE code = $code;", ("$code", code));
            }

            return (seat, JoinRefusal.None);
        }
    }

    public int SeatsTaken(string code)
    {
        lock (_gate)
        {
            using SqliteCommand count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM seats WHERE code = $code;";
            count.Parameters.AddWithValue("$code", code);

            return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Which seat a token owns, or null if it owns none in this match.</summary>
    public int? SeatOf(string code, string token)
    {
        lock (_gate)
        {
            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText = "SELECT number FROM seats WHERE code = $code AND token = $token;";
            read.Parameters.AddWithValue("$code", code);
            read.Parameters.AddWithValue("$token", token);

            object? found = read.ExecuteScalar();

            return found is null ? null : Convert.ToInt32(found, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Writes a seat row and mints the token that owns it. Callers hold the lock.</summary>
    private Seat Claim(string code, int number, DateTimeOffset now)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        Execute(
            """
            INSERT INTO seats (code, number, token, joined_at)
            VALUES ($code, $number, $token, $joined);
            """,
            ("$code", code), ("$number", number), ("$token", token), ("$joined", Stamp(now)));

        return new Seat(code, number, token, now);
    }

    // ---- Rounds ---------------------------------------------------------------------

    /// <summary>
    /// Stores one seat's payload for one round, exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// First submission wins. A seat that sends twice has either double-tapped commit or is trying
    /// to see what everybody else did and then change its mind, and simultaneous turns are the whole
    /// game, so the second one is refused rather than merged.
    /// </remarks>
    public bool Submit(string code, int round, int seat, byte[] payload, DateTimeOffset now)
    {
        lock (_gate)
        {
            using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText =
                """
                INSERT OR IGNORE INTO submissions (code, round, seat, payload, at)
                VALUES ($code, $round, $seat, $payload, $at);
                """;
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$round", round);
            insert.Parameters.AddWithValue("$seat", seat);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$at", Stamp(now));

            return insert.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Everything submitted for a round so far, in seat order.</summary>
    public IReadOnlyList<Submission> Submissions(string code, int round)
    {
        lock (_gate)
        {
            List<Submission> found = new List<Submission>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT seat, payload, at FROM submissions
                WHERE code = $code AND round = $round ORDER BY seat;
                """;
            read.Parameters.AddWithValue("$code", code);
            read.Parameters.AddWithValue("$round", round);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(new Submission(
                    code,
                    round,
                    row.GetInt32(0),
                    (byte[])row["payload"],
                    DateTimeOffset.Parse(row.GetString(2), CultureInfo.InvariantCulture)));
            }

            return found;
        }
    }

    // ---- Determinism reports --------------------------------------------------------

    /// <summary>
    /// Records what one client thought the world looked like at the end of a round.
    /// </summary>
    /// <remarks>
    /// This is the field telemetry the plan asks for, and it is the one thing the relay stores that
    /// is not needed to play the game. Every participant simulates the same round from the same
    /// inputs, so their state hashes must match; if they do not, that is a determinism bug on real
    /// hardware, and the match is its own reproduction because the seed and every plan are already
    /// sitting in the other two tables.
    ///
    /// Per round rather than per match, which the plan describes as the cheap version. A hash is
    /// eight bytes, so recording one each round costs nothing and narrows a divergence to the round
    /// it started in rather than to a whole afternoon, which is the difference between a bisector
    /// having a lead and having a haystack.
    ///
    /// The relay records and does not arbitrate. It has no idea which client is right and cannot
    /// acquire one without simulating, so a disagreement is reported rather than resolved.
    /// </remarks>
    public bool ReportHash(string code, int round, int seat, ulong hash, DateTimeOffset now)
    {
        lock (_gate)
        {
            using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText =
                """
                INSERT OR IGNORE INTO hashes (code, round, seat, hash, at)
                VALUES ($code, $round, $seat, $hash, $at);
                """;
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$round", round);
            insert.Parameters.AddWithValue("$seat", seat);
            insert.Parameters.AddWithValue("$hash", hash.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$at", Stamp(now));

            return insert.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Every hash reported for a match, oldest round first.</summary>
    public IReadOnlyList<ReportedHash> Hashes(string code)
    {
        lock (_gate)
        {
            List<ReportedHash> found = new List<ReportedHash>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT round, seat, hash FROM hashes
                WHERE code = $code ORDER BY round, seat;
                """;
            read.Parameters.AddWithValue("$code", code);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(new ReportedHash(
                    row.GetInt32(0),
                    row.GetInt32(1),
                    ulong.Parse(row.GetString(2), CultureInfo.InvariantCulture)));
            }

            return found;
        }
    }

    /// <summary>Moves a match on to the next round, once everybody's plan is in.</summary>
    /// <remarks>
    /// Every client calls this when it sees a resolved round, so it has to be safe to call twice and
    /// it must never walk a match backwards. The round guard in the WHERE clause is what does that.
    /// </remarks>
    public void Advance(string code, int toRound)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE matches SET round = $round WHERE code = $code AND round < $round;",
                ("$code", code), ("$round", toRound));
        }
    }

    // ---- Plumbing -------------------------------------------------------------------

    private static ulong NextSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        return BitConverter.ToUInt64(bytes);
    }

    /// <summary>
    /// Round trip safe and sortable, which matters because forfeits will read these back.
    /// </summary>
    private static string Stamp(DateTimeOffset when) =>
        when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Why a join did not happen.</summary>
public enum JoinRefusal
{
    None = 0,
    NoSuchMatch = 1,
    Full = 2,
}
