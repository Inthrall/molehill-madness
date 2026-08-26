# Molehill Mayhem

A turn-based artillery game where everybody takes their turn at the same time.

Four players, four moles each, one shared eight-second clock. Every round all four plot a move at once, then every plan resolves together: shots have to lead moving targets, four ambushes collide mid-air, and the best-laid plan marches proudly into a crater that did not exist when it was drawn. At round eight a line of lava appears along the bottom of the map and starts climbing, then closes in from the sides, until there is nowhere left to be careful.

Steam and Android, cross-play, free, cosmetics only. (iOS is out of scope until after the MVP.) Multiplayer only: no campaign, no AI. No words anywhere in the game.

## Documents

Both are single-file HTML, readable in any browser.

- [`docs/molehill-mayhem-design.html`](docs/molehill-mayhem-design.html) — the game design document. What the game is and every decision behind it.
- [`docs/implementation-plan.html`](docs/implementation-plan.html) — the implementation plan. Seven phases, two hard gates, and the engineering contracts.

## Layout

```
sim/     MoleSim, the deterministic game, and its tests
tools/   headless CLI, terrain dumps, corpus runners
client/  Godot 4 project (added at Phase 2)
relay/   ASP.NET Core lobby and turn-exchange service (added at Phase 4)
docs/    design document, implementation plan
```

The architectural rule, from which everything else follows: **MoleSim is the game and the engine is a lens pointed at it.** MoleSim is a plain `netstandard2.1` library with no engine types, no I/O and no floating-point arithmetic anywhere. The client renders it, the relay ferries its plans around, the tools replay it. Nothing else may mutate its state.

## Determinism

Every device must compute the same match from the same inputs, or online play is impossible. Three rules make that testable rather than hopeful:

1. **Fixed-point maths only.** `Fix64` (Q48.16) throughout the simulation. IEEE 754 arithmetic varies between platforms and would silently fork a live match between a phone and a PC.
2. **One seeded generator per match.** `MatchRng` (xoshiro256\*\*), drawn in a defined order, so replays are bit-exact down to which knockout animation plays.
3. **A golden corpus that only grows.** Every fixed bug leaves a recorded match behind, replayed on every platform in CI. The corpus is the reviewer a solo project does not otherwise have.

## Building

Needs the .NET 10 SDK (the library targets `netstandard2.1`; tests and tools target `net10.0`).

```bash
dotnet build
dotnet test
```

## Status

**Phase 0 gate: passed.** The simulation computes identical results on Windows, Linux and Android, across two CPU architectures and two .NET runtime versions, and the probe runs in 22 ms at 60 fps on a phone. Full evidence and the deferred risks are in [`docs/phase-0-gate.md`](docs/phase-0-gate.md).

**Phase 1 complete.** MoleSim plays a whole match headlessly: seamless movement priced in stamina, the full fifteen-weapon arsenal, projectiles and blasts with line of sight, crates, lava, pacing and the knockout reel. `dotnet run --project tools/Molehill.Cli -- match` plays one to a winner and prints it round by round. 217 tests, with a golden corpus of pinned match hashes verified on every platform in CI.

Next: Phase 2, the fun proof. The first build that can answer whether any of this is actually funny with four people in a room.

## Licence

All rights reserved. Public so the work is readable and so the documents can be linked; not open source, and not licensed for reuse.
