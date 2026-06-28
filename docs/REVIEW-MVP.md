# Lantern MVP — Hardening Report

Independent verification downgraded every originally-"high" finding to **medium** (all are frequency-gated: ~4-player self-hosted use, or only trigger on a process crash / mid-operation I/O failure). Nothing is a guaranteed-on-every-run defect. The list below is reordered by *impact* — the top group can cause **permanent silent state corruption or a wedged room**, so treat them as must-fix before you rely on persistence/crash recovery.

---

## 1. Must-fix — permanent corruption / wedged room

### 1.1 Commit is lost in-memory if the snapshot-prune SaveChanges throws → room wedged
`src/Lantern.Server/Rooms/RoomActor.cs:228-239`, `PruneSnapshotsAsync:298-302`
The commit transaction (journal + snapshot + `room.LastSeq`) is persisted at :228, but in-memory `_state`/`_lastSeq`/`_committedIntents`/`_gameplaySeqs` and the client broadcast are updated only *after* the `using` block (:232-237). In between, :229 runs `PruneSnapshotsAsync`, which does a **separate** `SaveChangesAsync`. If that throws (SQLITE_BUSY, disk error), the DB is at seq N but the actor stays at N-1, no client got the seq-N snapshot, and the next intent recomputes seq N → collides on the `(RoomCode,Seq)` PK → every subsequent intent fails until process restart. Prune runs on the hot path.
**Fix:** Apply in-memory state + broadcast *immediately* after the :228 SaveChanges, before any further fallible DB work. Make `PruneSnapshotsAsync` best-effort (try/catch + log).

### 1.2 Undo is non-atomic — both wedges the actor and corrupts crash-replay
`src/Lantern.Server/Rooms/RoomActor.cs:263-294` (esp. SaveChanges at :274 and :284; `_state` reassigned at :278) + `Lantern.Engine/ShowdownEngine.cs:44-59`
`UndoAsync` does **two** SaveChanges (journal+`LastSeq` at :274, then the re-folded snapshot at :284) and mutates `_state` between them, with `_lastSeq`/`_reverted`/`_committedIntents` updated only at :288-290. Two distinct failure modes:
- **I/O failure mid-undo** (second save or prune throws): same wedge as 1.1 — `_state` advanced but `_lastSeq` not, next intent collides with the persisted undo row.
- **Process crash between :274 and :284:** journal/`LastSeq` say the undo happened but no snapshot exists at U. `RehydrateAsync` (:98-104) loads the snapshot at U-1 (which still bakes in the reverted intent), and `Fold` computes its `reverted` set only from replayed tail entries (`ShowdownEngine.cs:46-49`), skipping the `UndoIntent` marker (:54). The reverted seq U-1 is the base snapshot, never in the tail, so its effect can never be stripped → the reverted intent stays applied, `LastSeq` advances past the undo, the undo's `ClientIntentId` is indexed committed (so the client never retries), room is **permanently wrong**. Every undo hits this window. CommitAsync (:228) is already atomic, so Undo is an inconsistent deviation.

**Fix:** Compute the re-folded state in memory (existing entries + the new undo entry), persist journal row + `RoomRow.LastSeq` + snapshot in a **single** SaveChanges, then mutate `_state`/`_lastSeq`/`_reverted`/`_committedIntents` and broadcast only on success. Keep prune best-effort. (Alternatively/additionally, make rehydrate fold from genesis when latest snapshot seq < `room.LastSeq`.)

### 1.3 Card-derived values are recomputed from the mutable content pack at fold time
`src/Lantern.Engine/ShowdownEngine.cs:112, 130, 148, 153` (deck build :70-73)
`HitsOn`, `WoundsOn`, `HasCriticalSlot`, `IsTrap`, and the deck multiset/order are all re-derived from the *current* pack during `Fold`, never journaled. `UndoAsync` re-folds the full journal from genesis against the live in-memory pack (`RoomActor.cs:278`). Packs load once at startup from `content/packs` + `content/local` and can be edited between sessions (SPEC §10.1 explicitly anticipates fixing a wrong to-hit/wound formula). After such an edit, an undo silently re-derives the whole timeline — changed deck → different seeded shuffle/draws, changed formula → different target numbers, changed flags → different trap/crit — cascading into different FSM transitions (Resolved vs AwaitingWounds). The persisted snapshots embed the *old* values, so snapshot-load and genesis-refold disagree. This also contradicts SPEC §4's stated "copied into state at StartShowdown … replayable even if the pack is later edited" invariant.
**Fix:** Journal the derived facts — store `HitsOn` on the DeclareAttack result and `WoundsOn`/`HasCriticalSlot`/`IsTrap` plus the resolved deck multiset at draw/start time, so `Fold` replays recorded values. (Or: have `UndoAsync` fold from the nearest snapshot rather than genesis, and pin a content-pack hash per room.)

---

## 2. Should-fix — medium

### 2.1 `_roster` / `_state` / `_lastSeq` are touched off-gate on every presence & snapshot path (data race)
`src/Lantern.Server/Rooms/RoomActor.cs` — writer under `_gate` at :123 (JoinAsync Add); **ungated** readers/writers: `Roster()` :318-319, `BroadcastPresenceAsync` :152-153, `SnapshotMessage`/`SendSnapshotToAsync` :155-156/:321-322, `MarkDisconnectedAsync` :158-162, `ResumeAsync` :139-145, `BindConnection` :147-150 (called outside any lock at `GameRoomService.cs:50/62/75`).
`_roster` is a plain `Dictionary<string,Member>` (:30). SignalR runs connections' invocations in parallel on the shared per-room actor, so a gated `JoinAsync` Add can run concurrently with an ungated enumeration/lookup → undefined behavior for `Dictionary` (throw "collection was modified", torn read, or bucket corruption). The same paths read `_state`+`_lastSeq` non-atomically vs the gated writes in `CommitAsync` (:232-233), producing torn (state, seq) snapshot/presence labels. This violates the class's documented single-writer invariant. *(Consolidates findings #1, #2, #3, #4, #8-auth, and the `_lastSeq` torn-label item.)*
**Fix:** Treat this as a class-wide cleanup: acquire `_gate` for *every* method that reads or mutates `_roster`/`_state`/`_lastSeq`; build the snapshot/presence DTO under the gate, then `await` the hub send outside it. Fold `BindConnection` into the gated Create/Join/Resume flow. Wrap the whole `ResumeAsync` body in `WaitAsync/try/finally` like `JoinAsync` (the dropped `await Task.CompletedTask` at :143 shows the sync was lost).

### 2.2 Client-supplied `MasterSeed` lets any player rig all showdown RNG
`src/Lantern.Server/Rooms/RoomActor.cs:198-199`, `Lantern.Contracts/Intents.cs:34`, `Lantern.Engine/ShowdownEngine.cs:67-73`
The actor mints a seed only when `MasterSeed` is null; a non-null client value passes straight into `RngCursors.Seeded(...)`, which drives the hit-location shuffle and all draws. A player can precompute a favorable seed offline. `StartShowdownIntent` isn't even host-only (:181). Contradicts SPEC §11 (actor-minted deterministic seeds).
**Fix:** Always overwrite server-side: `intent = ssi with { MasterSeed = Crypto.NewSeed() }` for every `StartShowdownIntent`, or remove `MasterSeed` from the client-facing intent entirely.

### 2.3 HostOverride whitelist omits the spec-required `attack.<cardId>.outcome` path
`src/Lantern.Engine/ShowdownEngine.cs:222-248`
SPEC §5.7 defines exactly five override paths; the handler implements four (status, `monster.totalWounds`, `monster.woundsByLocation.<loc>`, `survivor.<id>.dead`) and rejects `attack.<cardId>.outcome` at the :248 fallthrough. Correcting a mis-entered wound/crit/fail outcome is a primary host-override use case.
**Fix:** Add a branch matching `attack.<cardId>.outcome`: locate the `DrawnLocation` with that `CardId` in `state.Attack.DrawnLocations`, `SetItem` its `Result` via `ParseOutcome(...)`; reject if no attack is in flight or the card id is absent. Don't recompute Status (unlike EnterWound) — just set the result.

### 2.4 `DrawHitLocations` over-draws past a Trap instead of ending immediately
`src/Lantern.Engine/ShowdownEngine.cs:147-157`, `HitLocationDeck.cs:32-45`
SPEC §4.6/§2.6 say a Trap ends the attack *immediately*. `HitLocationDeck.Draw` pops the full `HitCount` unconditionally; the trap is detected only after the whole batch. With `HitCount>1` and a mid-batch trap, post-trap cards are removed from the draw pile, recorded in `DrawnThisAttack`, later discarded/reshuffled (corrupting deck order for later attacks), can trigger an early seeded reshuffle, and are never wound-rolled (status jumps to Resolved with `Result=null` — they silently vanish).
**Fix:** Stop the draw loop as soon as an `IsTrap` card is popped, returning up to and including the trap; leave the remaining count undrawn.

---

## 3. Nice-to-have — low

- **`busy_timeout` never applied** — `Program.cs:14,32`: only `journal_mode=WAL` runs (on a throwaway startup connection; WAL persists via the file header, busy_timeout does not). Each actor opens its own connection, so cross-room writers rely on Microsoft.Data.Sqlite's ~30s command-timeout retry instead of the intended 5s bound. Append `Default Timeout`/`busy_timeout` via the connection string or a per-open interceptor. (Compounds 1.1/1.2.)
- **`SetRoomPassword` dedup not journaled** — `RoomActor.cs:241-250`: writes no JournalRow but registers `_committedIntents[id]=_lastSeq` (an unrelated gameplay seq) and acks with it. After restart the dedup key is lost (rebuilt only from journal at :106-107), so a stale retry can re-apply an old password. Journal it as a real entry, or stop registering it and document it as non-idempotent control.
- **No ContractVersion resume-guard** — `RoomActor.cs:99`: `RehydrateAsync` deserializes `snap.StateJson` without comparing `snap.ContractVersion` to `Protocol.Version`; `JournalRow` has no `SchemaVersion` (`Entities.cs:30-39`). SPEC §7 specifies a `needs_migration` RoomError on mismatch (code reserved but never emitted). Unreachable today (`Protocol.Version`==`MinSupported`==1) but the specced safety net is absent for any future bump. Add the version compare + a `SchemaVersion` column. (The `EnsureCreatedAsync`-vs-migrations half is acceptable MVP tech debt — just document it.)
- **`RecordMonsterTurn`/`EndShowdown` lack an `Ended` guard** — `ShowdownEngine.cs:36,38`: unlike `AddSurvivor` (:100), they accept intents and advance seq on a terminal state. `MonsterTurnNote` is informational, so impact is cosmetic. Mirror the `AddSurvivor` reject.
- **README claims a nonexistent CI content-policy check** — `README.md:36`: no `.github/`, hooks, or enforcement script exist; only `.gitignore` guards the IP boundary. Either add the CI job or correct the README.
- **`.gitignore` image guards are incomplete** — `.gitignore:49-55`: case-sensitive (`card.PNG`/`.JPG`/`.HEIC` not ignored), omits `gif/bmp/svg/tif/pdf`, and scoped only to `content/**` (leaves `tools/` — the documented OCR/ingest dir — and repo root unguarded). Best fix: invert `content/packs/` to ignore everything except `*.json`/README, and add repo-wide image ignores with an allowlist for legit UI assets. (Consolidates findings #15, #16, #17.)

---

## Ship-readiness verdict

**Ship for personal use after fixing §1.** The §1 items (commit-prune wedge, non-atomic undo, pack-derived refold divergence) are the only ones that can *permanently* corrupt or brick a room, and undo is a normal in-game action — for a save-the-campaign tool that's the real risk. They are small, localized fixes (atomic SaveChanges + apply-state-before-fallible-work + journal the derived facts). The §2 concurrency race is genuine but very unlikely to bite at ~4 players; bundle the gate cleanup with the §1 work since you'll be in `RoomActor` anyway. §2.2–§2.4 are correctness/feature gaps with manual workarounds. §3 is hardening and doc accuracy. With §1 done and §2 ideally cleaned up, this is fit for a trusted self-hosted KDM group.
