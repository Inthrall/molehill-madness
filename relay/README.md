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

## What is not here yet

Live pace works, but by polling: both paces use the same submit-and-fetch endpoints, and Live simply polls faster. The WebSocket hub the design calls for is an optimisation on top of a protocol that already works, so it waits.

Forfeits and round windows are task 4.2, accounts and the age gate are 4.4, and neither exists here. Timestamps are stored in a form that sorts and round-trips exactly so that the forfeit job has something to read when it arrives.

Hosting is a small container app plus, eventually, managed Postgres. There is no Dockerfile in here yet because there is nowhere to verify one builds from this machine, and an unverified deployment file is worse than an absent one.

## Tests

```
dotnet test relay/Relay.Tests
```

The store tests run the real SQL against a real SQLite, in memory, rather than against a substitute, because the interesting behaviour lives in the schema: "first submission wins" is `INSERT OR IGNORE` against a composite primary key, and a fake store would happily agree with a bug in it. One test uses a temporary file instead, since the file-backed path is the one that gets deployed and reopening it is what persistence actually means.

The endpoint tests boot the real application in process, because most of what could break in an endpoint is the pipeline around it: the route constraint, the model binding, reading the plan off the raw body, and the header the token arrives in. None of that exists when a handler is called as a method.

Four tests hammer the store from many threads at once. Those are not thoroughness for its own sake: the store is a singleton behind a server that answers requests in parallel, and three of the four fail without the lock that `MatchStore` holds.
