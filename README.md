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

**Phase 1 complete.** MoleSim plays a whole match headlessly: seamless movement priced in stamina, the full fifteen-weapon arsenal, projectiles and blasts with line of sight, crates, lava, pacing and the knockout reel. `dotnet run --project tools/Molehill.Cli -- match` plays one to a winner and prints it round by round. A golden corpus of pinned match hashes is verified on every platform in CI.

**Phase 2 in progress.** The game is playable. `client/scenes/Match.tscn` plays a whole match: plan a route by dragging ink, watch a ghost of your mole walk it while the gauges drain, aim and stamp the turn's shot, plant a charge if you fancy it, then watch every plan resolve at once over eight seconds. Craters appear when the shells land, damage numbers rise and fade where they hit, and moles leave on one of two rough exits. 225 tests.

Planning is simultaneous, which is what split screen is for. Every platoon with its own controller plans at the same moment on one shared clock; platoons without one share the pointer and take turns, and the clock resets as it changes hands. The same code covers a couch full of gamepads and a prototype on one mouse, so the testable configuration is not a separate build. The screen carves into a pane per platoon while they plan, and for the replay it follows the design's rule: one shared view when the action is close enough to share, a pane each when it is not, decided once from the finished recording so the screen never splits and merges mid-round.

The HUD is wordless. Every weapon and gauge is drawn from primitives in `Glyphs.cs` rather than imported, which scales to any screen without a sprite sheet per density, recolours per platoon for free, and leaves the prototype with no art dependency at all. Digits survive only for damage, which is the exception the design carves out on the grounds that a numeral reads the same in every language a seven-year-old might have. The planning clock is a ring for the same reason.

On a phone it collapses to one view with the controls the design specifies: a weapon wheel you flick, a button to fire that doubles as the aim stick, one to plant, one to reset and one to commit. Touch reaches the same handful of verbs a mouse does, so the rules cannot tell a thumb from a cursor.

Three findings so far, all from watching rounds render rather than from a test. Craters used to be as wide as the blast that made them, which left the map unrecognisable by round five and contradicted pacing the design had already fixed, so a crater is now its own number and `ArsenalTests` defends the map surviving a dozen rounds. A round resolves before its first frame is drawn, so anything read from live state during playback gives the ending away: the map and the score are both replayed from the recording instead. And a gauge scaled by its own value is unreadable at low values, which is why the wind arrow keeps its length and puts the strength in the streaks behind it.

`--demo` drives the game through its own interface without a player, `--frail` starts everybody nearly out so the knockouts can be watched, `--split` forces the panes apart, and `--touch` brings up the phone controls on a desktop. With Godot's `--write-movie` they make the render layer inspectable frame by frame, which is how all three findings turned up:

```bash
godot --path client --write-movie frames/f.png --fixed-fps 30 --quit-after 300 -- --demo --split
```

Still owed: the gamepad axis reads have never met real hardware, though the simultaneous planning they feed is exercised by the driver. And the structured playtests, which are the actual gate. No amount of this code can answer whether it is funny.

## Licence

All rights reserved. Public so the work is readable and so the documents can be linked; not open source, and not licensed for reuse.
