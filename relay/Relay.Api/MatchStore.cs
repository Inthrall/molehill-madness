using System.Buffers.Text;
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
                code           TEXT PRIMARY KEY,
                player_count   INTEGER NOT NULL,
                pace           INTEGER NOT NULL,
                seed           TEXT NOT NULL,
                opened_at      TEXT NOT NULL,
                round          INTEGER NOT NULL,
                started        INTEGER NOT NULL,
                window_seconds INTEGER NOT NULL DEFAULT 0,
                round_opened_at TEXT NOT NULL DEFAULT ''
            );

            -- A seat that ran out of window. Recorded as a fact about the match rather than as a
            -- plan, because the relay does not know what a plan looks like and must not learn: a
            -- forfeit is something it can see for itself, since it knows the deadline and who has
            -- submitted. Clients feed nothing to the simulation for a forfeited seat, which is
            -- exactly the platoon doing nothing that round.
            CREATE TABLE IF NOT EXISTS forfeits (
                code  TEXT NOT NULL,
                round INTEGER NOT NULL,
                seat  INTEGER NOT NULL,
                at    TEXT NOT NULL,
                PRIMARY KEY (code, round, seat)
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

            -- As little of a player as the game can get away with knowing: an opaque id, the
            -- secret that owns it, and which side of the age threshold they said they were on. No
            -- name, no email, no handle, nothing anybody could be found by. The design asks for "no
            -- discoverable social graph", and the cheapest way to have none is to store nothing one
            -- could be built out of.
            CREATE TABLE IF NOT EXISTS accounts (
                id         TEXT PRIMARY KEY,
                secret     TEXT NOT NULL,
                band       INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                seen_at    TEXT NOT NULL
            );

            -- The matchmaking pool. One row per waiting player, and the same row carries the seat
            -- once the pool has found them one: a client holds a ticket from pressing the button to
            -- standing in the lobby and never has to swap it for anything else.
            --
            -- The account is unique rather than the primary key, so somebody who presses the button
            -- twice is refused the second one rather than joining the pool twice and being seated
            -- opposite themselves.
            CREATE TABLE IF NOT EXISTS queue (
                ticket       TEXT PRIMARY KEY,
                account      TEXT NOT NULL UNIQUE,
                player_count INTEGER NOT NULL,
                pace         INTEGER NOT NULL,
                joined_at    TEXT NOT NULL,
                code         TEXT NULL,
                seat         INTEGER NOT NULL DEFAULT -1,
                seat_token   TEXT NULL
            );

            -- One device per seat, latest wins: a player who reinstalls gets a new push token and
            -- the old one is dead, so keeping both would mean sending every notification twice and
            -- half of them nowhere.
            CREATE TABLE IF NOT EXISTS devices (
                code     TEXT NOT NULL,
                seat     INTEGER NOT NULL,
                token    TEXT NOT NULL,
                platform TEXT NOT NULL,
                at       TEXT NOT NULL,
                PRIMARY KEY (code, seat)
            );

            -- Notifications decided but not necessarily delivered. An outbox rather than a call,
            -- because the decision has a rule in it worth being certain about and the delivery is
            -- somebody else's service: this way an undeliverable nudge is a row instead of a lost
            -- event, and the "one a day per match" rule is enforced by reading this table.
            CREATE TABLE IF NOT EXISTS nudges (
                code   TEXT NOT NULL,
                seat   INTEGER NOT NULL,
                round  INTEGER NOT NULL,
                device TEXT NOT NULL,
                at     TEXT NOT NULL,
                sent   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (code, seat, round)
            );

            -- The only communication channel in the game. An autoincrementing id rather than a
            -- timestamp for the cursor, because two emotes in the same millisecond are entirely
            -- possible and a client that polled "everything after this time" would lose one.
            CREATE TABLE IF NOT EXISTS emotes (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                code  TEXT NOT NULL,
                seat  INTEGER NOT NULL,
                emote INTEGER NOT NULL,
                at    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS emotes_by_match ON emotes (code, id);

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
    public (Match Match, Seat Host) Open(
        int playerCount, Pace pace, DateTimeOffset now, int windowSeconds = 0)
    {
        if (playerCount is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerCount), "A match is two to four players.");
        }

        int window = RoundWindow.Sane(pace, windowSeconds);

        lock (_gate)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                string code = GameCode.Draw();
                ulong seed = NextSeed();

                using SqliteCommand insert = _connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO matches
                        (code, player_count, pace, seed, opened_at, round, started,
                         window_seconds, round_opened_at)
                    VALUES ($code, $count, $pace, $seed, $opened, 1, 0, $window, $opened);
                    """;
                insert.Parameters.AddWithValue("$code", code);
                insert.Parameters.AddWithValue("$count", playerCount);
                insert.Parameters.AddWithValue("$pace", (int)pace);
                insert.Parameters.AddWithValue("$seed", seed.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$opened", Stamp(now));
                insert.Parameters.AddWithValue("$window", window);

                if (insert.ExecuteNonQuery() == 0)
                {
                    continue;
                }

                Seat host = Claim(code, 0, now);

                // Not started: a lobby with only its host in it is a lobby. Filling the last seat is
                // what starts a match, and Join is where that happens.
                return (
                    new Match(
                        code, playerCount, pace, seed, now, Round: 1, Started: false,
                        WindowSeconds: window, RoundOpenedAt: now),
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
                SELECT player_count, pace, seed, opened_at, round, started,
                       window_seconds, round_opened_at
                FROM matches WHERE code = $code;
                """;
            read.Parameters.AddWithValue("$code", code);

            using SqliteDataReader row = read.ExecuteReader();

            if (!row.Read())
            {
                return null;
            }

            DateTimeOffset opened = DateTimeOffset.Parse(
                row.GetString(3), CultureInfo.InvariantCulture);

            // Falls back to when the match opened, which covers rows written before there were
            // windows at all: an empty stamp would otherwise parse as year one and every such match
            // would look expired the moment a sweep noticed it.
            string roundOpened = row.GetString(7);

            return new Match(
                code,
                row.GetInt32(0),
                (Pace)row.GetInt32(1),
                ulong.Parse(row.GetString(2), CultureInfo.InvariantCulture),
                opened,
                row.GetInt32(4),
                row.GetInt32(5) != 0,
                row.GetInt32(6),
                roundOpened.Length == 0
                    ? opened
                    : DateTimeOffset.Parse(roundOpened, CultureInfo.InvariantCulture));
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
                // Round one's window starts when the lobby fills, not when it opened. Otherwise a
                // host who set a one-hour window and then spent an hour finding a fourth player
                // would have the first round forfeit itself the instant it began.
                Execute(
                    "UPDATE matches SET started = 1, round_opened_at = $now WHERE code = $code;",
                    ("$code", code), ("$now", Stamp(now)));
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

    // ---- Emotes ---------------------------------------------------------------------

    /// <summary>
    /// Says something, unless this seat said something too recently.
    /// </summary>
    /// <remarks>
    /// The check and the insert happen under the one lock, which is the point of taking it at the
    /// public method: two taps arriving together must not both find an empty recent history and both
    /// get through, because that is precisely the burst the limit exists to stop.
    /// </remarks>
    public bool Emote(string code, int seat, int emote, DateTimeOffset now, TimeSpan gap)
    {
        lock (_gate)
        {
            using SqliteCommand recent = _connection.CreateCommand();
            recent.CommandText =
                "SELECT MAX(at) FROM emotes WHERE code = $code AND seat = $seat;";
            recent.Parameters.AddWithValue("$code", code);
            recent.Parameters.AddWithValue("$seat", seat);

            if (recent.ExecuteScalar() is string last && last.Length > 0
                && now - DateTimeOffset.Parse(last, CultureInfo.InvariantCulture) < gap)
            {
                return false;
            }

            Execute(
                """
                INSERT INTO emotes (code, seat, emote, at)
                VALUES ($code, $seat, $emote, $at);
                """,
                ("$code", code), ("$seat", seat), ("$emote", emote), ("$at", Stamp(now)));

            return true;
        }
    }

    /// <summary>Everything said in a match after a given id, oldest first.</summary>
    public IReadOnlyList<Emoted> Emotes(string code, long after, int most = 32)
    {
        lock (_gate)
        {
            List<Emoted> found = new List<Emoted>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT id, seat, emote, at FROM emotes
                WHERE code = $code AND id > $after ORDER BY id LIMIT $most;
                """;
            read.Parameters.AddWithValue("$code", code);
            read.Parameters.AddWithValue("$after", after);
            read.Parameters.AddWithValue("$most", most);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(new Emoted(
                    row.GetInt64(0),
                    code,
                    row.GetInt32(1),
                    row.GetInt32(2),
                    DateTimeOffset.Parse(row.GetString(3), CultureInfo.InvariantCulture)));
            }

            return found;
        }
    }

    // ---- Accounts ---------------------------------------------------------------------

    /// <summary>
    /// Makes an anonymous account and hands back the secret that owns it, once.
    /// </summary>
    /// <remarks>
    /// The secret is returned here and nowhere else, ever. A client that loses it has lost the
    /// account, which is the price of an account with nothing in it to recover it by: there is no
    /// email to send a link to and, for an under-threshold account, the design says there must not
    /// be one. Losing one costs a player nothing they can name, since an account holds no progress,
    /// no purchases and no friends. It is a way to be let into the stranger pool and little else.
    /// </remarks>
    public (Account Account, string Secret) OpenAccount(AgeBand band, DateTimeOffset now)
    {
        // Base64Url rather than base64, because both of these end up in places plain base64 breaks:
        // an id goes in a Location header and a URL path, and ordinary base64 contains slashes and
        // plus signs. A ticket found that out the hard way, so neither of these gets to.
        string id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
        string secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

        lock (_gate)
        {
            Execute(
                """
                INSERT INTO accounts (id, secret, band, created_at, seen_at)
                VALUES ($id, $secret, $band, $now, $now);
                """,
                ("$id", id), ("$secret", secret), ("$band", (int)band), ("$now", Stamp(now)));
        }

        return (new Account(id, band, now, now), secret);
    }

    /// <summary>
    /// The account that secret owns, or null. Touches it on the way past, since asking is being seen.
    /// </summary>
    public Account? Who(string id, string secret, DateTimeOffset now)
    {
        lock (_gate)
        {
            AgeBand band;
            DateTimeOffset created;

            using (SqliteCommand read = _connection.CreateCommand())
            {
                read.CommandText =
                    "SELECT band, created_at FROM accounts WHERE id = $id AND secret = $secret;";
                read.Parameters.AddWithValue("$id", id);
                read.Parameters.AddWithValue("$secret", secret);

                using SqliteDataReader row = read.ExecuteReader();

                if (!row.Read())
                {
                    return null;
                }

                band = (AgeBand)row.GetInt32(0);
                created = When(row.GetString(1));
            }

            Execute(
                "UPDATE accounts SET seen_at = $now WHERE id = $id;",
                ("$id", id), ("$now", Stamp(now)));

            return new Account(id, band, created, now);
        }
    }

    /// <summary>
    /// Changes an account's band, for a player the gate has asked again.
    /// </summary>
    /// <remarks>
    /// It has to be changeable, because a child becomes an adult while the account carries on
    /// existing. The client re-asks on the birthday the band was worked out from and sends the new
    /// answer here. Nothing about that is a loophole: the only thing a band buys is the stranger
    /// pool, and the only direction that matters is the one time was going to grant anyway.
    /// </remarks>
    public bool SetBand(string id, string secret, AgeBand band, DateTimeOffset now)
    {
        lock (_gate)
        {
            using SqliteCommand update = _connection.CreateCommand();
            update.CommandText =
                """
                UPDATE accounts SET band = $band, seen_at = $now
                WHERE id = $id AND secret = $secret;
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$secret", secret);
            update.Parameters.AddWithValue("$band", (int)band);
            update.Parameters.AddWithValue("$now", Stamp(now));

            return update.ExecuteNonQuery() > 0;
        }
    }

    // ---- The pool ---------------------------------------------------------------------

    /// <summary>
    /// Puts an account in the pool, or hands back the ticket it already has.
    /// </summary>
    /// <remarks>
    /// Idempotent rather than refusing, because the case it protects against is not somebody being
    /// clever: it is a phone that sent the request, lost signal before the reply arrived, and asked
    /// again. Refusing that would leave a player holding no ticket while the pool holds their place,
    /// which is the one state neither end can get out of.
    /// </remarks>
    public Ticket JoinQueue(string account, int playerCount, Pace pace, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Waiting(account) is Ticket already)
            {
                return already;
            }

            // URL-safe, because a ticket is asked about at /queue/{ticket} and a plain base64 one
            // contains slashes. A slash in the middle of it does not fail the request, it fails to
            // match the route at all, and an empty 404 is a confusing way to learn that.
            string ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));

            Execute(
                """
                INSERT INTO queue (ticket, account, player_count, pace, joined_at)
                VALUES ($ticket, $account, $count, $pace, $now);
                """,
                ("$ticket", ticket), ("$account", account), ("$count", playerCount),
                ("$pace", (int)pace), ("$now", Stamp(now)));

            return new Ticket(ticket, account, playerCount, pace, now, null, -1, null);
        }
    }

    /// <summary>Everybody in the pool, seated or not, oldest first.</summary>
    public IReadOnlyList<Ticket> Queue()
    {
        lock (_gate)
        {
            List<Ticket> found = new List<Ticket>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT ticket, account, player_count, pace, joined_at, code, seat, seat_token
                FROM queue ORDER BY joined_at, ticket;
                """;

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(ReadTicket(row));
            }

            return found;
        }
    }

    /// <summary>One ticket, by the id its owner is holding.</summary>
    public Ticket? Held(string ticket)
    {
        lock (_gate)
        {
            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT ticket, account, player_count, pace, joined_at, code, seat, seat_token
                FROM queue WHERE ticket = $ticket;
                """;
            read.Parameters.AddWithValue("$ticket", ticket);

            using SqliteDataReader row = read.ExecuteReader();

            return row.Read() ? ReadTicket(row) : null;
        }
    }

    /// <summary>
    /// Records the seat the pool found for a ticket.
    /// </summary>
    /// <remarks>
    /// Guarded on the ticket not having one already, so a pass of the pool that overlapped another
    /// cannot move somebody out of the lobby they were already put into.
    /// </remarks>
    public void Seated(string ticket, string code, int seat, string token)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE queue SET code = $code, seat = $seat, seat_token = $token
                WHERE ticket = $ticket AND code IS NULL;
                """,
                ("$ticket", ticket), ("$code", code), ("$seat", seat), ("$token", token));
        }
    }

    /// <summary>Takes a ticket out of the pool, whether it was seated or gave up.</summary>
    public bool LeaveQueue(string ticket)
    {
        lock (_gate)
        {
            using SqliteCommand delete = _connection.CreateCommand();
            delete.CommandText = "DELETE FROM queue WHERE ticket = $ticket;";
            delete.Parameters.AddWithValue("$ticket", ticket);

            return delete.ExecuteNonQuery() > 0;
        }
    }

    private Ticket? Waiting(string account)
    {
        using SqliteCommand read = _connection.CreateCommand();
        read.CommandText =
            """
            SELECT ticket, account, player_count, pace, joined_at, code, seat, seat_token
            FROM queue WHERE account = $account;
            """;
        read.Parameters.AddWithValue("$account", account);

        using SqliteDataReader row = read.ExecuteReader();

        return row.Read() ? ReadTicket(row) : null;
    }

    private static Ticket ReadTicket(SqliteDataReader row) =>
        new Ticket(
            row.GetString(0),
            row.GetString(1),
            row.GetInt32(2),
            (Pace)row.GetInt32(3),
            When(row.GetString(4)),
            row.IsDBNull(5) ? null : row.GetString(5),
            row.GetInt32(6),
            row.IsDBNull(7) ? null : row.GetString(7));

    // ---- Devices and nudges ---------------------------------------------------------

    /// <summary>Remembers where to reach one seat's player. Replaces whatever was there.</summary>
    public void RegisterDevice(string code, int seat, string token, string platform, DateTimeOffset now)
    {
        lock (_gate)
        {
            Execute(
                """
                INSERT INTO devices (code, seat, token, platform, at)
                VALUES ($code, $seat, $token, $platform, $at)
                ON CONFLICT (code, seat) DO UPDATE SET
                    token = excluded.token, platform = excluded.platform, at = excluded.at;
                """,
                ("$code", code), ("$seat", seat), ("$token", token),
                ("$platform", platform), ("$at", Stamp(now)));
        }
    }

    /// <summary>
    /// Forgets a device the notification service says is gone, if it is still the one on file.
    /// </summary>
    /// <remarks>
    /// The token is part of the condition rather than just the code and the seat, and that is the
    /// whole point of the method. A player who reinstalled between a nudge being decided and it
    /// failing has already registered a working device under the same seat, and deleting that
    /// because the token it replaced is dead would lose them for the rest of the match.
    /// </remarks>
    public void ForgetDevice(string code, int seat, string token)
    {
        lock (_gate)
        {
            Execute(
                "DELETE FROM devices WHERE code = $code AND seat = $seat AND token = $token;",
                ("$code", code), ("$seat", seat), ("$token", token));
        }
    }

    /// <summary>Every device registered for a match, in seat order.</summary>
    public IReadOnlyList<Device> Devices(string code)
    {
        lock (_gate)
        {
            List<Device> found = new List<Device>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT seat, token, platform, at FROM devices
                WHERE code = $code ORDER BY seat;
                """;
            read.Parameters.AddWithValue("$code", code);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(new Device(
                    code,
                    row.GetInt32(0),
                    row.GetString(1),
                    row.GetString(2),
                    DateTimeOffset.Parse(row.GetString(3), CultureInfo.InvariantCulture)));
            }

            return found;
        }
    }

    /// <summary>When this seat was last told anything about this match, if ever.</summary>
    public DateTimeOffset? LastNudged(string code, int seat)
    {
        lock (_gate)
        {
            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                "SELECT MAX(at) FROM nudges WHERE code = $code AND seat = $seat;";
            read.Parameters.AddWithValue("$code", code);
            read.Parameters.AddWithValue("$seat", seat);

            object? found = read.ExecuteScalar();

            return found is string when && when.Length > 0
                ? DateTimeOffset.Parse(when, CultureInfo.InvariantCulture)
                : null;
        }
    }

    /// <summary>
    /// Writes a decided notification down. False if this seat was already told about this round.
    /// </summary>
    public bool RecordNudge(Nudge nudge)
    {
        lock (_gate)
        {
            using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText =
                """
                INSERT OR IGNORE INTO nudges (code, seat, round, device, at)
                VALUES ($code, $seat, $round, $device, $at);
                """;
            insert.Parameters.AddWithValue("$code", nudge.Code);
            insert.Parameters.AddWithValue("$seat", nudge.Seat);
            insert.Parameters.AddWithValue("$round", nudge.Round);
            insert.Parameters.AddWithValue("$device", nudge.DeviceToken);
            insert.Parameters.AddWithValue("$at", Stamp(nudge.DecidedAt));

            return insert.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Nudges decided but not yet delivered, oldest first.</summary>
    public IReadOnlyList<Nudge> PendingNudges(int most = 200)
    {
        lock (_gate)
        {
            List<Nudge> found = new List<Nudge>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT code, seat, round, device, at FROM nudges
                WHERE sent = 0 ORDER BY at LIMIT $most;
                """;
            read.Parameters.AddWithValue("$most", most);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                found.Add(new Nudge(
                    row.GetString(0),
                    row.GetInt32(1),
                    row.GetInt32(2),
                    row.GetString(3),
                    DateTimeOffset.Parse(row.GetString(4), CultureInfo.InvariantCulture)));
            }

            return found;
        }
    }

    /// <summary>Marks a nudge delivered, so nothing sends it twice.</summary>
    public void NudgeSent(string code, int seat, int round)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE nudges SET sent = 1
                WHERE code = $code AND seat = $seat AND round = $round;
                """,
                ("$code", code), ("$seat", seat), ("$round", round));
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
    public void Advance(string code, int toRound, DateTimeOffset now)
    {
        lock (_gate)
        {
            // The new round's window starts now. Guarded by the same round check, so a second caller
            // arriving a moment later cannot push the deadline out and give everybody a free hour.
            Execute(
                """
                UPDATE matches SET round = $round, round_opened_at = $now
                WHERE code = $code AND round < $round;
                """,
                ("$code", code), ("$round", toRound), ("$now", Stamp(now)));
        }
    }

    /// <summary>
    /// Changes how a match is paced, for a Live one that has lost somebody.
    /// </summary>
    /// <remarks>
    /// The design's disconnect-to-Anytime downgrade, and it is the only thing that can rescue a Live
    /// match from a player who walks off. Live pace has no deadline on purpose, because everybody is
    /// present, so a Live round waiting on somebody who is gone waits for ever and takes three other
    /// people's match with it. Moving the pace gives the round a window, and the forfeit sweep that
    /// was already running does the rest without knowing anything about sockets.
    ///
    /// The window hangs off when the round opened rather than off now, which is what the deadline is
    /// built from everywhere else, so a round that has been open ninety seconds gets a day from when
    /// it started rather than a day from when somebody noticed.
    ///
    /// Guarded on the pace it is coming from, so this cannot run twice and cannot walk a match that
    /// was always Anytime into a different window.
    /// </remarks>
    public void Downgrade(string code, Pace to, int windowSeconds)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE matches SET pace = $pace, window_seconds = $window
                WHERE code = $code AND pace <> $pace;
                """,
                ("$code", code), ("$pace", (int)to), ("$window", windowSeconds));
        }
    }

    // ---- Forfeits -------------------------------------------------------------------

    /// <summary>
    /// Records that a seat ran out of window.
    /// </summary>
    /// <remarks>
    /// A fact, not a plan. The relay knows the deadline and knows who has submitted, so it can see a
    /// forfeit for itself without ever looking at a payload, and clients feed nothing to the
    /// simulation for that seat. Synthesising an empty plan here instead would mean the relay
    /// knowing the plan format, which is the one thing it must never learn.
    /// </remarks>
    public bool Forfeit(string code, int round, int seat, DateTimeOffset now)
    {
        lock (_gate)
        {
            using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText =
                """
                INSERT OR IGNORE INTO forfeits (code, round, seat, at)
                VALUES ($code, $round, $seat, $at);
                """;
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$round", round);
            insert.Parameters.AddWithValue("$seat", seat);
            insert.Parameters.AddWithValue("$at", Stamp(now));

            return insert.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Which seats gave up on a round, in seat order.</summary>
    public IReadOnlyList<int> Forfeited(string code, int round)
    {
        lock (_gate)
        {
            List<int> seats = new List<int>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT seat FROM forfeits
                WHERE code = $code AND round = $round ORDER BY seat;
                """;
            read.Parameters.AddWithValue("$code", code);
            read.Parameters.AddWithValue("$round", round);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                seats.Add(row.GetInt32(0));
            }

            return seats;
        }
    }

    /// <summary>
    /// Every started match whose round window has run out, for the sweep to deal with.
    /// </summary>
    /// <remarks>
    /// Only matches with a window at all, which excludes Live pace by construction rather than by a
    /// condition somebody has to remember. Codes only: the sweep reads each match properly afterwards
    /// so that it is working from the same values every other caller sees.
    /// </remarks>
    public IReadOnlyList<string> Overdue(DateTimeOffset now, int most = 200)
    {
        lock (_gate)
        {
            List<string> codes = new List<string>();

            using SqliteCommand read = _connection.CreateCommand();
            read.CommandText =
                """
                SELECT code FROM matches
                WHERE started = 1 AND window_seconds > 0
                  AND datetime(round_opened_at, '+' || window_seconds || ' seconds') <= datetime($now)
                ORDER BY round_opened_at
                LIMIT $most;
                """;
            read.Parameters.AddWithValue("$now", Stamp(now));
            read.Parameters.AddWithValue("$most", most);

            using SqliteDataReader row = read.ExecuteReader();

            while (row.Read())
            {
                codes.Add(row.GetString(0));
            }

            return codes;
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

    /// <summary>The other half of <see cref="Stamp"/>.</summary>
    private static DateTimeOffset When(string stamped) =>
        DateTimeOffset.Parse(stamped, CultureInfo.InvariantCulture);

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
