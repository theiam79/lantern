# Lantern — MVP Implementation Spec (v1)

*Authoritative build spec for the survivor-only showdown slice. Synthesizes the three design lenses (pragmatic thin-slice scope, clean versioned contract, journal-first correctness) into one plan. Where this spec and `docs/PLAN.md` disagree, this spec wins for MVP scope; PLAN remains the long-range design.*

---

## 0. Guiding decisions (locked)

- **Scope = ONE survivor-only showdown, end-to-end, live-synced.** A host creates a room, ≤4 guests join by code, the table enters a showdown against one monster, and a survivor walks the full attack chain (`DeclareAttack → hits-on-X+ → enter hits → app draws hit locations → wounds-on-Y+ → enter wounds → apply`). The monster's turn is **recorded manually** (no AI deck). Everything off that path is cut but left as additive seams.
- **The journal is the source of truth.** Every accepted intent becomes exactly one `JournalEntry` at a monotonic per-room `seq`. Live state is a deterministic fold over the journal; snapshots are a cache. Undo = append a compensating entry; host-override = append a privileged patch entry. Nothing is ever deleted.
- **One authoritative writer per room** (`System.Threading.Channels` single-consumer actor). The hub is transient and stateless.
- **Engine is pure** (`Lantern.Engine`, no SignalR/EF refs): `Reduce(state, intent, rng) → (effects, rngDraws, reject?)`.
- **Engine is LOOSE.** It enforces only what it can see — phase/attack-step ordering, seq, seat existence, deck-non-empty, computed target numbers — and **records** everything that depends on card text or physical dice. Undo + host-override fix the rest.
- **Dice are physical/player-entered.** The app computes & displays target numbers ("hits on X+", "wounds on Y+"); it does **not** roll attack dice. The app **does** run the seeded digital **Hit-Location deck** (the only deck in MVP).
- **IP boundary:** ship mechanical data only (ids, names, stat lines, deck-build counts, card metadata flags). Never ship/commit card art or effect prose. Hit-location card data is **host-provided** (Scribe lacks it).
- **Persist-before-broadcast.** The journal row is written inside one SQLite transaction *before* the delta is broadcast, so `seq` never regresses across a crash. Snapshot writes stay async.

---

## 1. Build order (each bullet ≈ one conventional-commit-sized slice)

> Projects already scaffolded: `Lantern.Engine`, `Lantern.Contracts`, `Lantern.Server`, `Lantern.ServiceDefaults`, `Lantern.AppHost`. Server is organized **vertical-slice / feature-folder**: `Features/Rooms`, `Features/Showdown`, `Features/Journal`, plus `Persistence/` and `Content/` seams.

1. `feat(contracts): envelope, intent/delta DTO catalog, acks` — all records in §2. No logic. Pure assembly; this is the durable asset.
2. `feat(engine): rng — seeded xoshiro256** with named substreams + RngDraw audit` — §4.5. Unit-tested for determinism.
3. `feat(engine): showdown state aggregate + target-number helpers (hits-on/wounds-on)` — §4.2–§4.4. Pure functions, golden-value tested.
4. `feat(engine): hit-location deck (seeded shuffle/draw/discard, trap detection)` — §4.6.
5. `feat(engine): reducer — Reduce + Fold over journal; attack mini-FSM` — §4.1, §5. The centerpiece; TDD with deterministic seeds.
6. `feat(content): content-pack schema + loader/merge + validation` — §6.
7. `feat(persistence): EF Core + SQLite DbContext (Rooms, RoomMembers, Journal, Snapshots) + migrations` — §7.
8. `feat(server): GameRoomService singleton + RoomActor (Channel consumer, commit pipeline, persist-before-broadcast)` — §3.3, §5.4.
9. `feat(server): GameHubV1 — CreateRoom/JoinRoom/Resume/SubmitIntent/RequestSnapshot + auth handshake` — §3, §8. Wire `MapHub<GameHubV1>("/hub/v1")` in `Program.cs` (replaces the TODO).
10. `feat(server): rehydrate active rooms on startup; SIGTERM final snapshot` — §7.
11. `feat(journal): Undo + HostOverride intents + roster/presence` — §5.6–§5.7.
12. `chore(ci): content-pack IP guard (no prose/art) + engine determinism replay test` — §6.4, §9.

Phase-0 walking skeleton = slices 1–9 with only `StartShowdown` + `DeclareAttack` wired; slices 10–12 harden it into the full playable slice.

---

## 2. Wire contract — `Lantern.Contracts`

All server→client traffic uses the **single envelope** already scaffolded. All client→server gameplay uses the **single `SubmitIntent` entrypoint** carrying a polymorphic, closed-set intent.

### 2.1 Envelope, versioning, seq/resync

```csharp
public static class Protocol
{
    public const int Version = 1;          // bump on ANY breaking intent/delta change
    public const int MinSupported = 1;     // server accepts [MinSupported, Version]
}

// already in Protocol.cs — keep as-is
public sealed record ServerMessage<T>(int ContractVersion, long Seq, string Type, T Payload);
```

**Rules (defined above the transport — SignalR is a replaceable adapter):**
- On join/resync the server sends **exactly one** `Snapshot` with `Seq = N` (the seq the state is current as of), then `Delta`s with **strictly increasing** `Seq = N+1, N+2, …`.
- One accepted intent → one journal entry → one `Delta` carrying that entry's effects, at that entry's `seq`.
- Client tracks `lastSeq`. If an incoming delta's `Seq != lastSeq+1`, **OR** a snapshot arrives with `Seq < lastSeq`, the client **adopts the snapshot, resets `lastSeq`, and discards buffered deltas**. The server is always right.
- `RoomError` does **not** advance seq.
- Every request carries `ContractVersion`. Outside `[MinSupported, Version]` → `RoomError(code="contract_version_mismatch")` and the invoke fails fast.

### 2.2 Server → client messages (`IGameClient` payloads)

```csharp
// type = "snapshot"
public sealed record SnapshotPayload(
    string RoomCode,
    ShowdownSnapshotDto State,        // full projected state (ids + numbers only; no prose)
    RosterDto Roster);

// type = <effectType> (e.g. "AttackDeclared"); one Delta per committed entry
public sealed record DeltaPayload(
    string ActorPlayerId,
    IReadOnlyList<EffectDto> Effects); // the entry's effects, in order

public sealed record EffectDto(string Kind, JsonElement Data);

// type = "error"
public sealed record RoomErrorPayload(
    string Code,                      // see §2.6
    string Message,
    string? RejectedIntentId);

// type = "presence"
public sealed record RosterDto(IReadOnlyList<RosterEntryDto> Players);
public sealed record RosterEntryDto(
    string PlayerId, string DisplayName, bool IsHost, bool Connected);
```

`ShowdownSnapshotDto` is the serialized projection of `ShowdownState` (§4) — flat DTOs of ids/counters, never engine internals.

### 2.3 Handshake acks (returned from invoke, not broadcast)

```csharp
public sealed record CreateRoomResult(
    string RoomCode, string PlayerId, string PlayerToken,
    int ContractVersion, ServerMessage<SnapshotPayload> Snapshot);

public sealed record JoinRoomResult(
    string RoomCode, string PlayerId, string PlayerToken,
    int ContractVersion, ServerMessage<SnapshotPayload> Snapshot);

public sealed record IntentAck(
    bool Accepted, long? CommittedSeq, string? RejectReason, string ClientIntentId);
```

### 2.4 Handshake request DTOs

```csharp
public sealed record CreateRoomRequest(
    string HostPassword, string DisplayName, string ContentPackId,
    string? RoomPassword, int ContractVersion);

public sealed record JoinRoomRequest(
    string RoomCode, string DisplayName, string? RoomPassword, int ContractVersion);

public sealed record ResumeRequest(
    string RoomCode, string PlayerId, string PlayerToken, int ContractVersion);
```

### 2.5 The gameplay intent envelope + closed intent set

`SubmitIntent` carries one envelope. The `Intent` is JSON-polymorphic, discriminated on `Type`, and the deserializer **rejects unknown types** (closed allowlist — never best-effort bind).

```csharp
public sealed record IntentEnvelope(
    string RoomCode,
    string PlayerId,
    string PlayerToken,
    string ClientIntentId,            // client GUID — idempotency key (per-room unique)
    Intent Intent);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartShowdownIntent),     "StartShowdown")]
[JsonDerivedType(typeof(AddSurvivorIntent),       "AddSurvivor")]
[JsonDerivedType(typeof(DeclareAttackIntent),     "DeclareAttack")]
[JsonDerivedType(typeof(EnterHitsIntent),         "EnterHits")]
[JsonDerivedType(typeof(DrawHitLocationsIntent),  "DrawHitLocations")]
[JsonDerivedType(typeof(EnterWoundIntent),        "EnterWound")]
[JsonDerivedType(typeof(ApplyAttackResultIntent), "ApplyAttackResult")]
[JsonDerivedType(typeof(RecordMonsterTurnIntent), "RecordMonsterTurn")]
[JsonDerivedType(typeof(SetRoomPasswordIntent),   "SetRoomPassword")]
[JsonDerivedType(typeof(UndoIntent),              "Undo")]
[JsonDerivedType(typeof(HostOverrideIntent),      "HostOverride")]
[JsonDerivedType(typeof(EndShowdownIntent),       "EndShowdown")]
public abstract record Intent;

// ---- setup ----
public sealed record StartShowdownIntent(
    string MonsterId, string MonsterLevelId, ulong? MasterSeed) : Intent;

public sealed record AddSurvivorIntent(
    string SurvivorId, string Name, AttributesDto Attributes, string WeaponId) : Intent;
public sealed record AttributesDto(int Mov, int Acc, int Str, int Eva, int Lck, int Spd);

// ---- attack mini-FSM (the centerpiece) ----
public sealed record DeclareAttackIntent(
    string SurvivorId, string WeaponId, string TargetMonsterId) : Intent;

// Mode "rolls": app computes hits = count(r >= hitsOn). Mode "count": app trusts HitCount.
public sealed record EnterHitsIntent(
    string AttackId, string Mode, int[]? Rolls, int? HitCount) : Intent;

public sealed record DrawHitLocationsIntent(string AttackId) : Intent;

// Mode "roll": compare Roll vs woundsOn (+ lantern-crit if HasCriticalSlot). Mode "outcome": trust Outcome.
public sealed record EnterWoundIntent(
    string AttackId, string LocationCardId, string Mode,
    int? Roll, string? Outcome /* "wound"|"fail"|"crit" */) : Intent;

public sealed record ApplyAttackResultIntent(string AttackId) : Intent;

// ---- manual monster turn (no AI deck in MVP) ----
public sealed record RecordMonsterTurnIntent(
    string Note, ManualSurvivorWoundDto? SurvivorWound) : Intent;
public sealed record ManualSurvivorWoundDto(string SurvivorId, int Amount);

// ---- host-only ----
public sealed record SetRoomPasswordIntent(string? Password) : Intent;
public sealed record UndoIntent(int Count, long? TargetSeq) : Intent;     // count default 1
public sealed record HostOverrideIntent(StatePatchDto Patch, string Reason) : Intent;
public sealed record EndShowdownIntent(string Result /* "Victory"|"Defeat"|"Abort" */) : Intent;

// typed, whitelisted patch (NOT free JSON-pointer) — see §5.7
public sealed record StatePatchDto(string Path, JsonElement Value);
```

### 2.6 Intent catalog (who may submit / what it does)

| Intent | Who | Effect (loose) |
|---|---|---|
| `StartShowdown` | any seated | Build `MonsterState` from pack, seed + shuffle hit-loc deck, `Status: Idle`. |
| `AddSurvivor` | any seated | Thin combat record entered once. |
| `DeclareAttack` | any seated | `Idle → AwaitingHits`; engine derives `HitsOn` (acc vs evasion) & `AttackDice` (weapon speed). |
| `EnterHits` | any seated | `AwaitingHits → DrawingLocations`; records hit count (computed from rolls or trusted). |
| `DrawHitLocations` | any seated | Draws `HitCount` cards from seeded deck. Trap → attack ends (`Resolved`, reason `Trap`). Else `→ AwaitingWounds` with per-card `WoundsOn`. |
| `EnterWound` | any seated | Records per-location outcome; when all drawn locations resolved → `Resolved`. |
| `ApplyAttackResult` | any seated | Commit wounds to monster, discard drawn cards, `Resolved → Idle`; emit `MonsterDefeated` if threshold met. |
| `RecordMonsterTurn` | any seated | Free-text monster-turn record + optional manual survivor damage. |
| `SetRoomPassword` | host | Set/clear room join password. |
| `Undo` | host | Append revert entry for last N intents (or back to `TargetSeq`). |
| `HostOverride` | host | Apply whitelisted typed patch; journaled with reason. |
| `EndShowdown` | host | Terminal; final snapshot retained. |

> **Authorization is permissive** (cooperative game, 4 trusted friends): any seated player may submit any gameplay intent. Only `SetRoomPassword`, `Undo`, `HostOverride`, `EndShowdown` are host-gated. `RecordMonsterTurn` carries no AI-card reference yet but the DTO is free to grow one later (additive).

---

## 3. SignalR hub — `Lantern.Server`

### 3.1 Hub + client interface

One route-versioned hub at `/hub/v1`, JSON protocol (`AddJsonProtocol`). Transient/stateless: it resolves connection context, calls the singleton, manages group membership. Never holds game state.

```csharp
public sealed class GameHubV1(IGameRoomService rooms) : Hub<IGameClient>
{
    public Task<CreateRoomResult> CreateRoom(CreateRoomRequest req);
    public Task<JoinRoomResult>   JoinRoom(JoinRoomRequest req);
    public Task<JoinRoomResult>   Resume(ResumeRequest req);      // reconnect path
    public Task<IntentAck>        SubmitIntent(IntentEnvelope env);
    public Task                   RequestSnapshot(string roomCode); // server replies via Snapshot to caller

    public override Task OnConnectedAsync();                       // no group add — client must Join/Resume
    public override Task OnDisconnectedAsync(Exception? e);        // RoomActor marks member Connected=false; keeps seat
}

public interface IGameClient
{
    Task Snapshot(ServerMessage<SnapshotPayload> msg);
    Task Delta(ServerMessage<DeltaPayload> msg);
    Task RoomError(ServerMessage<RoomErrorPayload> msg);
    Task Presence(ServerMessage<RosterDto> msg);
}
```

`RoomError` codes: `host_auth_failed`, `room_not_found`, `room_password_required`, `bad_token`, `host_only`, `contract_version_mismatch`, `intent_rejected`, `needs_migration`.

### 3.2 Rooms = SignalR groups; identity

- **Rooms are SignalR Groups** keyed by `roomCode`. `Groups.AddToGroupAsync` on Create/Join/Resume; deltas fan out via `Clients.Group(roomCode)`.
- **Group membership is NOT restored** on reconnect/restart — the authoritative roster lives in the `RoomActor`. The hub re-adds the connection on every Join/Resume.
- **Never trust `connectionId` for identity.** On Join/Resume the server binds `(playerId, playerToken)` into `Context.Items["playerId"]` and the actor's `connectionId → playerId` map. Every `SubmitIntent` is authorized by the `(PlayerId, PlayerToken)` in the envelope, validated against the actor roster.

### 3.3 Connection lifecycle / handshake

1. **Create** — host invokes `CreateRoom{hostPassword,…}`. Server validates env password (constant-time), mints `roomCode` (8-char Crockford base32) + `playerId` (GUID) + `playerToken` (256-bit random, stored hashed), creates the `RoomActor`, appends genesis journal entry (`seq=0`), persists initial snapshot, adds caller to group, returns `CreateRoomResult` then pushes `Snapshot`.
2. **Join** — guest invokes `JoinRoom{roomCode, displayName, roomPassword?}`. If room has a password, validate. Mint `playerId`+`playerToken`, seat the player (journaled roster effect), add to group, return `JoinRoomResult`, push `Snapshot`, broadcast `Presence`.
3. **Play** — `SubmitIntent` → actor reduces, commits at `seq`, ack returns `IntentAck{accepted, committedSeq}`, delta fans out to the group.
4. **Reconnect** — socket drops (new `connectionId`); client invokes `Resume{roomCode, playerId, playerToken}`. Server re-validates token, rebinds connection, re-adds to group, always pushes a fresh `Snapshot`.
5. **Seq gap** — client invokes `RequestSnapshot(roomCode)`; server replies `Snapshot` to that caller only.

Client guidance (thin): register all `.on(...)` before `start()`; wrap `start()` in a retry loop; supply a long custom `IRetryPolicy` (default gives up ~42s — too short for a game night); store `playerId`/`playerToken` in `localStorage`.

---

## 4. Engine domain model — `Lantern.Engine`

Pure, immutable records. **Legend:** `(A)` app-owned mutable state · `(R)` reference-by-id into content pack · `(T)` thin entered copy. `(R)` data is **copied into state at `StartShowdown`** so the showdown is self-contained and replayable even if the pack is later edited.

### 4.1 Reducer surface

```csharp
public interface IShowdownEngine
{
    ReduceResult Reduce(ShowdownState state, Intent intent, IRngStream rng, IContentPack pack);
    ShowdownState Fold(ShowdownState fromSnapshot, IReadOnlyList<JournalEntry> tail, IContentPack pack);
}

public sealed record ReduceResult(
    bool Accepted,
    string? RejectReason,
    IReadOnlyList<Effect> Effects,     // applied by the fold AND broadcast as the delta
    IReadOnlyList<RngDraw> RngDraws);  // every app RNG draw this reduce consumed (audit/replay)
```

### 4.2 Aggregate

```csharp
public sealed record ShowdownState
{
    public required string ShowdownId { get; init; }
    public required int ContractVersion { get; init; }
    public required string ContentPackId { get; init; }    // (R) pin
    public required ShowdownStatus Status { get; init; }    // Idle|AwaitingHits|DrawingLocations|AwaitingWounds|Resolved|Ended
    public required string MonsterId { get; init; }         // (R)
    public required string MonsterLevelId { get; init; }    // (R)
    public required MonsterState Monster { get; init; }
    public required ImmutableDictionary<string, SurvivorCombatState> Survivors { get; init; }
    public required HitLocationDeckState Deck { get; init; }
    public AttackSequence? Attack { get; init; }            // non-null only while an attack is in flight
    public string? MonsterTurnNote { get; init; }
    public required RngCursors Rng { get; init; }
    public long LastSeq { get; init; }
}
public enum ShowdownStatus { Idle, AwaitingHits, DrawingLocations, AwaitingWounds, Resolved, Ended }
```

### 4.3 Monster & survivor

```csharp
public sealed record MonsterState
{
    public required string Id { get; init; }                                   // (R)
    public required MonsterStats Base { get; init; }                           // (R) copied at StartShowdown
    public ImmutableDictionary<string,int> WoundsByLocation { get; init; }     // (A)
    public int TotalWounds { get; init; }                                      // (A)
    public required int ToughnessWoundThreshold { get; init; }                 // from level data; lethal at >=
    // Modifiers / traits DEFERRED for the slice (no card effects yet) — additive later.
}
public sealed record MonsterStats(
    int Movement, int Toughness, int Speed, int Accuracy, int Damage, int Luck, int Evasion);

public sealed record SurvivorCombatState(
    string SurvivorId, string Name, SurvivorAttributes Attributes,
    string WeaponId /* (R) */, bool Dead /* recorded, not enforced */);       // (T)
public sealed record SurvivorAttributes(int Mov, int Acc, int Str, int Eva, int Lck, int Spd);
```

### 4.4 Attack mini-FSM + target-number computation

```csharp
public sealed record AttackSequence(
    string AttackId, string SurvivorId, string WeaponId, string TargetMonsterId,
    int AttackDice,                                  // = weapon.Speed
    int HitsOn,                                      // derived (see below)
    int? EnteredHitCount,                            // after EnterHits
    ImmutableArray<DrawnLocation> DrawnLocations);   // after DrawHitLocations

public sealed record DrawnLocation(
    string CardId, bool HasCriticalSlot, bool IsTrap,
    int WoundsOn,                                    // derived (see below)
    WoundOutcome? Result);                           // null until EnterWound
public enum WoundOutcome { Wound, Fail, Crit }
```

**Target-number helpers (pure, unit-tested, content-pack-configurable formulas):**

```csharp
// "hits on X+"  — default formula kind "accuracy-minus-evasion":
//   HitsOn = clamp(pack.toHit.base - weapon.Accuracy + monster.Evasion, 2, 10)   // higher Accuracy => easier
// "wounds on Y+" — default formula kind "toughness-minus-strength":
//   WoundsOn = clamp(monster.Toughness - (survivor.Str + weapon.Strength), pack.wound.min /*2*/, pack.wound.max /*10*/)
// natural-10 (lantern) = Crit IFF DrawnLocation.HasCriticalSlot.
```

> ⚠️ The exact constants/direction are rulebook-ambiguous and made pack-configurable. A wrong default silently produces wrong target numbers — verify against the physical rulebook before first play (open assumption §10).

### 4.5 Seeded RNG (auditable, replayable)

```csharp
public interface IRngStream { ulong Next(string substream); ImmutableArray<int> Shuffle(int n, string substream); }
public sealed record RngCursors(ulong MasterSeed, ImmutableDictionary<string,ulong> PerSubstream);
public sealed record RngDraw(string Substream, ulong CursorBefore, ulong Raw, string DerivedResult);
```

- **Never `System.Random`.** Use SplitMix64 → xoshiro256**, seeded by `hash(masterSeed + substreamLabel)`.
- MVP substream: `"hitloc-shuffle"` only (**no `"dice"` substream — dice are physical**).
- No `DateTime`, no parallelism, deterministic collection ordering (`ImmutableArray` for deck order) inside `Reduce`. Every draw appends an `RngDraw` to the entry → exact replay/undo.

### 4.6 Hit-location deck (the one deck the app runs)

```csharp
public sealed record HitLocationDeckState(
    ImmutableArray<string> DrawPile,      // card ids in ORDER (seeded Fisher-Yates) (A)
    ImmutableArray<string> DiscardPile,
    ImmutableArray<string> DrawnThisAttack);
```

- Built at `StartShowdown` by expanding pack card `count`s into an id multiset, then seeded Fisher-Yates on `"hitloc-shuffle"`.
- **Draw rule (configurable default = reshuffle-and-continue):** pop `HitCount` from `DrawPile`; if it empties mid-draw, seeded-reshuffle `DiscardPile` into `DrawPile` and continue. A **Trap**-type card (metadata flag) ends the attack immediately (`AttackEndedByTrap`); it is still recorded as drawn.

### 4.7 Effects (delta payload = fold input)

`PhaseChanged`, `ShowdownStarted`, `SurvivorAdded`, `AttackDeclared{hitsOn,attackDice}`, `HitsEntered{count}`, `LocationsDrawn{cardIds}`, `AttackEndedByTrap{cardId}`, `WoundEntered{cardId,outcome}`, `AttackApplied{woundsByLocation}`, `MonsterDefeated`, `MonsterTurnRecorded`, `RosterChanged`, `ShowdownEnded{result}`, `Reverted{seqs}`, `StateOverridden{path,reason}`.

### 4.8 Enforcement boundary (explicit)

| Engine ENFORCES (rejects) | Engine RECORDS (trusts) |
|---|---|
| Attack-step ordering (no `EnterWound` before `DrawHitLocations`) | Hit/wound/crit outcomes & literal rolls |
| Status-right-for-intent; seat exists; deck non-empty | Monster turn (free text + manual survivor damage) |
| Computed `HitsOn`/`WoundsOn` (display + roll-mode math) | Survivor death, knockdown, any card-text effect |
| Host-only gating; seq monotonicity; idempotency | Legality that depends on card prose / positioning |

---

## 5. Journal / undo / host-override

### 5.1 Journal entry (the committed unit of truth)

```csharp
public sealed record JournalEntry(
    string RoomCode,
    long Seq,                               // monotonic per room; seq 0 = Genesis
    JournalKind Kind,                       // Genesis | Intent | Revert | HostOverride
    string ActorPlayerId,
    DateTimeOffset CommittedAt,
    Intent? Intent,                         // present for Kind=Intent
    ImmutableArray<Effect> Effects,         // applied by Fold AND broadcast as the delta
    ImmutableArray<RngDraw> RngDraws,
    ImmutableArray<long> RevertedSeqs,      // present for Kind=Revert
    StatePatchDto? Override,                // present for Kind=HostOverride
    string? ClientIntentId);                // idempotency key (unique per room)
public enum JournalKind { Genesis, Intent, Revert, HostOverride }
```

### 5.2 State = fold over journal

`current = Fold(latestSnapshot, journal.Where(e => e.Seq > snapshot.Seq).OrderBy(e => e.Seq))`. The fold:
- accumulates a set of reverted seqs from every `Revert` entry; it **skips the Effects AND RngDraws** of any entry whose seq is in that set (so deck order stays deterministic across undo);
- applies `HostOverride` patches last-in-fold (wins);
- a `Revert` entry itself contributes no game effects beyond extending the reverted set + emitting a `Reverted` delta.

### 5.3 Snapshot cadence

Snapshot **after every committed entry** (cheap at 4 players → near-lossless resume). Snapshots are a *cache* of the fold — any can be rebuilt from the journal since Genesis. Keep latest ~5 per room; force a snapshot on SIGTERM.

### 5.4 Commit pipeline (single writer = RoomActor consumer)

1. Dedupe by `ClientIntentId` → return the prior `IntentAck` if already committed (reconnect-retry safe).
2. Authorize (seat exists; host-gating for host-only intents).
3. `result = engine.Reduce(state, intent, rng, pack)`.
4. If `!Accepted` → `RoomError` to caller; **no journal append** (rejected intents are not history).
5. `seq = state.LastSeq + 1`; build `JournalEntry`; **in ONE SQLite transaction**: insert `JournalRow(seq)` + update `RoomRow.LastSeq`. Apply effects to fold live in-memory state.
6. **Then broadcast** `Delta(seq, effects)` to the group; return `IntentAck{accepted, seq}`.
7. Enqueue async `SnapshotRow` write off the hot path.

Persist-before-broadcast guarantees `seq` never regresses across a crash; a crash after persist but before broadcast merely leaves clients behind → they resync on reconnect.

### 5.5 Snapshot/seq/resync — see §2.1 (single source of monotonicity: the RoomActor `seq`, identical in journal row, broadcast `Seq`, and `snapshot.LastSeq`).

### 5.6 Undo (host-only)

`Undo{Count|TargetSeq}` appends a `Revert` entry (new forward seq) whose `RevertedSeqs` = the last N committed `Intent`/`HostOverride` seqs (or all seqs back to `TargetSeq`). History is never deleted. The actor re-folds from the nearest snapshot at/before the rewind point, then **broadcasts a fresh `Snapshot`** (forced resync) rather than incremental deltas. Undo is itself a normal entry and is itself undoable. **Default granularity = per-intent** (open assumption §10; correlation-group undo can be layered later).

### 5.7 Host override (host-only)

`HostOverride{Patch, Reason}` appends a `HostOverride` entry, applied last-in-fold. `StatePatchDto.Path` is a **typed whitelist**, not free JSON-pointer:
`monster.totalWounds`, `monster.woundsByLocation.<loc>`, `attack.<cardId>.outcome`, `survivor.<id>.dead`, `status`. Paths outside the whitelist → reject. The `Reason` is retained for audit. Broadcast as a forced `Snapshot`.

---

## 6. Content pack — `content/packs/`

### 6.1 Two-tier model

- **(A) Shippable pack** (committed, `content/packs/<packId>.json`): gear stats, monster stat lines, level wound thresholds, deck-build counts, formula config. Mechanical data only.
- **(B) Host-provided hit-location deck** (NOT committed; mounted at `content/local/<ref>.json` (gitignored) **or** uploaded at room setup): the literal card list with metadata flags only. Merged into (A) by `hitLocationDeckRef` at `StartShowdown`.

### 6.2 Shippable pack example (`content/packs/core-1.6.json`)

```jsonc
{
  "schemaVersion": 1,
  "packId": "core-1.6",
  "editionId": "kdm-1.6",
  "displayName": "Core (mechanical data only)",
  "shippable": true,
  "weapons": [
    { "id": "founding-stone", "name": "Founding Stone", "type": "melee", "speed": 2, "accuracy": 6, "strength": 1 },
    { "id": "bone-axe",       "name": "Bone Axe",       "type": "melee", "speed": 2, "accuracy": 6, "strength": 3 }
  ],
  "monsters": [
    { "id": "white-lion", "name": "White Lion",
      "levels": [
        { "id": "white-lion-l1", "name": "Level 1",
          "stats": { "movement": 6, "toughness": 9, "speed": 1, "accuracy": 0, "damage": 2, "luck": 0, "evasion": 0 },
          "woundThreshold": 9,
          "hitLocationDeckRef": "host:white-lion-hitloc" } ] }
  ],
  "toHitFormula": { "kind": "accuracy-minus-evasion", "base": 6 },
  "woundFormula": { "kind": "toughness-minus-strength", "min": 2, "max": 10 }
}
```

### 6.3 Host-provided hit-location deck (gitignored / uploaded)

```jsonc
{
  "schemaVersion": 1,
  "deckId": "white-lion-hitloc",
  "cards": [
    { "id": "wl-hl-01",   "label": "Hit Location A", "hasCriticalSlot": true,  "isTrap": false, "copies": 1 },
    { "id": "wl-hl-02",   "label": "Hit Location B", "hasCriticalSlot": false, "isTrap": false, "copies": 2 },
    { "id": "wl-hl-trap", "label": "Trap",           "hasCriticalSlot": false, "isTrap": true,  "copies": 1 }
  ]
}
```

`label` is a neutral mechanical name, **not effect prose** — effects stay on the player's physical card.

### 6.4 Loader + IP guard

- **Loader** (`Content/` seam in Server; engine consumes an `IContentPack` interface): validate `schemaVersion` is known; unique ids; every monster level's `hitLocationDeckRef` resolves (shippable id or `host:` ref present at runtime); `copies >= 1`; stats present. Expand deck-build counts into id multisets at `StartShowdown`. Missing host overlay → **setup-blocking `RoomError`**, not a crash. Unknown top-level keys ignored (forward-compat).
- Host-provided deck data is stored **per-room** (DB row, ephemeral to the campaign) when uploaded, or read from the gitignored mount.
- **CI guard:** grep committed packs to reject any field that looks like effect prose (`effect`, `rulesText`, free-text bodies) or image paths. `content/assets/` is gitignored and never referenced by id-resolution.

---

## 7. Persistence — EF Core + SQLite (WAL)

`LanternDbContext`, four tables. All writes funnel through the per-room actor (single writer; WAL handles concurrent readers).

```csharp
// Rooms — registry + lobby status
class RoomRow {
  string RoomCode;            // PK
  string ContentPackId;
  string? RoomPasswordHash;   // null = open
  string HostPlayerId;
  long LastSeq;               // monotonic high-water mark
  string Status;              // "lobby" | "showdown" | "ended"
  string? HostHitLocDeckJson; // host-provided deck, ephemeral to this room (nullable)
  int ContractVersion;
  DateTimeOffset CreatedAt, UpdatedAt;
}

// RoomMembers — auth roster
class RoomMemberRow {
  string RoomCode;            // PK part
  string PlayerId;            // PK part
  string DisplayName;
  bool IsHost;
  string PlayerTokenHash;     // hashed; never store the raw token
  DateTimeOffset JoinedAt, LastSeenAt;
}

// JournalRows — append-only source of truth (INSERT only; never UPDATE/DELETE)
class JournalRow {
  string RoomCode;            // PK part
  long Seq;                   // PK part; UNIQUE (RoomCode, Seq)
  int Kind;                   // Genesis|Intent|Revert|HostOverride
  string ActorPlayerId;
  string? IntentJson;
  string EffectsJson;
  string RngDrawsJson;
  string? RevertedSeqsJson;
  string? OverrideJson;
  string? ClientIntentId;     // UNIQUE (RoomCode, ClientIntentId) — idempotency
  int SchemaVersion;
  DateTimeOffset CommittedAt;
}

// SnapshotRows — fold cache (keep latest ~5 per room)
class SnapshotRow {
  string RoomCode;            // PK part
  long Seq;                   // PK part
  int ContractVersion;        // GameState serialization shape (resume guard)
  string StateJson;           // serialized ShowdownState (ids + numbers only)
  string RngCursorsJson;
  DateTimeOffset At;
}
```

- **Write path:** §5.4 — journal row + `RoomRow.LastSeq` in one transaction *before* broadcast; snapshot async.
- **Rehydrate on startup:** load non-ended rooms; load latest `SnapshotRow` + `JournalRow`s where `Seq > snapshot.Seq`; `engine.Fold(snapshot, tail)` → live state; `lastSeq = max(Seq)`. Genesis (seq 0) guarantees a foldable origin. Group membership rebuilt lazily as players `Resume`.
- **Schema versioning:** `SnapshotRow.ContractVersion`/`JournalRow.SchemaVersion` stamp the JSON shape. On mismatch with no registered upcaster → set `RoomRow.Status = "needs migration"` and surface `RoomError(needs_migration)` rather than misfold. EF migrations cover DDL; JSON-body migration is separate and explicit (deferred work, seam noted).
- **Backup (documented):** SQLite file on a volume; `VACUUM INTO` copy as MVP backup. Single-PVC SPOF accepted for personal use.

---

## 8. Auth

- **Room-creation gate:** env var **`LANTERN_HOST_PASSWORD`**. `CreateRoom.HostPassword` is compared constant-time; mismatch → `RoomError(host_auth_failed)`, no room. This is the only server secret. The creator becomes host (`IsHost = true`).
- **Guest join:** `JoinRoom{roomCode, displayName, roomPassword?}`. `roomCode` is 8-char Crockford base32 (~40 bits) from a CSPRNG — entropy is the practical anti-enumeration measure. If the host set a room password, it must match. MVP default = open join (no admit step); admit-mode is a later one-flag flip.
- **Player identity:** on Create/Join the server mints a stable `playerId` (GUID) + `playerToken` (256-bit random, stored **hashed**). Client persists both in `localStorage`. Every `SubmitIntent`/`Resume` is authorized by `(playerId, playerToken)` against the actor roster — **never `connectionId`**. `Resume` re-validates and rebinds the new connection. The token travels in the message payload (not a cookie) to avoid the Cloudflare Access binding-cookie WebSocket trap.
- **Roles:** host-only intents = `SetRoomPassword`, `Undo`, `HostOverride`, `EndShowdown`. All other gameplay intents are open to any seated player.

**Enforced now:** host-password create gate; room-code entropy; optional room password; token-based identity & reconnect; host-only intent gating.
**Deferred (seams in place):** admit/kick mode; rate-limiting on `JoinRoom`; swapping the host-secret check for Cloudflare Access/OIDC behind an `IRoomCreationGate`; token rotation/expiry.

---

## 9. Testing (minimum bar, day one)

- **Engine determinism / golden-master replay:** same seed + same intents ⇒ identical effects, deck order, and final state. Replaying the journal reproduces the snapshot byte-for-byte (after canonical serialization).
- **Target-number unit tests:** `HitsOn`/`WoundsOn` for representative gear/monster combos.
- **Undo replay tests:** an undo of N intents re-folds to exactly the pre-N state (RNG cursors included).
- **Idempotency test:** a retried `SubmitIntent` (same `ClientIntentId`) returns the original ack and does not double-apply.
- **CI content-pack IP guard** (§6.4).

---

## 10. Open assumptions (revisit before/at first play)

1. **To-hit & wound formula constants + direction** — defaults in §4.4 are wiki-sourced; confirm against the physical rulebook. Wrong default = silently wrong target numbers. Pack-configurable.
2. **Hit-location reshuffle-on-empty** — default `reshuffle-and-continue` (vs stop-at-empty) needs rulebook confirmation; configurable.
3. **Trap semantics** — assumed "drawn card ends the attack immediately"; confirm.
4. **First monster/level** — White Lion L1 assumed as the slice target.
5. **Undo granularity** — default per-intent (vs per-attack-chain correlation grouping, which can be added later).
6. **Auto-chaining** — `EnterHits→DrawHitLocations` and `EnterWound→ApplyAttackResult` are explicit intents (cleaner undo); optional UI auto-chain is a client concern, not a contract change.
7. **Host-provided deck delivery** — file-mount supported now; in-hub upload at `StartShowdown` is a later convenience.
8. **Persist-before-broadcast latency** — one sync SQLite write on the intent path; negligible at 4 players. If it ever bites, move the *snapshot* (not the journal) write async (already is) and keep journal sync.
9. **Snapshot schema migration** — only stubbed (refuse-to-resume on mismatch); a real upcaster chain is deferred work not yet budgeted.
10. **Out of MVP scope (additive seams kept):** monster-attacks-survivor resolution (hit locations/armor/severe injury), AI deck, grid/movement/facing, reactions/interrupts, survival actions, multi-monster, knockdown, Hunt/Settlement phases. The protocol's polymorphic closed intent set grows by adding new derived types — existing message ordering and old clients are never broken.
11. **TS contract generation toolchain** (TypeGen vs NSwag vs openapi-typescript) — blocks client work, not this engine/contract slice; decide before Phase-0 client. TypeGen favored for a SignalR-first contract.

---

## 11. MVP implementation decisions (resolving the design critique)

These refine §0–§10 where the design critique found gaps; **the code follows these where they differ from the sections above.**

1. **Wire updates are full-state pushes, not typed-effect deltas (MVP).** Every accepted intent makes the server push the full projected `ShowdownStateDto` (+ `RosterDto`) at the new `seq`; the client renders the latest and never folds effects. Removes the need for a client effect catalog/fold; snapshot and update payloads are identical. Revisit with granular deltas only if payloads grow.
2. **State reconstruction = snapshot + `Reduce`-replay (not effect-replay).** A snapshot stores the full engine `ShowdownState` (incl. deck order + RNG cursors). Fold/rehydrate = load the latest snapshot, then re-run `Reduce` over journal entries after it (RNG re-seeded from cursors). This makes deck order after a reshuffle reconstructible.
3. **Determinism: IDs & seeds are minted by the `RoomActor` (the single writer) and journaled — never inside `Reduce`.** `AttackId = "atk-{seq}"`; the showdown `MasterSeed` (if not supplied) is generated once at `StartShowdown` and stored in that journal entry. `Reduce` is a pure function of (state, resolved-intent, rng-from-cursor).
4. **Presence is off the gameplay journal.** Create/Join/Resume/disconnect update a `RoomMembers` roster and broadcast a **seq-less** `presence` message; only gameplay intents append to the journal and advance `seq`. The single-writer `RoomActor` serializes both. Prevents connect/disconnect storms polluting the journal.
5. **`SubmitIntent` takes the intent as a `JsonElement`** and deserializes manually into the closed intent set (System.Text.Json would otherwise throw on an unknown discriminator during SignalR binding). Unknown type / out-of-range version → `IntentAck{Accepted=false}` + `RoomError`, no throw.
6. **Host-override applies at its seq position** (a normal journaled intent in the replay), not perpetually last. **Undo** appends an `Undo{targetSeq}` entry; Fold skips undone entries. MVP undo targets the last gameplay entry.
7. **Snapshot delivery: the handshake invoke RESULT carries the snapshot** (`CreateRoomResult`/`JoinRoomResult`/`ResumeResult`); there is no duplicate pushed snapshot callback on join (avoids the ordering race).
8. **Host-provided hit-location decks load from `content/local/` (gitignored)**; shippable mechanical packs load from `content/packs/` (committed). The loader merges both; a `host:<id>` deck ref resolves under `content/local/`. Upload-via-hub is deferred. `content/local/` is now in `.gitignore`.
9. **SQLite: WAL + `busy_timeout`** on the DbContext; the journal row is written inside one transaction before broadcast (persist-before-broadcast). Sufficient for the personal single-room case; cross-room contention is handled by WAL + busy-timeout.
10. **Target numbers are LOOSE, isolated in one `TargetNumbers` helper:** `HitsOn = clamp(weapon.Accuracy − survivor.Accuracy + monster.Evasion, 2, 10)`; `WoundsOn = clamp(monster.Toughness − survivor.Strength − weapon.Strength, 2, 10)`. **ASSUMPTION — verify against the physical rulebook** (open question); kept in one place so it's a one-line fix.
11. **Single host for the MVP** (host powers don't migrate). A dropped host uses `Resume` (PlayerToken) to reclaim. Host migration deferred.
12. **`RoomError` carries the current `seq`** in the envelope (it does not advance it).

**Deferred on purpose:** host-deck upload-via-hub, host migration, handshake idempotency keys, granular deltas, JSON journal schema migration, admit-on-join (MVP = open join with optional room password).
