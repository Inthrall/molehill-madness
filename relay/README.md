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

## What is not here yet

Accounts and the age gate are task 4.4 and are not here.

Hosting is a small container app plus, eventually, managed Postgres. There is no Dockerfile in here yet because there is nowhere to verify one builds from this machine, and an unverified deployment file is worse than an absent one.

## Tests

```
dotnet test relay/Relay.Tests
```

The store tests run the real SQL against a real SQLite, in memory, rather than against a substitute, because the interesting behaviour lives in the schema: "first submission wins" is `INSERT OR IGNORE` against a composite primary key, and a fake store would happily agree with a bug in it. One test uses a temporary file instead, since the file-backed path is the one that gets deployed and reopening it is what persistence actually means.

The endpoint tests boot the real application in process, because most of what could break in an endpoint is the pipeline around it: the route constraint, the model binding, reading the plan off the raw body, and the header the token arrives in. None of that exists when a handler is called as a method.

Four tests hammer the store from many threads at once. Those are not thoroughness for its own sake: the store is a singleton behind a server that answers requests in parallel, and three of the four fail without the lock that `MatchStore` holds.
