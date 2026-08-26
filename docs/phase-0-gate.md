# Phase 0 gate: proceed

**Verdict: proceed to Phase 1, with one leg of the gate deferred and named.**

The implementation plan puts a hard gate at the end of Phase 0 and gives it the authority to reopen the engine decision. Two questions had to be answered before anything else was worth building:

1. Does Godot's C# export actually run on a phone, at a sensible frame rate?
2. Does the simulation compute the same answers there as it does on a desktop?

The second is the one that mattered, because a no would have invalidated the whole technical plan rather than just the engine choice.

## What was measured

The determinism probe (`MoleSim.Diagnostics.DeterminismProbe`) runs a scripted workout across every part of the simulation that could plausibly differ between platforms: a long chain of fixed-point multiplies, divides, roots and vector lengths; ten thousand draws from the seeded generator; two thousand carves against a terrain grid, hashed both incrementally and from scratch. Everything folds into one `COMBINED` value.

It lives in the simulation rather than in the tools deliberately, so every platform runs byte-identical code. A probe reimplemented per platform could drift and would then be measuring itself.

| Platform | CPU | Runtime | Host | `COMBINED` |
|---|---|---|---|---|
| Windows 11 | x86-64 | .NET 10 | console runner | `D1096413CA1B6CF8` |
| Windows 11 | x86-64 | .NET 9 | Godot 4.7.2 | `D1096413CA1B6CF8` |
| Ubuntu | x86-64 | .NET 10 | console runner, CI | `D1096413CA1B6CF8` |
| Android | **ARM64** | .NET 9 | Godot 4.7.2, Mali-G78 | `D1096413CA1B6CF8` |

Every individual component matched too, not just the fold: `fix64 0000000535C0AB5B`, `rng 1982B36F586BE4A9`, `terrain C046A758BC161E00`, and the rolling terrain hash agreed with a full recompute on every platform.

## Why this is stronger evidence than it looks

Three things make this more than "it worked on my machine twice".

**Two CPU architectures.** x86-64 and ARM64 are precisely where IEEE 754 arithmetic diverges in practice: different fused-multiply-add behaviour, different intermediate precision, different library implementations of transcendental functions. Getting identical results across that boundary is the whole reason the simulation is integer-only, and it is now demonstrated rather than assumed.

**Two .NET runtime versions.** The console runner is .NET 10 and Godot is .NET 9. Agreement across a runtime major version is evidence that nothing depends on JIT behaviour that a future upgrade could change underneath us.

**A hand-rolled numeric stack, on purpose.** `Fix64` computes its own 128-bit intermediates for multiply and divide rather than calling a platform intrinsic. That looked like extra work; it is why there is no hardware-dependent path to diverge.

## Performance

| | Desktop | Phone |
|---|---|---|
| Probe | 12 ms | **22 ms** |
| Frame rate | vsync | **60 fps** |

The plan's tick budget is a full 240-tick four-player round resolving in under 250 ms on a mid-range phone. The probe is a heavier workload than one round and completes in 22 ms, so there is roughly an order of magnitude of headroom. Ghost previews, instant replays and the clip renderer all depend on re-running the simulation constantly, and that now looks comfortably affordable.

## What is not proven, and what follows from it

**iOS.** The plan's gate asks for both phones. Godot reports that Apple Embedded export with C#/.NET "is experimental and requires macOS", so it cannot be attempted from a Windows machine at all. This is a deferral, not a pass.

It is an acceptable deferral for three reasons: `MoleSim` has zero engine dependencies, so the determinism result carries over to any host that runs .NET; the same architecture is what makes the plan's "replace the lens" fallback real; and the risk is contained to the client layer, which is deliberately thin. It is **not** an acceptable deferral past Phase 4, where iOS builds are needed for TestFlight, so a Mac is now a dated dependency rather than a vague one.

**Godot's .NET Android export is labelled experimental** by Godot itself in 4.7. It works, and it produced a correct build here, but that label is a live risk to the engine choice rather than a footnote. It wants watching as the client grows past a single scene.

**Four-viewport performance** is untested and remains a Phase 3 spike. One scene at 60 fps says nothing about four cameras.

## What the gate cost, and what it caught

Three things had to be discovered rather than assumed, which is what a gate is for:

- Godot 4.7.2's Android export template accepts `net9.0` only, even though the editor's own assemblies are `net8.0`.
- Android export requires ETC2/ASTC texture compression enabled in project settings.
- Godot needs a classic `.sln` beside the client `.csproj`, and .NET 10 creates `.slnx` by default. **Without it the export silently produced a 28 MB APK containing no C# at all**: it warned, signed it, and looked like success. Verifying that `MoleSim.dll` is genuinely inside the package is now part of the deploy script, because a signed APK that runs nothing resembles a working one closely.

No Android SDK was installed. The toolchain is borrowed from an existing Unity installation, which ships a current SDK, NDK and JDK 17.

## Decision

Proceed to Phase 1, MoleSim v0. The engine decision stands. The determinism approach is validated across the two CPU architectures that matter, and the performance headroom is large enough that the plan's reliance on cheap re-simulation is safe.

Carry forward as tracked risks: iOS remains unproven and needs a Mac before Phase 4; Godot's .NET mobile export is experimental; four-viewport performance is still a Phase 3 question.
