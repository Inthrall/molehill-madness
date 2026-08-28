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

**This has never been built.** Docker is installed on the machine it was written on and its engine will not start, so what can be said about it is exactly this much: the publish step is the same command run outside a container, and it produces `Relay.Api.dll`; that binary was then started with the container's own configuration, answered `/health`, opened a lobby and wrote its SQLite file to the path the image sets. Everything in the file above that is not the image layering has been run. The layering has not. That is a weaker claim than this repository usually makes and it is written down rather than left to be discovered.

## What is not here yet

The email link. An account is anonymous and stays on one device, so a player who reinstalls is a new player, and the design's "lightweight account (email or platform sign-in)" is what eventually carries one between a phone and a desktop. It is not here because it needs something that can send mail and be seen to have sent it, which cannot be verified from the machine this was written on, and because an under-threshold account must never be asked for one: the feature is half a gate as well as half a login, and half of it built is worse than none.

Platform-level parental approval. The design lets an under-threshold account into the pool with it, and it is not a parameter here because nothing can set it truthfully: a store hands it to us, a player cannot tick it, and a flag this service accepted on somebody's word would be a hole in the gate wearing the name of a safeguard. When a platform can assert it, it arrives as a second field on the account and the rule becomes an or.

## Tests

```
dotnet test relay/Relay.Tests
```

The store tests run the real SQL against a real SQLite, in memory, rather than against a substitute, because the interesting behaviour lives in the schema: "first submission wins" is `INSERT OR IGNORE` against a composite primary key, and a fake store would happily agree with a bug in it. One test uses a temporary file instead, since the file-backed path is the one that gets deployed and reopening it is what persistence actually means.

The endpoint tests boot the real application in process, because most of what could break in an endpoint is the pipeline around it: the route constraint, the model binding, reading the plan off the raw body, and the header the token arrives in. None of that exists when a handler is called as a method.

Four tests hammer the store from many threads at once. Those are not thoroughness for its own sake: the store is a singleton behind a server that answers requests in parallel, and three of the four fail without the lock that `MatchStore` holds.
