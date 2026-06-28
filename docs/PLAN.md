# Kingdom Death: Monster — Remote Play Dashboard
## Re-Scoped Architecture & Implementation Plan (Revision 3)

*Lead-architect planning document. Plan only — no code. Supersedes all prior revisions that integrated with Scribe at runtime or used a Cloudflare-relay / Tauri-host design.*

---

## 0. Revision 3.1 — Physical-play & content refinements (latest decisions; authoritative)

These four decisions were made after the body below was drafted and **override** it where they conflict (noted inline in §4.3, §4.4, §4.6):

1. **Spirit of physical play is the guiding principle.** The app is a **shared state tracker**, not a simulator. It augments the tabletop; it does not replace tactile/social play.
2. **Dice are physical and player-entered.** The app is **not** the randomness authority for dice. It **computes and displays target numbers** ("hits on 6+, wounds on 7+") from gear + monster stats; players **roll their own physical dice** and enter the result. Entry supports **both** modes — the **literal roll** (so the app can do hit/wound/crit math) *or* just the **outcome** (hit/miss/crit; faster, more tactile). An optional in-app convenience roller can come **later**. *(Overrides §4.3 `RollToHit`/`WoundRoll`; removes the `'dice'` RNG substream in §4.4.)*
3. **Decks are fully DIGITAL.** The app builds, **seeded-shuffles**, draws, discards, and tracks the AI and Hit-Location decks (physical deck handling was the clunky part). **App-owned randomness (shuffles, hunt-event order) stays seeded and auditable** per §4.4; only *dice* move to the players.
4. **Content scope = "match Scribe."** The app/content pack **may ship** card **names + mechanical data** (gear stats, monster stat lines, deck-build counts) — the same class of data Scribe already includes — to power the digital decks and the "hits on" helper. It must **never ship, commit, or persist in any shared store** card **images/art** or verbatim **effect prose**; players add their **own card images locally**, kept **session-local and ephemeral**. *(Relaxes §4.6's "ship zero content / no numbers" stance.)*

---

## 1. Scope & What Changed

This project is a **personal-use, self-hosted web app to actually PLAY Kingdom Death: Monster (KDM) remotely** with ~4 friends. It is the **play dashboard / game engine**: it runs turn-to-turn gameplay — the campaign phase/turn flow and especially the **SHOWDOWN (monster combat) loop** — and syncs it live to all players over a real-time channel.

**What changed from prior revisions (these are now FIXED):**

- **Scribe integration is DEFERRED.** Prior research about Scribe's TCP/55666 protocol, the Cloudflare-relay idea, the Tauri sidecar host, and the WireGuard/Tailscale "make-it-same-LAN" bridge are all **superseded as runtime architecture**. They survive only as reference for a *future, optional, read-only* one-time import feature. The app does **not** drive Scribe and is **not** built around it.
- **The app is NOT a survivor/settlement bookkeeping tool.** Players keep their durable survivor & settlement sheets in their existing tracker (Scribe / Black Ledger). This app keeps its **own thin, combat-relevant survivor records** (entered once for the MVP).
- **The stack is fixed and .NET-centric** to minimize new external dependencies:
  - **Backend:** ASP.NET Core (**.NET 10 LTS**) with **SignalR** as the authoritative real-time server; SignalR groups = rooms. Dev orchestration via **.NET Aspire 13.4.6+**.
  - **Engine:** the KDM play logic (phase/turn machine, showdown engine, AI deck, hit-location deck, monster stats, initiative, seeded RNG) is a **clean, transport-free, unit-testable C# domain library**, separate from the hub/transport.
  - **Frontend:** **Vite + Svelte 5** SPA using the **`@microsoft/signalr`** JS client. The client is **thin**: render state, send intents.
  - **Persistence:** **SQLite + EF Core**, single-instance default.
- **Future-client requirement (design-for, don't build):** the backend exposes a **clean, versioned hub/DTO contract** (intents + state deltas) so a future three.js / Godot / Unity client is "just another consumer." The Svelte web app is the **first** client.

**IP boundary (non-negotiable):** model only **mechanical** state — positions, counters, deck *order*, references by ID — and **never** reproduce KDM card/AI/event TEXT or stat tables. Players read effect text off their own physical cards.

---

## 2. Executive Summary

Build a **single-process, single-instance ASP.NET Core (.NET 10) application** that is the entire app: it hosts one versioned SignalR hub (`/hub/v1`), an in-process **authoritative KDM play engine** (a pure C# domain library), a **singleton room registry**, an **EF Core + SQLite** store, and serves the pre-built **Svelte** SPA from `wwwroot` (same-origin — no CORS). Authoritative state lives in a **singleton registry**; each room serializes intents through a **per-room `System.Threading.Channels` actor** (single-writer, no locks on the hot path, no CRDT needed at four players). All gameplay randomness flows through a **seeded, named-substream PRNG** producing an auditable, replayable event journal. The wire protocol is a **versioned `{contractVersion, seq, type, payload}` envelope**: one full **Snapshot** on join/resync, then strictly increasing **Deltas**, with a **seq-gap → RequestSnapshot resync** rule that doubles as the reconnection mechanism.

This is the **"Lean Game-Night MVP"** approach (Candidate 1) as the build target, executed with the **hexagonal discipline** of Candidate 3 (engine + contract as the durable, replaceable-adapter core) so that the **same container** later drops into the **k8s-behind-Cloudflare-Tunnel** deployment of Candidate 2 with **no app rewrite**. The three lenses are not alternatives — they are the same system at three maturity stages. We build Lean, structure it Hexagonally, and deploy it Full-Time when ready.

Auth gates **room creation** only; the lowest-effort recommendation is **Cloudflare Access** in front (existing infra) with a service-token path for the WebSocket, and an **app-level host-secret** as the always-available fallback. Guest **join** stays lightweight: room code + display name.

---

## 3. Architecture

### 3.1 Component diagram (target MVP)

```
                          ┌──────────────────────────────────────────────────────────┐
   Players' browsers      │   ASP.NET Core (.NET 10)  — ONE process, ONE container    │
   ┌───────────────┐      │                                                            │
   │ Svelte SPA #1 │woss  │   ┌────────────────────────────────────────────────────┐ │
   │ @microsoft/   ├──────┼──▶│  GameHubV1  (transient, STATELESS)                   │ │
   │  signalr      │      │   │  CreateRoom / JoinRoom / SubmitIntent /              │ │
   └───────────────┘      │   │  RequestSnapshot  ── callbacks ▶ Snapshot, Delta,    │ │
   ┌───────────────┐      │   │                                   RoomError          │ │
   │ Svelte SPA #2 ├──────┼──▶└──────────────┬─────────────────────────────────────-┘ │
   └───────────────┘      │                  │ forwards intent + connection ctx         │
   ┌───────────────┐      │                  ▼                                          │
   │ Svelte SPA #3 ├──────┼──▶┌────────────────────────────────────────────────────┐ │
   └───────────────┘      │   │  GameRoomService  (SINGLETON registry)              │ │
   ┌───────────────┐      │   │  ConcurrentDictionary<roomId, RoomActor>            │ │
   │ Svelte SPA #4 ├──────┼──▶│  RoomActor: Channel<Intent> → single consumer task  │ │
   └───────────────┘      │   │   • connectionId ⇄ stable playerId map              │ │
                          │   │   • authoritative roster (re-adds groups on reconn) │ │
                          │   └──────────────┬──────────────────────┬──────────────┘ │
                          │                  │ Reduce(state,cmd,rng) │ snapshot        │
                          │                  ▼                       ▼                 │
                          │   ┌───────────────────────────┐  ┌────────────────────┐   │
                          │   │ KDM.Engine (PURE library) │  │ KDM.Persistence    │   │
                          │   │ Campaign FSM + Showdown   │  │ EF Core + SQLite    │  │
                          │   │ sub-FSM, seeded PRNG,     │  │ (WAL) snapshots +   │  │
                          │   │ EventJournal, NO content  │  │ RNG cursors + roster│  │
                          │   └──────────────┬────────────┘  └────────────────────┘   │
                          │                  │ references IDs only                     │
                          │                  ▼                                         │
                          │   ┌───────────────────────────┐  ┌────────────────────┐  │
                          │   │ KDM.Contracts (DTOs)      │  │ wwwroot/ (Svelte    │  │
                          │   │ intents, delta envelope   │  │ static build)       │  │
                          │   │ → generates @kdm/contract │  └────────────────────┘  │
                          │   └───────────────────────────┘                          │
                          └──────────────────────────────────────────────────────────┘
        Dev only: Aspire AppHost orchestrates server + Vite dev server + OpenTelemetry dashboard.
        Content (KDM mechanical metadata) loaded from a USER-SUPPLIED pack, never shipped.
```

### 3.2 SignalR hub design (rooms = groups)

- **One hub, route-versioned: `/hub/v1` (`GameHubV1`).** Hubs in SignalR are **transient — a new instance per method invocation** — so the hub holds **NO state**. It only: reads connection context (room id, stable player id/token), authenticates/identifies the caller, forwards the intent to the singleton, and (on join) adds the connection to its SignalR Group.
- **Server methods (intents in):** `CreateRoom`, `JoinRoom`, `SubmitIntent`, `RequestSnapshot`. `invoke()` (Promise/ack) for anything needing a result; `send()` (fire-and-forget) for the rest.
- **Client callbacks (state out):** `StateSnapshot`, `StateDelta`, `RoomError`.
- **Rooms = SignalR Groups:** `Groups.AddToGroupAsync` on join; `Clients.Group(roomId).SendAsync(...)` for delta fan-out.
- **CRITICAL caveat (MS docs):** group membership is **in-memory and is NOT restored** after a server restart or a reconnect. Therefore the **authoritative roster lives in the `RoomActor`**, not in SignalR group state, and the `OnConnected`/reconnect path **re-adds** the connection to its group and pushes a fresh snapshot.

### 3.3 Where authoritative state lives & how intents are serialized

- Authoritative play state lives **entirely in the singleton `GameRoomService`**: a `ConcurrentDictionary<roomId, RoomActor>`. Each `RoomActor` owns one `Campaign` aggregate (engine state), the player roster, the `connectionId → playerId` map, and a **`System.Threading.Channels` single-consumer queue**.
- **Every intent is enqueued and processed one-at-a-time** by that room's single consumer task. This gives **ordered, single-threaded mutation** of one room's state **without blocking other rooms** and **without any locks on the hot path** — the textbook single-writer fit for an authoritative turn-based game. (No CRDT: at four players, conflict-free merge solves a problem we don't have and would risk silently-illegal game states.)
- The consumer calls `engine.Reduce(state, command, rngStream) → (state', events)`, assigns each event a **monotonically increasing per-room `seq`**, **broadcasts the deltas first**, then **persists the snapshot asynchronously off the hot path** so a slow SQLite write never stalls the room.

**Decision — intent serialization mechanism (recommend-with-options):**

| Option | Verdict |
|---|---|
| **`System.Threading.Channels` per-room actor** | **RECOMMENDED.** In-box (zero new dep), ordered, await-friendly, single-consumer. Honors "minimize new dependencies." |
| Per-room `SemaphoreSlim`/lock | Acceptable but inferior: awaiting EF/SQLite inside the critical section can serialize the whole room. |
| Akka.NET actor per room | Correct but adds a heavyweight external dependency for no benefit at this scale. |

### 3.4 Room / session lifecycle

1. **Create** — host authenticates (host secret or CF Access), calls `CreateRoom`; service mints a `roomId` + short `roomCode`, instantiates a `RoomActor`, persists an initial snapshot.
2. **Join** — guest calls `JoinRoom(roomCode, displayName)`; service issues a **stable `playerId` + player token**, adds the connection to the group, sends a `StateSnapshot(seq=N)`.
3. **Play** — players send intents; the room actor reduces and broadcasts deltas (`seq=N+1, N+2, …`).
4. **Reconnect** — client re-establishes the socket (new volatile `connectionId`), re-identifies via the **stable playerId/token**, is re-added to the group, and **always resyncs** with a fresh snapshot.
5. **Suspend/resume** — on graceful shutdown (SIGTERM) the actor snapshots; on (re)start `GameRoomService` rehydrates active rooms from the latest SQLite snapshot.
6. **End** — campaign reaches `Ended(Victory|Defeat)`; final snapshot retained for resume/audit.

### 3.5 Reconnection + snapshot/seq/delta resync

This is the **load-bearing resilience primitive**, defined **above** the transport so it is independent of SignalR quirks:

- **Envelope:** `{ contractVersion, seq, type, payload }`.
- **Join/resync:** server sends **exactly one** `FullSnapshot(seq=N)`, then `Delta`s with **strictly increasing** seq.
- **Client rule:** track `lastSeq`; if an incoming delta's `seq != lastSeq+1`, **invoke `RequestSnapshot`** and **discard deltas** until the snapshot lands. On **every** reconnect, **always resync** (the connection looks entirely new to the server).
- **Transport hardening (belt-and-suspenders):**
  - Client `.withAutomaticReconnect(longPolicy)` — note the default policy **gives up after ~42s** (retries at 0/2/10/30s), far too short for a multi-hour game night; supply a custom `IRetryPolicy` with long/indefinite backoff.
  - Auto-reconnect **does not retry the initial `start()`** — wrap `start()` in your own retry loop.
  - Optionally enable **.NET 10 stateful reconnect** (`AllowStatefulReconnects` server-side + `withStatefulReconnect` client-side) to buffer/replay across brief drops — a *convenience*, not the source of truth.
  - Use the **Web Locks API** (`navigator.locks.request`) to keep the socket alive against background-tab sleeping during long sessions.

### 3.6 Why this recommendation — grafting the three candidates

| Lens | What we take | What we defer |
|---|---|---|
| **C1 — Lean Game-Night MVP** | The **build target**: one process, one image, one store, same-origin SPA, host-secret auth, single-instance by design. Fastest path to a real playable night. | Nothing — this is the spine. |
| **C2 — Always-On k8s/Cloudflare** | The **deploy story** (PVC-backed SQLite, CF Tunnel/Access, health/observability, documented Redis+Postgres scale path) and the **durability cadence** for weeks-long campaigns. | k8s/CF wiring is **Phase 3**, not the MVP critical path. |
| **C3 — Contract-First Hexagonal Core** | The **internal discipline**: engine + contract as the durable assets; SignalR, EF/SQLite, content-pack, and host are **replaceable adapters**. Enforced IP boundary. | The full Engine↔wire mapping ceremony is justified *only* by the multi-client goal — keep it lean but present from day one. |

The single most important cross-cutting decision they all share: **transport-free deterministic engine + versioned snapshot/delta/intent contract + containerized config-driven deploy.** Those three cost little now and unlock everything later.

---

## 4. The KDM Play-Engine Domain Model

The engine is a **pure, transport-free C# library** (`KDM.Engine`) with **no SignalR/EF references**, **100% unit-testable** — deterministic seed in ⇒ deterministic events out. Core API:

```
IReadOnlyState Reduce(State state, Command cmd, IRngStream rng)
    → (State next, IReadOnlyList<Event> events)
```

One **single-writer instance per room**; one **monotonic `seq`** per emitted event; a `Snapshot = { serializedAggregate, lastSeq, rngCursors }`.

**Legend:** `(A)` = app-owned mutable state · `(R)` = reference-by-id into a user-supplied content pack · `(T)` = thin imported/entered copy.

### 4.1 Outer machine — Campaign FSM

`Campaign` (aggregate root) `(A)`:
- `Id`, `Name`, `contentPackVersion`, `editionId (R)`
- `Phase` enum `{ Setup, Hunt, Showdown, Settlement, Ended }`
- `LanternYear` (counter)
- `settlementId`, `survivorRosterIds[]`
- `masterSeed`, `rngCursors`

Transitions are **explicit guarded commands** (illegal transitions rejected by the reducer, never the client):
- `Settlement.Begin` → increments `LanternYear` (fills next timeline space).
- → `Hunt` (inputs: quarry id `(R)` + monster-level id `(R)`, departing survivor ids, starting survival).
- → `Showdown` (auto-triggered when the party token reaches the showdown space, or on ambush).
- → back to `Settlement` on showdown end.
- Terminal guards: `population == 0` ⇒ `Ended(Defeat)`; finale monster defeated ⇒ `Ended(Victory)`.

### 4.2 Hunt sub-state `(A)` (light — mostly prompt/record)

```
HuntState {
  quarryId(R), monsterLevelId(R),
  board: HuntSpace[], partyIndex, monsterIndex,
  huntEventDeckOrder: id[],
  resolvedEvents: { spaceIndex, eventId(R), choiceId, rolls }[],
  startingSurvival, ambush: bool
}
```
Each step: `AdvanceParty` → draw top hunt-event id → present prompt (**text lives on the physical card**; engine references the id only) → controller enters chosen branch + dice outcomes (**rolled on the player's own physical dice and entered** — see §0/Rev 3.1). Showdown triggers when `partyIndex` meets `monsterIndex` / a showdown space is reached.

### 4.3 Showdown sub-FSM `(A)` — the engineering centerpiece

**Round structure:** `Round = SurvivorsTurn → MonsterTurn`.
- ⚠️ **Make round order a DATA/CONFIG value.** Rulebook canon is survivors-first, but one source said monster-first — **verify against the physical rulebook** and keep it configurable.
- At `Round.Begin`: rotate the **monster-controller** seat; **reset** per-survivor flags. If the controller's own survivor is later targeted, apply an automatic **+1 insanity** effect.
- **SurvivorsTurn:** each survivor gets exactly **1 Movement + 1 Activation** in any order; tracked by `movementUsed`/`activationUsed`.
- **MonsterTurn:** draw top AI card → resolve; if AI draw pile empty, seeded-reshuffle discard; if **both** draw and discard empty, perform **Basic Action**.
- **End:** monster reaches required wounds/brain-trauma ⇒ Victory; no conscious/alive survivors ⇒ Defeat.

**Attack mini-FSM (explicit):**
`DeclareAttack(survivorId, weaponId, targetMonsterId)`
→ `EnterHits` — the app **displays the derived hit number** ("hits on X+", from weapon **Speed**/**Accuracy** + monster **Evasion**); the player **rolls their own physical dice** and enters the result (literal rolls *or* a hit count — see §0/Rev 3.1)
→ `DrawHitLocations` (= number of hits; the app **draws from the digital Hit-Location deck**; **a `Trap`-type card ends the attack immediately**)
→ `EnterWound` per location in player-chosen order — the app **displays the wound target** ("wounds on Y+", from STR + weapon vs monster **Toughness**); the player rolls physically and enters the result; natural-10 *lantern* = critical **only** if the location has a crit slot, and a crit cancels reactions
→ apply to `woundsByLocation`; on lethal threshold ⇒ Victory.

**State aggregates:**

```
AiDeckState {                 // never card text — ids + flags only
  drawPile: cardId[], discardPile: cardId[],
  resolving: cardId?, inPlay: cardId[]   // moods/persistent
}

HitLocationDeckState {
  drawPile: cardId[], discardPile: cardId[],
  drawnThisAttack: cardId[],
  woundedLocations: { cardId → { wounded, critical } }
}

MonsterState {
  baseStats { Movement, Toughness, Speed, Accuracy, Damage, Luck, Evasion },  // mechanical stat line — MAY ship (Scribe-aligned, see §0); powers "hits on X+". No card art.
  modifiers: { source(cardId/woundId), stat, delta, durationScope }[],         // scope: this-attack | this-round | showdown | permanent (append-only, deterministic expiry)
  knockedDown, position (grid cells; NxN footprint by size), facing?,
  woundsByLocation, persistentInjuries: locationId[], traitsInPlay: cardId[], counters: { name → int }
}

SurvivorCombatState {
  survivorId, gridPosition (x,y), knockedDown,
  movementUsed, activationUsed,
  survivalActionsUsedThisRound: Set<actionId>,
  tokens: id[], isTarget, dead
}

Board { width, height, occupancy map, monster footprint }
```

**Survival actions** are **data-driven** `{ actionId, gatingInnovationId(R), oncePerRound:true }`. Base set: Dodge, Encourage (needs Silent Dialect), Dash (needs Paint), Surge (needs Inner Lantern), Endure; expansions add Overcharge/Embolden. `UseSurvivalAction` guards: survivor not blocked, gating innovation present in the `ThinSettlement` snapshot, survival points > 0 (capped by Survival Limit), and action not already used this round. All per-round flags reset at `Round.Begin`. Knocked-down survivors must spend a movement/activation to stand (or be Encouraged) and cannot dodge.

### 4.4 Seeded, auditable RNG (hard requirement)

- **Do NOT use `System.Random`** (not stable across runtimes/versions).
- Use a **counter-based / splittable PRNG** — e.g. **SplitMix64 → xoshiro256\*\*** (or a PCG variant) — with a **per-showdown master seed stored in state**.
- Derive **named substreams** by hashing `masterSeed + label` — for **app-owned randomness only**: `'ai-shuffle'`, `'hitloc-shuffle'`, `'hunt-events'`, `'severe-injury'`. (**No `'dice'` substream — dice are physical and player-entered**, see §0.) This way, adding a feature that consumes randomness in one area **never shifts another area's sequence**.
- Persist `{ masterSeed, perSubstreamCursor }` in **every snapshot**. Every app-generated **shuffle/draw** appends `{ substream, cursorBefore, raw, derivedResult }`, and **every player-entered dice outcome** appends `{ source:'player-dice', survivorId, entered }`, to the **EventJournal** — giving identical client rendering, full auditability, and exact replay. Shuffles are **seeded Fisher-Yates** so deck ORDER is reproducible.

### 4.5 Thin MVP survivor roster

```
SurvivorRecord (T):  Id, name, status (alive/dead),
  attributes { MOV, ACC, STR, EVA, LCK, SPD },
  survivalPoints, insanity, courage, understanding,
  weaponProficiency { typeId(R), level },
  gearGrid: { slot, gearId(R) }[],
  fightingArtIds(R), disorderIds(R), severeInjuries,
  armorPointsByLocation { head, arms, body, waist, legs }

ThinSettlement (T):  survivalLimit,
  unlockedSurvivalActionIds(R), combatGatingInnovationIds(R),
  milestonesReached
```

Entered/imported **once** for the MVP. This is the slot where the **deferred read-only Scribe / Black Ledger import** would later seed data — off the critical path.

### 4.6 IP boundary (reaffirmed, enforce in CI)

**Scope = "match Scribe" (see §0/Rev 3.1).** The repo and a shipped **content pack** MAY contain the same class of data Scribe already includes — card **names/IDs**, **deck-build composition/counts**, and **mechanical numeric data** (gear Speed/Accuracy/Strength, monster stat lines, hit numbers). The engine manipulates:
1. **stable IDs** + names,
2. **mechanical metadata** per id — card `TYPE` enum, `isMood`, `hasCriticalSlot`, `oncePerRound`, gating-innovation id, per-level AI-build counts, and **numeric stats** (these power the "hits on X+" helper),
3. **app-owned positional/counter/deck-ORDER** state (grids, tokens, wounds, draw/discard sequences, RNG cursors).

The **withheld assets are card IMAGES/ART and verbatim card EFFECT PROSE** — these are **never shipped, committed, or placed in any shared/persisted store**. A player who wants to see a card face adds **their own image locally**; those stay **session-local and ephemeral** (not source-controlled, not redistributed). MVP stays **"manual-assist" for card *effects*** (not for stats): the engine prompts *"resolve AI card `<name>`"* and the player applies the effect off their own card, while the app tracks board/decks/RNG **and the mechanical numbers it ships**. Add a **CI check** that the repo/engine contains **no KDM card art and no verbatim effect prose** (mechanical names and numbers are allowed).

---

## 5. Client & Contract

### 5.1 Svelte client (thin consumer)

- **Svelte 5 (~5.55) + Vite + TypeScript.** Built once to static assets and served from the API's `wwwroot` via static-file middleware + `MapFallbackToFile` — **same-origin, so the hub is a RELATIVE URL** (no CORS, no credentialed-WS complexity for the MVP).
- A **single connection module** owns the `@microsoft/signalr` v10 `HubConnection`:
  - Build once: `new HubConnectionBuilder().withUrl('/hub/v1').withAutomaticReconnect(longPolicy).build()`.
  - **Register all `.on(...)` handlers BEFORE `start()`** (MS best practice — no early messages missed).
  - Wrap `start()` in its own retry loop.
  - Drive a `connectionState` store from `onreconnecting` / `onreconnected` / `onclose` to disable inputs and show status during a multi-hour night. (Note: `connectionId` changes per connect and is `undefined` if `skipNegotiation` is set — **never use it for identity**; use the stable `playerId`.)
- **State flow:** one `board` store holds the authoritative snapshot; a **pure, unit-testable reducer** applies each delta keyed on `seq` (mirroring the engine's delta semantics).
- **Optimism: minimal** — only local hover/selection. KDM is authoritative and turn-based with high latency tolerance; server-confirmed state beats rollback complexity.

### 5.2 The versioned contract (the future-client path)

- **Envelope:** `{ contractVersion, seq, type, payload }`. One `FullSnapshot(seq=N)` on join/resync; then strictly increasing `Delta`s; seq-gap → `RequestSnapshot`.
- **Intents are a closed, named set** carrying only IDs/coords/counters — **never KDM text**: `CreateRoom`, `JoinRoom`, `AdvancePhase`, `DrawAiCard`, `DeclareAttack`, `RollHitLocation`, `UseSurvivalAction`, `MoveToken`, `MoveMonster`, `EndTurn`, …
- **Both the hub route (`/hub/v1`) and `contractVersion` are versioned** so old clients fail fast / negotiate.

### 5.3 C# → TS type relationship (automate from day one)

- The **canonical contract lives in a dedicated C# `KDM.Contracts` assembly** — the single source of truth, shared by server and future **.NET** clients (referenced directly).
- **TS types are GENERATED** from `KDM.Contracts` into a standalone **`@kdm/contract`** package consumed by Svelte. Hand-mirroring drifts and silently desyncs — **automate in CI**.

**Decision — TS generation toolchain (recommend-with-options):**

| Option | Notes |
|---|---|
| **NSwag (via OpenAPI schema)** | Full client+types; strong if you also expose REST. **Recommended primary.** |
| `openapi-typescript` | Types-only, lightweight, good if you only need interfaces. |
| **TypeGen** | Attribute/convention-driven, **no OpenAPI needed** — attractive for a SignalR-first contract where most DTOs are hub messages, not controller responses. **Strong alternative.** |

### 5.4 Future 3D clients — "just another consumer"

- **three.js** — runs in-browser, reuses the **exact same `@microsoft/signalr` JS client** and the same generated `@kdm/contract`. Genuinely just-another-consumer: swap the render layer (DOM → WebGL), keep the connection module, store reducer, and intents.
- **Godot (C#/mono)** & **Unity** — use the official **`Microsoft.AspNetCore.SignalR.Client`** (.NET Standard 2.0/2.1) and can reference the **`KDM.Contracts` C# assembly directly** (no TS generation). ⚠️ **Spike before committing:** the Godot SignalR sample is community/unofficial and may be stale against current Godot .NET; Unity isn't NuGet-native (drop DLLs in `Assets/Plugins`), historically lags supported client versions, and Unity WebGL needs a WebGL-specific SignalR shim. The "3D = just another consumer" promise is **clean today for three.js**; Godot/Unity need a small validation spike.

---

## 6. Persistence

- **SQLite + EF Core (WAL mode)**, single-instance default — **zero new external dependency**, matches the constraint.
- **Persist only what is needed to RESUME between game nights and recover from a process restart:**
  - the serialized `Campaign`/`Showdown`/`Hunt` snapshot,
  - `lastSeq`,
  - RNG `masterSeed` + per-substream cursors,
  - deck **ORDER** as ID sequences (**never card text**),
  - thin survivor roster + `ThinSettlement`,
  - room/roster membership.
- **Cadence:** snapshot **after each committed intent** (cheap at four players, near-lossless mid-showdown crash recovery) **plus** a guaranteed snapshot **on graceful shutdown** (SIGTERM handler). For the always-on mode this same cadence survives pod restarts across weeks.
- **Concurrency:** route **all writes through the per-room consumer task** so SQLite's single-writer model never contends across rooms; keep the actual write **off the broadcast hot path**. WAL + a handful of rooms ⇒ contention is a non-issue. Sustained write contention is the early signal toward Postgres.
- **Optional:** persist the full **EventJournal** per-room for replay/audit. MVP only requires **latest-snapshot** durability.
- **Scale path (documented, NOT adopted):** **Postgres** (shared state) + **Redis SignalR backplane** + **sticky sessions** — only if the app ever runs **more than one replica**.

---

## 7. Deployment & Auth

The **same deployment-agnostic container image** runs both modes; all config via env vars (host secret, SQLite path on a mounted volume, allowed origins, contract/hub version).

### 7.1 Ephemeral game-night mode

- Run the container (or `dotnet run` via the Aspire AppHost in dev) on the host's machine.
- Reach friends via:
  - **(a) Microsoft Dev Tunnels** — `devtunnel create` first for a **PERSISTENT URL** that survives restarts (auto-deleted after 30 days inactivity), then `devtunnel host`; supports multi-port + **anonymous access** for the night.
  - **(b) Tailscale / WireGuard / LAN.**
- SignalR WebSockets work fine over Dev Tunnels and LAN.

### 7.2 Full-time mode — k8s behind existing Cloudflare Tunnel

- Deploy the **same image** to the user's existing Kubernetes as a **single replica** with a **PersistentVolumeClaim** for the SQLite file, plus liveness/readiness probes on `/health`. Expose through the **existing `cloudflared` tunnel**.
- **Deploy mechanism (recommend-with-options):** prefer a **plain container + your own Helm/manifests** to stay deployment-agnostic; Aspire 13.4's native **Kubernetes/Helm publisher** and the community **Aspir8** tool are alternatives. *Do not lock production deploy to Aspire's (newer, evolving) k8s publisher.*

### 7.3 Do SignalR WebSockets work through CF Tunnel / Access?

**Yes — with documented gotchas that must be validated end-to-end (not just a page load):**

- **Cloudflare Tunnel:** WebSockets must be **enabled account-wide**; SSL/TLS mode **Full/Flexible (not Off)**; set **Super Bot Fight Mode "definitely automated" to Allow** on the hub path; watch overlapping Worker routes. With these, SignalR WS **traverses `cloudflared`**.
- **Cloudflare Access (the classic "works on LAN, fails over tunnel" trap):** the Access **binding cookie `CF_Binding` is stripped at the edge and breaks SignalR WebSockets** ("WebSocket failed to connect"). Mitigations: **disable the binding cookie** on this app; put the `/hub/v1` WS path behind a **Service Auth / service-token policy** (or exclude `/hub` and authorize at the app via room token); keep **interactive IdP/PIN Access on the human-facing room-creation routes**. Browsers can't easily send service-token headers, so excluding `/hub` and gating it at the app layer is often the pragmatic choice.

### 7.4 Auth (gate ROOM CREATION) — recommend-with-options

Auth is needed at least to gate **room creation** (the app may be internet-reachable). Guest **join** stays lightweight (room code + display name + per-player token).

| Option | Effort | Recommendation |
|---|---|---|
| **Cloudflare Access in front** (existing infra) on the create-room route + **Service Auth token for the WS path** | Near-zero app code | **RECOMMENDED for the full-time/k8s mode** — leverages existing infra. |
| **App-level single host-secret** env var checked by the `CreateRoom` intent | Trivial | **RECOMMENDED fallback / ephemeral mode** — perfectly adequate for four friends; always available regardless of host. |
| **External OIDC** (the user's existing IdP) | Moderate | Clean *if* the user already runs an IdP. |
| **ASP.NET Core Identity** | High | Overkill for a personal tool. |

---

## 8. What Kinds of Services Are Genuinely Required vs Not

**Genuinely required at runtime:**
- **ONE ASP.NET Core process** — SignalR hub + game engine + room registry + static SPA host. *The only runtime service.*
- **SQLite** — an embedded file, **not a separate service** (on a PVC in k8s mode).
- **External/existing infra reused, not built** — Microsoft Dev Tunnels (ephemeral) **or** the user's existing Cloudflare Tunnel + Kubernetes (full-time).

**Development only (none ship to production):**
- Vite dev server, .NET Aspire AppHost, OpenTelemetry dashboard, the CI step for C#→TS contract generation.

**Explicitly NOT required:**
- **Redis backplane**, **Postgres**, any **message broker**, a **separate frontend host**, a **separate auth/IdP service** (unless OIDC is chosen), and — critically — **no Scribe bridge/proxy, no TCP-55666 client, no WireGuard/Tailscale runtime dependency, no Tauri host**. All of those are superseded.

---

## 9. Tech Stack Recommendation (with options)

Versions verified as current at the research date **2026-06-27**; re-verify exact patch versions at implementation time (Aspire and Cloudflare ship fast).

| Layer | Pinned choice (verified) | Options / notes |
|---|---|---|
| **Runtime** | **.NET 10** (LTS, GA 2025-11-11, supported to **Nov 2028**) | Use as backend target. |
| **Real-time** | **ASP.NET Core SignalR 10.0.x** (in the shared framework — no separate server NuGet) | **JSON protocol** (`AddJsonProtocol`) for the MVP — debuggable, only protocol the JS client supports. **MessagePack** is a future toggle for bandwidth/perf (unnecessary at ~5 players). |
| **Concurrency** | **`System.Threading.Channels`** per-room actor | In-box; avoids Akka.NET. |
| **Persistence** | **EF Core + SQLite (WAL)** | Scale path: Postgres + Redis backplane (not required). |
| **Engine** | Plain C# class library + **custom seeded PRNG** (xoshiro256\*\* / PCG via SplitMix64) | **Never `System.Random`.** |
| **Contract** | **`KDM.Contracts`** C# assembly → **`@kdm/contract`** TS (NSwag / openapi-typescript / TypeGen) | TypeGen is attractive for a SignalR-first (not REST-first) contract. |
| **Frontend** | **Svelte 5 (~5.55)** + **Vite** + **TypeScript** | (SvelteKit 2.57.x available; a plain Vite SPA served from `wwwroot` is simplest here.) |
| **JS client** | **`@microsoft/signalr` 10.0.0** | `@microsoft/signalr-protocol-msgpack` 10.0.0 (now depends on `@msgpack/msgpack`) if MessagePack is ever enabled. |
| **Dev orchestration** | **.NET Aspire 13.4.6** (2026-06-19; matches stated floor) | Dev-only: server + Vite + OpenTelemetry dashboard. Not a runtime dependency. |
| **SPA proxy (dev)** | `Microsoft.AspNetCore.SpaProxy` 10.0.9 **or** Vite `server.proxy` with `ws:true` | Either works for dev `/hub` proxying. |
| **Deploy** | Single Docker image + existing k8s/Helm + existing `cloudflared` | Dev Tunnels for ephemeral reach. |

**Unverified / to confirm at build time:** Godot+.NET SignalR support (community sample, possibly stale); Unity's currently-supported SignalR client version + WebGL shim; exact current Aspire/`cloudflared`/`@microsoft/signalr`/Svelte patch versions; Aspire's k8s-publisher maturity.

---

## 10. Phased Roadmap

Total: roughly **5–8 focused weeks** to a genuine first game night for the Lean MVP (10–16 weeks for the robust always-on, contract-first build). **Phase 0/1 is a PLAY-ENGINE walking skeleton — NOT a Scribe capture.**

### Phase 0 — Walking skeleton (~1 wk)
- **Goal:** one room, one trivial intent, live sync end-to-end, persisted.
- **Deliverables:** single process; `GameHubV1` with `CreateRoom`/`JoinRoom`/`SubmitIntent` + `StateSnapshot`/`StateDelta`; `GameRoomService` singleton with per-room `Channel`; a **trivial engine** (advance phase + one seeded dice roll); Svelte client that connects, renders, and sends one intent; SQLite snapshot; host-secret gate; **generated `@kdm/contract`** in CI from day one.
- **Risks:** getting the singleton/transient-hub split and the seq/snapshot contract right early (cheap to fix now, expensive later).

### Phase 1 — Playable showdown (~3–4 wk; the dominant cost)
- **Goal:** a real, fair, recoverable showdown for four players.
- **Deliverables:** the Showdown sub-FSM — initiative/controller rotation, AI deck draw by ORDER, hit-location draw, the **attack mini-FSM** (to-hit / wound / critical / trap), survival actions, knockdown, monster footprint/grid; **seeded substream RNG + EventJournal**; snapshot/delta `seq` + resync; reconnection roster rebind; scoped modifier stacks with deterministic expiry.
- **Risks:** showdown is the single largest, most mutable subsystem; determinism leaks; rule ambiguities (see §11).

### Phase 2 — Minimal loop + polish (~1–2 wk)
- **Goal:** a thin but complete Hunt → Showdown → Settlement cycle.
- **Deliverables:** thin Hunt and Settlement phases; thin survivor roster + `ThinSettlement` entry CRUD; content-pack loader (IDs + mechanical flags); contract versioning hardened; Dockerfile; Dev Tunnel run; end-to-end test **through Cloudflare Tunnel** (validate the actual WS upgrade).
- **Risks:** CF Access/WS binding-cookie trap; scope creep on the settlement phase.

### Phase 3 — Deploy hardening (always-on)
- **Goal:** full-time on k8s behind the existing CF Tunnel.
- **Deliverables:** k8s Deployment/Service/Ingress/PVC + health probes; CF Tunnel/Access wiring with Service-Auth token for `/hub`; per-intent snapshot durability validated across pod restarts; OpenTelemetry observability; SIGTERM snapshot.
- **Risks:** single-replica SPOF; Aspire→k8s publisher maturity (mitigate by deploying a plain container).

### Phase 4 — Optional future clients & optional import (not on critical path)
- **Goal:** prove "just another consumer"; optionally seed the thin roster.
- **Deliverables:** three.js client spike (reuses same JS client + `@kdm/contract`); Godot/Unity validation spikes; **optional read-only one-time importer** from Scribe's documented session dump or Black Ledger's JSON export into the thin `SurvivorRecord`/`ThinSettlement`; optional content-pack auto-resolution; optional MessagePack toggle.
- **Risks:** Godot/Unity client maturity; **the importer must remain read-only, one-time, and must NOT drive Scribe at runtime.**

---

## 11. Critical Risks & De-Risking

| Risk | Impact | De-risking |
|---|---|---|
| **Showdown complexity/scope** (grid + two ordered decks + rotating controller + per-round flags + scoped modifier stacks) — the biggest subsystem | Desync, unrecoverable rooms, blown timeline | Strict **single-writer reducer** + append-only EventJournal; model decks/actions/monsters as **data, not code**; TDD the engine offline with deterministic seeds before any UI. |
| **IP boundary** (KDM card/AI/event text & stat tables are IP) | Legal exposure | Engine ships **zero content**; IDs + mechanical flags only; **CI check** rejects KDM prose/numbers; content packs are user-supplied; MVP is **manual-assist**. |
| **SignalR state/concurrency correctness** (transient hubs, in-memory groups not restored) | Reconnecting players miss deltas; races on the showdown loop | Authoritative state/roster in the **singleton**; per-room **Channel** serialization; **re-add groups on reconnect**; identify by **stable playerId**, not connectionId. |
| **Reconnection / long sessions** (default auto-reconnect gives up ~42s; background-tab sleep drops sockets) | Players silently dropped mid-night | Custom **long `IRetryPolicy`**; wrap initial `start()`; **Web Locks** keep-alive; engine-level **seq+snapshot resync** as source of truth; optional .NET 10 stateful reconnect. |
| **Determinism fragility** (`System.Random`, `DateTime.Now`, dictionary order, parallelism) | Replay/audit/late-join silently diverge across clients | Route **all** randomness through seeded **named substreams**; explicit collection ordering; no parallelism in the reducer; log every roll/shuffle with its cursor. |
| **KDM rule ambiguities** (round order survivors-vs-monster-first; hit-location reshuffle timing; severe-injury overflow; per-monster AI-build counts) | Wrong mechanics, unfair play | Make round order + reshuffle policy **configurable data**; **verify against the physical rulebook PDF**, not wikis; AI-build counts come from the **user content pack**, never hardcoded. |
| **CF Tunnel/Access WebSockets** ("works on LAN, fails over tunnel") | Real-time play silently breaks | Enable WS account-wide; SSL Full/Flexible; **disable Access binding cookie**; **Service-Auth token for `/hub`**; **test the actual WS upgrade end-to-end**, not just a page load. |
| **Single-replica SPOF** (no HA / zero-downtime) | A deploy/crash drops live connections | Acceptable for a personal tool; fast restart + **snapshot-per-intent resume** mitigates. HA would force the Redis-backplane + sticky-sessions + Postgres jump — explicitly out of scope. |
| **Aspire→k8s publisher maturity** (13.4 features recent/evolving) | Lock-in / churn | Deploy a **plain, deployment-agnostic container**; treat Aspire as **dev-only**. |
| **Scope creep** (more monsters/expansions, full settlement phase, auto-resolution) | Never-ship | Hard-cut MVP to **one monster + minimal phase loop, manual-assist**; everything monster/card/action-specific is **data** so content grows without code changes. |
| **C#/TS contract drift** | Subtle client desync with no clear error | **Generate TS from `KDM.Contracts` in CI from day one**; `contractVersion` mismatch fails fast. |

---

## 12. Open Questions for the User

1. **Edition & expansions** — which KDM edition (1.5/1.6) and which expansions (Gambler's Chest, Sunstalker, …) must the MVP support? This sizes the data-driven content model (monsters, AI/HL deck builds, survival actions like Overcharge/Embolden, events).
2. **First monster** — which single monster's showdown is the Phase-1 target (likely the campaign's starting quarry)?
3. **Round order & reshuffle** — can you confirm, from your **physical rulebook**, the canonical showdown round order (survivors-first vs monster-first) and the exact hit-location-deck reshuffle/empty-deck rule? (We'll make both configurable, but need the default.)
4. **Auth posture for full-time mode** — is **Cloudflare Access + a Service-Auth token on `/hub`** acceptable, and do you already run an IdP behind Access (making OIDC "free")? If not, we default to the app-level host secret.
5. **Persistence/resume semantics** — is **per-intent** snapshotting (near-lossless mid-showdown resume) the goal, or is per-game-night durability enough? And do you want the **full EventJournal** persisted for replay/audit, or latest-snapshot-only?
6. **Crash resumability bar** — must an in-progress showdown be **fully resumable after a process/pod crash**, or is "restart and resync at the start of the current turn" acceptable?
7. **Content-pack source** — are you willing to author/enter a small **user-supplied content pack** (IDs + mechanical flags) for the MVP, or should the MVP stay purely manual-assist with players reading every value off physical cards?
8. **Future-client priority** — is **three.js** the realistic next client (cheapest; literally the same JS client), or do you specifically want Godot/Unity (each needs a validation spike)?
9. **Deferred import** — when/if we add the optional read-only importer, which is your source of record: **Scribe's session dump** or **Black Ledger's JSON export** (or both)?


---

## Appendix — Completeness Critique (adversarial review of Rev 3)

_Skeptical critic pass; gaps/risks not fully resolved in the body above. (Some are addressed by the §0 Rev 3.1 refinements.)_

### Gaps
- MONSTER-ATTACKS-SURVIVOR RESOLUTION IS ENTIRELY MISSING. The only attack FSM modeled is survivor->monster (DeclareAttack/RollToHit/DrawHitLocations/WoundRoll). The monster turn deals damage TO survivors via survivor hit locations, armor-by-location, wound->severe-injury/brain-trauma, knockback/grab/knockdown - none of it is in the model despite SurvivorRecord.armorPointsByLocation existing. This is ~half of showdown combat and the Phase-1 '3-4 week' estimate clearly does not account for it.
- GRID COMBAT GEOMETRY IS UNDERSPECIFIED. Board has only width/height/occupancy/footprint. There is no model for reach/range/adjacency, movement cost, line-of-sight, terrain tiles, monster movement toward a target, or blind spot. Facing is marked optional ('facing?') yet blind-spot is a core KDM mechanic. MoveMonster/MoveToken intents exist with no spatial rules behind them.
- ENFORCEMENT-vs-RECORD BOUNDARY IS UNDEFINED. The plan says 'illegal transitions are rejected by the reducer,' but in manual-assist most legality (range, valid targets, timing windows, card effects) depends on card TEXT the engine deliberately does not have. The plan never states what the engine actually validates versus what it merely records from human input. This is the central design ambiguity of the whole engine.
- REACTIONS / REACTIVE SURVIVAL ACTIONS ARE NOT MODELED AS INTERRUPTS. 'Dodge' cancels a hit during the monster's attack and 'a crit cancels reactions' is asserted, but the FSM is strictly Round->SurvivorsTurn->MonsterTurn with no reaction/interrupt window. Reactive timing is a real, hard part of KDM combat and is absent.
- SNAPSHOT SERIALIZATION VERSIONING IS ABSENT. Persistence stores 'serializedAggregate'. There is no plan for deserializing old snapshots after the engine state shape changes between deploys, and no EF migration story for the snapshot schema. For weeks-long campaigns spanning app updates this silently breaks resume - directly contradicting the stated durability goal.
- NO UNDO / CORRECTION / GM-OVERRIDE PATH. A manual-assist tool where humans type dice/wound outcomes WILL have misentries. An authoritative engine with no undo is hostile to real play. The EventJournal could enable rewind, but no intent, UX, or mechanism is designed for correcting mistakes mid-showdown.
- PLAYER<->SURVIVOR CONTROL ASSIGNMENT AND TURN-AUTHORIZATION NOT MODELED. connectionId<->playerId is mapped, but nothing defines which player may submit which survivor's intents, who may act as monster controller, or what happens (takeover by whom) when the active survivor's player or the monster controller disconnects mid-turn.
- NO BACKUP/RESTORE FOR CAMPAIGN DATA. Single replica + single SQLite file on one ReadWriteOnce PVC means a node/PV loss destroys a multi-week campaign. No backup cadence, export, or restore path is described.
- ASYNC-PERSIST vs BROADCAST ORDERING IS HAND-WAVED. Deltas are broadcast BEFORE the async SQLite write. A crash between broadcast and write leaves persisted lastSeq behind the seq clients already hold, and seq continuity across process restart is not guaranteed. 'Always resync' papers over most of it, but the lost-last-intent-on-crash case contradicts the 'near-lossless mid-showdown recovery' claim, and seq potentially moving backwards across restart is not addressed.
- THE CONTENT PACK IS LOAD-BEARING BUT UNSPECIFIED. No schema, authoring workflow, validation pipeline, storage location (DB vs mounted file), or compatibility checks beyond a 'contentPackVersion' string. The entire data-driven engine depends on it.
- MULTI-MONSTER / ENCOUNTER / INITIATIVE-CARD scope is implicitly single-monster but never stated. Showdown setup (starting positions, terrain placement, monster starting stat entry) is reduced to a 'starting positions hint' - effectively unmodeled.
- TESTING STRATEGY IS THIN. 'TDD the engine' is asserted, but there are no golden-master/replay tests off the EventJournal, no determinism property tests, and no C#<->TS contract tests in CI - despite contract drift and determinism being named top risks.

### Unverified assumptions
- KDM combat mechanics are stated as FACT in the domain model but are sourced only from Fandom/community wikis, NOT the official rulebook the plan itself says to trust: weapon Speed = number of d10; hit-number derivation from Accuracy/Evasion; wound = 1d10+STR vs Toughness; natural-10 lantern crit only if the location has a crit slot; 'a crit cancels reactions'; Trap card ends the attack; AI both-piles-empty => Basic Action; survivors-first round order; monster controller rotates clockwise; controller's own survivor targeted => +1 insanity; survival-action gating innovations (Encourage/Silent Dialect, Dash/Paint, Surge/Inner Lantern).
- The severe-injury OVERFLOW rule ('damage from a single hit applied up to the first severe-injury roll, remaining overflow discarded') is stated as fact; the research flags this area as BGG-ambiguous and unverified.
- The hit-location reshuffle/empty-deck rule is correctly made configurable, but a DEFAULT behavior is still implicitly assumed without rulebook confirmation.
- The AI card taxonomy itself (Basic/Advanced/Legendary/Special + isMood) is wiki-sourced; only the build COUNTS are correctly deferred to the content pack.
- The 'Cloudflare Access = near-zero app code' recommendation is undercut by the plan's own admission that browsers cannot send service-token headers, so /hub must be EXCLUDED from Access and gated by an app-issued token. App-level auth code is therefore still required; the 'near-zero' effort claim for the recommended option is optimistic.
- Dev Tunnels anonymous access is assumed acceptably safe for a game night: only room CREATION is gated, so anyone with the tunnel URL can reach the hub and attempt JoinRoom. Room-code entropy and rate-limiting are assumed but unspecified.
- Version pins (.NET 10 LTS to Nov 2028; Aspire 13.4.6; @microsoft/signalr 10.0.0 / server 10.0.9; Svelte 5.55.x; SpaProxy 10.0.9) are corroborated by the research as of 2026-06-27 but assumed stable; the research itself notes Aspire/cloudflared ship fast and flags a cloudflared 2026.6.0 service-token regression.
- Stateful-reconnect buffer adequacy (SignalR default ~100,000 bytes) is assumed sufficient; it is never sized against showdown delta volume.
- JSON protocol bandwidth is assumed 'fine at ~5 players' for chatty showdown deltas; plausible but unmeasured.
- The 'deploy a plain container instead of Aspire's k8s publisher' mitigation assumes the team will hand-author Helm/manifests; that effort is not budgeted in the roadmap.
- '3D = just another consumer' is assumed clean; the plan correctly flags Godot/Unity need a spike, but still leans on community/unofficial SignalR samples (Godot-mono.SignalR, Unity WebGL shim) whose current-version compatibility is unverified.

### Missing services / decisions
- C#->TS contract generation toolchain is still UNDECIDED (NSwag vs openapi-typescript vs TypeGen) yet 'generate @kdm/contract in CI from day one' is a Phase-0 deliverable - the tool must be chosen before Phase 0 starts.
- skipNegotiation vs full negotiation behind Cloudflare is not decided; it affects transport fallback (WebSockets-only) and whether connectionId is defined.
- Snapshot schema versioning + migration strategy for cross-deploy resume (a decision, not just a documentation gap).
- The enforcement-vs-record policy for the engine (what it validates vs trusts).
- Final auth decision: CF Access (HTTP create-room only) + app-token on /hub versus pure host-secret; plus the room/player token issuance, storage, and expiry scheme.
- k8s Secret management for host secret / service tokens; PVC StorageClass and access mode; an automated SQLite backup job (e.g., CronJob).
- Content-pack format, storage location, validation, and version-compatibility check service.
- Undo/correction mechanism decision (EventJournal rewind vs none).
- Disconnect-takeover policy for the active survivor and the monster controller.
- EventJournal persistence decision: the plan calls it optional/latest-snapshot-only for MVP, but reconnection-by-replay AND any undo capability both want it persisted - decide now, not later.
- Rate-limiting / room-code entropy / abuse protection for the internet-reachable JoinRoom path.
- Production observability target (OpenTelemetry exporter/destination) versus the dev-only dashboard currently described.
- Explicit in/out scope statement for multi-monster encounters and initiative cards.

### Questions the critic raised for you
- For the MVP showdown, is the monster-attacks-survivors flow (hit locations on survivors, armor by location, severe injury / brain trauma, knockback) IN scope, or is only the survivor->monster attack required first? The model currently omits the monster-offense half entirely.
- Should the engine ENFORCE spatial/timing legality (range, adjacency, valid targets, once-per-round windows) or merely RECORD human-entered outcomes in manual-assist? Where exactly is the trust boundary between engine-validated and player-asserted state?
- Do you need an undo / correction / GM-override capability for misentered dice and wound results during live play? (Strongly recommended for a manual-assist tool; it also forces a decision to persist the EventJournal.)
- When the app is updated mid-campaign, must in-progress saved snapshots still resume (requiring snapshot versioning/migration), or is 'finish the current showdown on the old version' acceptable?
- How are survivors assigned to players, and what should happen when the active survivor's player or the monster controller disconnects mid-turn - who takes over?
- Confirm the realistic auth shape: Cloudflare Access gates ONLY the HTTP create-room route while /hub is excluded from Access and gated by an app-issued room/player token. Is that acceptable? (Access cannot protect the browser WebSocket directly without breaking it.)
- What is your data-loss tolerance for a weeks-long campaign, and do you want an automated backup/export of the SQLite campaign data (single-replica + single PVC is a single point of total loss)?
- Will you author a content pack for the MVP, in what form, with how much acceptable effort, and where should it live (in-DB vs mounted file)? The entire data-driven engine hinges on this artifact existing.
