# CLAUDE.md

Guidance for Claude Code working in this repository.

**Molehill Madness** is a Worms-style artillery game where all 2-4 players plan simultaneously (WeGo) and the round resolves on one shared clock. Public repo, all rights reserved, no OSS licence.

---

## Architecture

Godot 4 + C#, but **the game is `MoleSim`**: a pure `netstandard2.1`, fixed-point (Q48.16), fully deterministic library that the engine only renders. Projects: `sim/` (MoleSim), `client/` (Molehill.Client, the Godot app), `relay/` (Relay.Api), `online/` (Molehill.Online), `clip/` (Molehill.Clip), `tools/`.

**Determinism is the whole design, and CI enforces it.** Each OS runs `Molehill.Cli selftest`, uploads a fingerprint, and a `Platforms agree` job fails the build if they differ. Separate guards grep MoleSim for banned APIs (`float`, `double`, `System.Random`, `DateTime`) and assert MoleSim has **zero** project or package references.

The Phase 0 gate passed with one combined hash byte-identical on Windows x86-64, Windows Godot, Ubuntu CI **and Android ARM64**. That ARM64-vs-x86 match is the point: it is the boundary where IEEE 754 diverges, so the integer-only design is demonstrated rather than assumed.

---

## Documentation and house style

The **game design document** (`docs/molehill-madness-design.html`) and the **implementation plan** (`docs/implementation-plan.html`) are single-source HTML in this repo and **nowhere else**. They cross-link by relative filename, so opening either from `docs/` resolves the other.

⚠️ **They used to be published as Claude artifacts, and those were retired deliberately.** The repo is the single source; a published copy was just a second place to go stale, which is exactly what happened after a rename. **Do not republish them, and do not put artifact URLs back into the HTML.** Publishing anything out of this repo needs an explicit ask.

House style for these docs: **no em dashes**, NZ English, earthy palette (grass green / soil / lava tokens), Titan One + Bricolage Grotesque + Nunito Sans + IBM Plex Mono, decisions tables with pills, hand-drawn SVG diagrams.

---

## Settled design calls

Every design decision is made; GDD §21 is "Checkpoints", not open questions. The load-bearing ones:

- **4-player FFA only** (hard ceiling), 4 moles each, no classes.
- **Ink-style planning**: forward-only, one reset per turn, spares in crates.
- **Damage ends a mole's input** (the Worms rule). Hitting before someone's fire timestamp cancels their shot.
- **Lava line from round 8**, rising then boxing in from the sides; bouncy (10 pluck x 2, KO on the third landing).
- **Multiplayer only. No AI, no solo, ever.**
- **Wordless UI** — damage digits are the sole numeral, on by default.
- **All-ages**: ESRB E target, PEGI 7 floor accepted, no text chat, generated names only.
- **Free plus cosmetics only.**
- **Kahoot-style game codes** with a host who picks player count. No friend codes or URLs.
- **Online paces**: Live (60s) and Anytime (24h then forfeit).
- **One-press portrait clip sharing by deterministic re-render**, not screen capture.

⚠️ **iOS is out of scope until after the MVP.** Targets are **Steam + Android**, with cross-play. The recorded cost is **reach, not engineering**: the clip-sharing growth loop dies on an iPhone, and the WASM web replay player is the cheap partial answer. That is the argument to weigh when revisiting.

**The outstanding gate is Phase 2.6 — three structured playtests with four humans. No amount of code answers it.**

---

## Simulation traps

- ⚠️ **Cell size is 1/16 m (6.25 cm), NOT the design's nominal 5 cm.** One twentieth of a metre cannot be represented in Q16, which made a position of exactly -0.1 m land in the wrong cell. A power-of-two cell converts by shifting, exactly and reversibly. **Do not "fix" this back to 5 cm.**
- ⚠️ **Movement cost is sampled at the body's LEADING EDGE, not its centre.** A tunnelling mole stands in the hole it just made, so the centre always reads Air. The related pattern is **"carve first, then check if still blocked"** — the "is the centre material diggable?" bug has been made twice.
- ⚠️ **`CarveBody` carves one cell WIDER than the body radius**, or a mole stays blocked by the sliver it left behind and tunnelling stops dead after one step.
- ⚠️ **Step-up must be gated on route direction** (`direction.Y > 0.35` means dig), or a mole told to tunnel down steps into the clear air above its target. All tests passed while this was broken; it was found by *drawing* the round via `molehill walk`.
- **Aim is stored as a direction vector, not an angle**, which avoids fixed-point trig entirely. Airborne moles fire relative to their tumble via an exact complex multiply.
- ⚠️ **Ballistic blasts require line of sight; only Fracking ignores it** — and shielding is computed **before** the crater carves, or the blast removes the dirt that was shielding. This is what makes the underground worth its stamina.
- ⚠️ **Seismic blasts must NOT crater.** Cratering blew the roof off the very column Fracking was about to collapse.
- ⚠️ **Tunnel collapse must walk each column from the top**, tracking "have I passed a roof". Testing only the cell directly above fills a tunnel's ceiling and leaves the tunnel usable.
- **Weapons are grouped by `WeaponKind`** (Thrown / FromTheSky / Planted / Melee / Seismic / Tool) rather than 15 special cases; traps, snares and vents share one `Placement` list.
- **Fracking's oil slick is deliberately NOT implemented** — movement is route-following at constant speed, so friction does not exist for a slick to act on. It waits for a momentum-based movement model rather than being faked.
- **Crates score candidate spots on fairness first** (the gap between nearest and furthest platoon), centrality only as tie-break; they embed 1 m so the last stretch must be dug. Claim rules: 1 takes it, 2 split, 3+ shatter it.
- **`KnockoutReel.Choose` picks the exit animation in the sim** from cause + damage + shove + underground, and all knockouts route through `MoleMatch.RecordKnockout` so the call sites cannot disagree.
- **Firing flat at a target ~9 m away misses** (the shell drops ~0.6 m and lands short of its blast radius). Correct artillery behaviour: tests that just want "damage happened" should shoot point-blank.

### The golden corpus

`CorpusTests` pins five match hashes. ⚠️ **These are STABILITY checks, not correctness oracles.** A deliberate rules change updates the pin in the same commit; **an accidental divergence must NOT be papered over by refreshing it.** ⚠️ **Adding an RNG draw anywhere moves every pin even with zero rule changes** — a whole class of legitimate movement, noted in the fixture's pin history.

⚠️ **Do not fold a full match into `DeterminismProbe`.** The Phase 0 APK on the test phone has the old hash compiled in and would report a false MISMATCH.

---

## Relay and online traps

- ⚠️ **The Live socket is a doorbell, not a delivery van.** It carries only "round N is ready", so the polling path stays the truth and a dropped socket costs latency only. Notices come from a **watcher over the store**, not from the endpoints, so no call site can forget to ring.
- ⚠️ **`INudgeSender` returns a four-value `Delivery`** (Sent / Deferred / Dropped / Unregistered), not a bool: an outbox that retries a dead phone spins forever, and one that gives up on a busy service loses the round. **Firebase's own `errorCode` in the body beats the HTTP status**, because a 400 is both a bad message and a bad token.
- ⚠️ **The age rule is deliberately written down TWICE** — `Allowed.Matchmaking` exists in both `Relay.Api` and `Molehill.Online`. The relay's is the gate; the client's decides whether the button is offered. **Do NOT "fix" the duplication by sharing a library:** that would give the relay a reference to the game, which is what stops it ever learning what a plan is.
- ⚠️ **Parental approval is a SIGNED GRANT, not a flag.** ECDSA P-256 over `base64url(payload).base64url(sig)`, verified against per-platform PEM keys, with the account inside the signature so a grant cannot be passed around, and one-hour freshness both directions. **No keys configured means nothing can ever be approved, which is how it ships.** All refusals return the same 403 deliberately.
- **Email link is Adults only, refused server-side**, and an approval does not buy an address. Rate limited, with expiry and a guess cap. **SMTP, not a vendor API — which is why it is genuinely tested**: the suite stands a real SMTP server up in-process and reads the bytes.
- ⚠️ **Ids that appear in a URL must be Base64Url, not base64.** A `/` in a ticket id does not fail the request, it **fails to match the route at all** and returns an empty 404. Seat tokens stay plain base64 because they travel in headers.
- ⚠️ **Config read timing differs on purpose:** Firebase and SMTP are read off `builder.Configuration` because they decide which service to register (so they must precede `builder.Build()`); **approval keys are read off `app.Configuration` AFTER the build, or a host's injected config is invisible.** A test caught this.
- **A matchmade lobby is opened through the same `Open`/`Join` a host uses**, so nothing downstream can tell how its players met.

---

## Godot, Android and Play

Godot 4.7.2 mono is a portable extraction (nothing installed system-wide). ⚠️ **No Android SDK is installed** — it is borrowed from Unity's `PlaybackEngines\AndroidPlayer`. `tools/scripts/deploy-android.sh` does the whole USB deploy in one command.

- ⚠️ **Godot 4.7.2's Android export template accepts `net9.0` ONLY**, though the editor's own assemblies are net8.0. The client uses net9.0 + `RollForward=LatestMajor`.
- ⚠️⚠️ **Godot needs a classic `.sln` beside the client csproj, and .NET 10 creates `.slnx`.** Without it the export **silently** produces a ~28 MB APK **with no C# in it at all** — it warns, signs it, and looks like success. **Verify with `unzip -l app.apk | grep MoleSim`.**
- Android export also requires `textures/vram_compression/import_etc2_astc=true` in `project.godot`.
- ⚠️ Godot itself warns .NET Android export is **experimental** in 4.7 — a live risk to the engine choice.

### Verifying UI without a device

`godot --path client -- --probe-screenshot out.png` renders and quits. ⚠️ **Godot does NOT rebuild C# from the command line**, so `dotnet build` first or you debug a stale assembly. ⚠️ **A render check cannot catch the planning screen by default** — `--demo` plans a whole turn inside one frame and `AutoPause` is 0.35 s, so at `--fixed-fps 30` the planning beat is about ten frames. Raise `AutoPause` to ~3.0 temporarily; **raising `--fixed-fps` alone does not help**, because it does not change how long the beat lasts in game time.

⚠️ **Never render or measure with a minimised or hidden window.** A minimised run draws **nothing** and still reports a full set of timings: one gave a flat 6.9 ms a frame, zero draw calls, and printed PASS. The probe now refuses a run with no draw calls. Put the window on the laptop panel (`--panel`, which `--perf` implies), never on an external monitor and never off-screen.

### Performance

- ⚠️ **The noise floor is about 25%** — three runs of one configuration ranged 456-617 fps. Use `-Repeat 3` and read the spread, not the average. A single run of high vs low "showed" low was 10% slower, which is backwards.
- **The machine is CPU-bound, not fragment-bound**: identical frame rate at 720p and 1080p. So there was nothing to tune, which is itself the answer to "make it run better on low-spec".
- ⚠️ **`--perf` turns vsync off** and lifts the cap, so the frame rates in `docs/perf.md` are headroom, not anything a player sees.
- ⚠️ **Renders are NOT frame-for-frame deterministic run to run** (~1% of pixels differ by frame 90), so an A/B of rendered frames must compare early frames or accept that floor.
- Godot's own `TIME_PROCESS` monitor is smoothed and misreports badly; the scene and world views time themselves instead. Per-viewport `ViewportGetMeasuredRenderTimeCpu/Gpu` is the instrument that can see the shader.

### Google Play

- ⚠️ **A new personal Play account cannot reach production until 12 testers have stayed opted in to a closed test for 14 consecutive days.** That is calendar time, so **open the closed track early rather than when the game is ready.** Account type is chosen at signup and cannot be changed.
- ⚠️ **The first bundle for a new app must be uploaded through the console by hand**; the API only works afterwards.
- **Version code comes from the workflow run number** — a tag cannot be it, because 0.7.10 sorts below 0.7.9.
- The release workflow is inert until four secrets exist (`PLAY_UPLOAD_KEYSTORE` base64 `-w0`, `PLAY_UPLOAD_KEY_ALIAS`, `PLAY_UPLOAD_KEY_PASSWORD`, `PLAY_SERVICE_ACCOUNT`). Godot reads the keystore path and credentials from the environment, which is how the key stays out of the repo.
- **Play's Android vitals gives crash reporting for free once on the store**, which answers the plan's crash-reporting task with no SDK and no second privacy disclosure.
- Still outstanding: the developer account itself, Play App Signing enrolment, the service account, IARC, data safety, target audience under the Families policy, and **a privacy policy at a public URL**.

---

## Environment notes

- ⚠️ **Docker Desktop on the work machine is broken** — it starts, the WSL `docker-desktop` distro stays Stopped, and it puts an always-on-top error dialog on screen. The relay Dockerfile is committed **with its README saying plainly it has never been built**, which is that README's own rule: an unverified deployment file is worse than an absent one.
- ⚠️ **There is no ffmpeg on the work machine**, and GitHub's Ubuntu image carried none either — **so the clip encoder tests had been skipping on every runner and a green suite had never run the encoder.** ffmpeg is now installed on CI.
- ⚠️ **`client/src/Game/MatchHud.cs` is LF in the working tree** while every other source file is CRLF, so a scripted find/replace that builds its search string with `\r\n` **silently matches nothing there**. Check a file's endings before any scripted edit.

### ⚠️ Pushing from the work machine needs the personal token explicitly

The active `gh` account there is an Enterprise Managed User and **cannot create or push to public repos** (`GraphQL: Public repositories are not permitted for Enterprise Managed Users`). **Do not `gh auth switch`** — that changes the global default. Git has **both** `manager` and the gh helper configured, so the helper list must be reset or the credential manager answers first with the work account:

```bash
TOKEN=$(gh auth token -u Inthrall)
GH_TOKEN="$TOKEN" gh repo create ...
GH_TOKEN="$TOKEN" git -c credential.helper= \
  -c credential.helper="!'C:\Program Files\GitHub CLI\gh.exe' auth git-credential" push
```

The repo-local git identity is pinned to `Inthrall <18254357+Inthrall@users.noreply.github.com>` so the work email stays out of public history.
