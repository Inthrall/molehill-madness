# Relay

The server two phones talk to when they play a match apart. Plan task 4.1.

It is deliberately boring, and the boringness is the design rather than a stage it will grow out of. A plan is a list of inputs, not an outcome, so the relay stores a seed and a pile of opaque payloads and hands them back once everybody's is in. It never parses a plan, never validates one and never simulates. That is what keeps a second implementation of the rules from existing in here, and it is why the whole service is four files.

The cheating model follows from the same fact. Because a payload is inputs, you cannot submit an illegal state, only illegal inputs, and every client's simulation rejects those identically. The relay does not need to know a mole from a mortar to be safe.

## Running it

```
dotnet run --project relay/Relay.Api
```

That comes up on the Kestrel default with an in-memory database, which is right for a local run and wrong for anything a player might come back to. Point it at a file to keep matches:

```
Relay__Database=/var/lib/molehill/relay.sqlite dotnet run --project relay/Relay.Api
```

SQLite first, Postgres when it hurts, and it will not hurt for a long while: an Anytime match is a few kilobytes a day, so thousands of concurrent matches are a rounding error. The first real scaling event would be a lovely emergency.

## The shape of a match

| | |
| --- | --- |
| `POST /lobbies` | `{"playerCount":2,"pace":"Anytime"}`. Draws the code and the seed, seats the host, returns both plus a seat token. |
| `POST /lobbies/{code}/seats` | Takes the next seat. The code is tidied first, because it arrives via a human ear and a human thumb. |
| `GET /lobbies/{code}` | Who is in it and which round it is on. Public, so no tokens in the reply. |
| `POST /matches/{code}/rounds/{round}/plan` | The raw bytes of a plan, with `X-Seat-Token`. First submission for a seat wins. |
| `GET /matches/{code}/rounds/{round}` | Nothing until every seat has committed, then all of them. |

Two rules in there are load-bearing rather than incidental.

**Nothing is released until every seat is in.** Handing back a partial round would let the last player to commit see what everybody else did first, which is the single thing simultaneous turns exist to prevent. A partial round returns a count and no plans.

**A seat gets one submission per round.** A second one is refused rather than merged, because a seat that sends twice has either double-tapped commit or is trying to change its mind after a peek.

A round that nobody can complete is swept rather than left. One player who loses interest must not be able to end a match for three other people by never opening the game again, so a lobby carries a window, and a sweep forfeits whoever ran out of it. A seat that has already submitted is never forfeited however late the sweep finds it: their turn is in, and taking it away would be worse than the delay.

## Being told at once

`GET /matches/{code}/live`, upgraded to a WebSocket with the same seat token everything else takes. It is a doorbell rather than a delivery van: the socket says "round four is ready" and the client fetches round four through the endpoint it would otherwise have polled. Nothing that matters travels over it, so a socket that drops costs a second of latency and nothing else, and the polling path stays the truth rather than becoming a branch that only runs once something has already gone wrong. A client with a socket up asks every ten seconds instead of every second, and hears about the round sooner than it would have anyway.

Notices are decided by watching the store rather than by ringing the bell from the endpoints. Ringing from the endpoints is lower latency and is one forgotten call site away from a match that silently stops notifying, which would only ever show up as "sometimes it takes a second longer", the least reportable symptom there is. A watcher means one place decides and no call site can disagree, and it costs two local reads per quarter second per match somebody is actually listening to.

Live pace has no deadline, deliberately, because everybody is present. This is where that stops being true. A Live round still waiting ninety seconds on a seat that has neither submitted nor got a socket has lost that player rather than found a slow one, since a connected client commits whatever it had at the buzzer, so the match downgrades to Anytime and the forfeit sweep that was already running finishes it. Without that, one person closing their phone leaves three others on a round that can never settle.

## Notifications

Telling somebody it is their turn is two halves. Deciding who to tell is the half with a rule in it, "one a day per match at most", and it is enforced by reading the outbox table rather than by remembering anything. Sending is an HTTP call to somebody else's service, so it is a background drain over the same table: an undeliverable nudge is a row rather than a lost event, and swapping the sender is one class.

Point the relay at a Google service account key and it sends through Firebase Cloud Messaging. Leave it unset and it writes the notifications into the log instead, which during development answers the only question worth asking, which is whether the right people are being told at the right times.

```
Relay__Firebase__ServiceAccount=/var/lib/molehill/firebase.json dotnet run --project relay/Relay.Api
```

`GOOGLE_APPLICATION_CREDENTIALS` works too, since that is the convention every other Google client uses. A key that is configured and unusable stops the process rather than quietly falling back to the log: an operator who set the path and got silence has no way to tell a missing key from a quiet day, and a quiet day is what anybody would assume.

What the sender cannot claim is that Google likes the bytes, because there is no Firebase project behind this repository to send to. Everything up to the socket is tested against a stub: a real key signs a real assertion which is verified the way Google verifies it, the bearer is minted and cached and dropped a minute early and thrown away when it is refused, and each documented error is put in front of it to see which of four answers it gives. Those four are the part worth being careful about. Sent and deferred and dropped and unregistered are not a boolean, and a boolean was going to start lying: an outbox that retries a dead phone spins for ever, and one that gives up on a busy service loses the round.

## Accounts, and the one gate

An account is only ever needed to be let in among strangers. Couch play needs none, and joining by code needs none either, which is why none of the endpoints above ask for one: a code arrives from somebody you know, and the person who read it out is accountable for who else is in the lobby. That absence is the design rather than an oversight, and `Allowed.JoiningByCode` is written down so it reads as one.

| | |
| --- | --- |
| `POST /accounts` | `{"band":"Adult"}`. Makes an anonymous account and returns the id and the secret. |
| `PUT /accounts/band` | The band again, with `X-Account` and `X-Account-Secret`, for a player who has had the birthday that moves it. |

What is stored is an opaque id, the secret that owns it, and one of three age bands. Not a date of birth, not an age, not a name, not an email, nothing anybody could be found by: the design asks for "no discoverable social graph", and the cheapest way to have none is to store nothing one could be built out of. The date is typed once on the device, turned into a band there, and never leaves it.

Three bands rather than two, because "we have not asked yet" is a real state. Defaulting an unasked account to the safe side would be tempting and wrong: it would silently apply child protections to adults and, worse, would make a bug that skipped the gate look like a working gate.

The secret is returned once and cannot be reissued. There is nothing in an account to recover it by, deliberately: no email for an under-threshold one, and the design says there must not be. Losing it costs a player nothing they can name, since an account holds no progress, no purchases and no friends, only permission to be put among strangers.

The client has the same rule about who may be matched written down, and the client's copy is not the enforcement. A guard that runs on the device decides whether a button is offered and can be edited out by anybody who cares to. This one is the gate. It is a rule stated twice, which is worth being uncomfortable about and is still smaller than the alternatives: sharing a library would mean the relay referencing the game, which is what stops it ever learning what a plan is, and trusting the client would mean not having a gate at all.

## The pool

| | |
| --- | --- |
| `POST /queue` | `{"playerCount":4,"pace":"Live"}` with the account headers. Returns a ticket. |
| `GET /queue/{ticket}` | Still waiting, with how long and whether that is slow; or the seat, once there is one. |
| `DELETE /queue/{ticket}` | Gives the place back. |

One region-wide pool, no skill brackets, no ranking, exactly as the design says and for the reason it gives: a new free game with a thin population is a queue that never fills, and every bracket divides a population that cannot afford dividing. The only thing separating one queue from another is how many seats somebody asked for and which clock they asked for, because those are different requests rather than different skill levels.

Oldest first is the only fairness rule in it. Partial groups are left waiting rather than seated short: a host in a lobby may lower the count and start, because a host is a person making a decision, and nothing in the pool is in a position to make that one on anybody's behalf.

What comes out is an ordinary lobby. It is opened through the same call a host uses and seats people through the same Join, so it has a code somebody could read out and nothing downstream can tell how its players found each other. That is worth more than it costs: every rule about rounds, forfeits, notifications and sockets already works on it, because there is nothing new to work on.

A queue that is not filling says so rather than spinning. After forty-five seconds a waiting ticket comes back marked slow, and the design's answer to a thin pool is the other pace, "offered by default to anyone whose Live queue is slow". The relay says when; the player decides whether, because changing somebody's pace out from under them would be answering a different question from the one they asked.

Joining the pool is idempotent. The case it protects against is not somebody being clever, it is a phone that sent the request and lost signal before the reply: refusing the second one would leave a player holding no ticket while the pool holds their place, which is the one state neither end can get out of.

## The image

```
docker build -t molehill-relay relay/
docker run --rm -p 8080:8080 -v molehill:/data molehill-relay
```

The build context is this directory rather than the repository root, which is a guard rather than a convenience. The relay has no project reference to MoleSim on purpose, because the moment it can see a `Plan` type it becomes possible for it to start having opinions about one, and a context that cannot reach the simulation means adding that reference breaks the image build instead of passing quietly.

The runtime image is the ordinary one rather than a chiselled or Alpine build. SQLite arrives as a native library through SQLitePCLRaw, and the small images are where that stops being somebody else's problem: musl wants a different build of it, and finding that out from a container that starts and then throws on the first query is a bad afternoon. Hosting is this image plus, eventually, managed Postgres. The database goes on a volume, since the whole point of Anytime pace is that a match outlives the process. There is no `HEALTHCHECK` line: the image carries no curl, adding one is a package and an attack surface for something every host does better itself, and `/health` is there for the platform to probe.

**Built and run.** This said "this has never been built" for as long as Docker was installed here and its engine would not start, which left the layering as the one part of the file nobody could vouch for. The engine started, so it has been built and it has been run, and the claim can be the ordinary one now.

The image comes out at 392 MB on Docker 29.5.3 with Linux containers, from `mcr.microsoft.com/dotnet/sdk:10.0` to publish and `aspnet:10.0` to run. What was checked was the protocol rather than the exit code, because a container that starts is not a relay that works: `/health` answered `200` and `{"ok":true}`; a lobby opened and handed back a code, a seat, a token and a seed; a second seat joined and started the match; one seat committing returned `complete: false` and `waitingOn: 1` and released no plans, which is the rule the whole design rests on; both seats committing returned both payloads with their bytes intact; a seat submitting twice got `409`; a made-up seat token got `401`.

Restarting the container returned the finished round with both payloads still in it, which is the volume doing the job it is there for and the only claim the image adds over `dotnet run`: an Anytime match outliving the process.

One path is still unexercised rather than unbuilt. With no Firebase key set the notifications are supposed to go to the log, and nothing has been through that branch, because it takes a round completing while somebody is still waiting on it and two seats committing a second apart never waits.

## Parental approval

| | |
| --- | --- |
| `POST /accounts/approval` | `{"grant":"<payload>.<signature>"}` with the account headers. |

The design lets an under-threshold account into the pool "with platform-level parental approval", and the two words carrying the weight are platform-level. This is not a setting, not a box a player ticks and not a field a client fills in: it is a statement made by a store, about one named account, signed with a key the store holds and the relay only has the public half of.

That shape is the whole feature. An approval accepted on a client's word would be a hole in the age gate wearing the name of a safeguard, and worse than no approval at all, because it would read as protection to anybody auditing the list. A signature makes the claim exactly as good as the platform that made it and useless to everybody else, the player it is about included.

Everything that can make a grant worthless is checked, and each check is a way one would be misused: an unverifiable signature is a forgery, an unconfigured platform is a stranger, a grant naming a different account is one lifted from somewhere else, and an old or future-dated one is a replay or a stretched clock. All of them come back as the same refusal, deliberately and without saying which, because the caller's answer is the same in every case and a reply that told them apart would be a way to ask this relay questions about grants it never issued.

Configure a platform's public key as `Relay:Approvals:<platform>`, in PEM, ECDSA P-256. Read once at startup so a mistyped one stops the process rather than failing the first time a child tries to play. **With no keys configured, nothing can be approved**, which is the correct default and the state this ships in: the mechanism is real and there is nobody yet entitled to use it.

An approval buys the stranger pool. It does not buy an address: the design gives under-threshold accounts no email collection, and reading an approval about playing with strangers as covering that too would be inventing consent from an adjacent sentence.

## Linking an address

| | |
| --- | --- |
| `POST /accounts/email` | `{"email":"..."}` with the account headers. Sends a six-character code. |
| `PUT /accounts/email` | `{"code":"..."}`. Attaches the address once the code comes back. |

Adults only, refused here rather than only on the device. Not collection with a consent box, not collection deleted later, and not collection with a parental approval attached.

The rest of the rules exist because a verification endpoint is one of the classic ways to make a service into somebody else's problem. Anybody who can call it can cause mail to be sent to an address they do not own, so an account gets one code a minute, a code lasts half an hour, five wrong guesses end the claim, and an address already attached to an account is refused rather than moved, since an address is how an account is recovered and moving one would be letting somebody take an account over.

Point the relay at a mail server and it sends; leave it unset and the codes go to the log, which during development answers the only question worth asking.

```
Relay__Smtp__Host=smtp.example.com Relay__Smtp__User=... Relay__Smtp__Password=... \
  Relay__Smtp__From=moles@example.com dotnet run --project relay/Relay.Api
```

SMTP rather than one provider's HTTP API, and that is not laziness. Every provider hands out SMTP credentials, so this picks no vendor and needs no package, and unlike the push notifications it can be tested against a real server: the tests stand one up in the process and read the bytes as they arrive. There is nothing to hedge about here in the way there is about Firebase.

## What is not here yet

Nothing about accounts. What remains is a decision rather than a gap: an account is still per-device unless an address is linked to it, and there is no flow yet for using that address to recover one on a new device, because recovery is a second set of abuse questions and it wants its own pass rather than being tacked onto the end of this one.

## Tests

```
dotnet test relay/Relay.Tests
```

The store tests run the real SQL against a real SQLite, in memory, rather than against a substitute, because the interesting behaviour lives in the schema: "first submission wins" is `INSERT OR IGNORE` against a composite primary key, and a fake store would happily agree with a bug in it. One test uses a temporary file instead, since the file-backed path is the one that gets deployed and reopening it is what persistence actually means.

The endpoint tests boot the real application in process, because most of what could break in an endpoint is the pipeline around it: the route constraint, the model binding, reading the plan off the raw body, and the header the token arrives in. None of that exists when a handler is called as a method.

Four tests hammer the store from many threads at once. Those are not thoroughness for its own sake: the store is a singleton behind a server that answers requests in parallel, and three of the four fail without the lock that `MatchStore` holds.
