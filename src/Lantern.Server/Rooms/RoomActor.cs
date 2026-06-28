using System.Text.Json;
using Lantern.Contracts;
using Lantern.Engine;
using Lantern.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lantern.Server.Rooms;

/// <summary>
/// Authoritative single-writer for one room. <see cref="_gate"/> serializes EVERY method that
/// reads or mutates <c>_roster</c>/<c>_state</c>/<c>_lastSeq</c> (SignalR invokes the shared actor
/// from multiple connections concurrently). Each gameplay commit is atomic: Reduce → persist
/// journal + room + snapshot in ONE SaveChanges → only then update in-memory state + broadcast.
/// Snapshot pruning is best-effort (never on the commit's critical path). State is rebuilt from
/// the latest snapshot + journal tail on rehydrate; undo re-folds from the nearest prior snapshot.
/// </summary>
internal sealed class RoomActor(
    string roomCode,
    IContentPack pack,
    IServiceScopeFactory scopes,
    IHubContext<GameHubV1, IGameClient> hub,
    ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Member> _roster = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _committedIntents = new(StringComparer.Ordinal);
    private readonly List<long> _gameplaySeqs = [];
    private readonly HashSet<long> _reverted = [];

    private ShowdownState? _state;
    private long _lastSeq;
    private string? _roomPasswordHash;

    public string RoomCode => roomCode;

    private sealed class Member
    {
        public required string PlayerId { get; init; }
        public required string DisplayName { get; init; }
        public required bool IsHost { get; init; }
        public required string TokenHash { get; init; }
        public bool Connected { get; set; }
        public string? ConnectionId { get; set; }
    }

    // ---------- lifecycle ----------

    public async Task<CreateRoomResult> InitNewAsync(CreateRoomRequest req, string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            var playerId = Guid.NewGuid().ToString("N");
            var token = Crypto.NewToken();
            _roomPasswordHash = string.IsNullOrEmpty(req.RoomPassword) ? null : Crypto.Hash(req.RoomPassword);
            var now = DateTimeOffset.UtcNow;
            _roster[playerId] = new Member { PlayerId = playerId, DisplayName = req.DisplayName, IsHost = true, TokenHash = Crypto.Hash(token), Connected = true, ConnectionId = connectionId };

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.Rooms.Add(new RoomRow
            {
                RoomCode = roomCode, ContentPackId = req.ContentPackId, RoomPasswordHash = _roomPasswordHash,
                HostPlayerId = playerId, LastSeq = 0, Status = "lobby", ContractVersion = Protocol.Version, CreatedAt = now, UpdatedAt = now,
            });
            db.RoomMembers.Add(new RoomMemberRow { RoomCode = roomCode, PlayerId = playerId, DisplayName = req.DisplayName, IsHost = true, PlayerTokenHash = _roster[playerId].TokenHash, JoinedAt = now, LastSeenAt = now });
            db.Journal.Add(new JournalRow { RoomCode = roomCode, Seq = 0, ActorPlayerId = playerId, CommittedAt = now });
            db.Snapshots.Add(new SnapshotRow { RoomCode = roomCode, Seq = 0, ContractVersion = Protocol.Version, StateJson = null, At = now });
            await db.SaveChangesAsync();

            return new CreateRoomResult(roomCode, playerId, token, Protocol.Version, SnapshotMessage());
        }
        finally { _gate.Release(); }
    }

    public async Task RehydrateAsync()
    {
        // Runs before the actor is published to other connections — no gate needed.
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
        var room = await db.Rooms.FindAsync(roomCode) ?? throw new InvalidOperationException("room missing");
        _roomPasswordHash = room.RoomPasswordHash;
        _lastSeq = room.LastSeq;

        foreach (var m in await db.RoomMembers.Where(m => m.RoomCode == roomCode).ToListAsync())
            _roster[m.PlayerId] = new Member { PlayerId = m.PlayerId, DisplayName = m.DisplayName, IsHost = m.IsHost, TokenHash = m.PlayerTokenHash };

        var snap = await db.Snapshots.Where(s => s.RoomCode == roomCode).OrderByDescending(s => s.Seq).FirstOrDefaultAsync();
        var fromState = snap?.StateJson is { } sj ? JsonSerializer.Deserialize<ShowdownState>(sj, Json) : null;
        var fromSeq = snap?.Seq ?? -1;

        var allRows = await db.Journal.Where(j => j.RoomCode == roomCode).OrderBy(j => j.Seq).ToListAsync();
        var tail = allRows.Where(j => j.Seq > fromSeq).Select(ToEntry).ToList();
        _state = ShowdownEngine.Fold(fromState, tail, pack);
        foreach (var r in allRows) IndexCommitted(ToEntry(r));

        logger.LogInformation("Rehydrated room {Room} at seq {Seq} ({Members} members)", roomCode, _lastSeq, _roster.Count);
    }

    public async Task<JoinRoomResult> JoinAsync(JoinRoomRequest req, string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_roomPasswordHash is not null && (req.RoomPassword is null || Crypto.Hash(req.RoomPassword) != _roomPasswordHash))
                throw new HubException("room_password_required");

            var playerId = Guid.NewGuid().ToString("N");
            var token = Crypto.NewToken();
            var now = DateTimeOffset.UtcNow;
            _roster[playerId] = new Member { PlayerId = playerId, DisplayName = req.DisplayName, IsHost = false, TokenHash = Crypto.Hash(token), Connected = true, ConnectionId = connectionId };

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.RoomMembers.Add(new RoomMemberRow { RoomCode = roomCode, PlayerId = playerId, DisplayName = req.DisplayName, IsHost = false, PlayerTokenHash = _roster[playerId].TokenHash, JoinedAt = now, LastSeenAt = now });
            await db.SaveChangesAsync();

            return new JoinRoomResult(roomCode, playerId, token, Protocol.Version, SnapshotMessage());
        }
        finally { _gate.Release(); }
    }

    public async Task<JoinRoomResult> ResumeAsync(ResumeRequest req, string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_roster.TryGetValue(req.PlayerId, out var m) || !Crypto.FixedTimeEquals(Crypto.Hash(req.PlayerToken), m.TokenHash))
                throw new HubException("bad_token");
            m.ConnectionId = connectionId;
            m.Connected = true;
            return new JoinRoomResult(roomCode, req.PlayerId, req.PlayerToken, Protocol.Version, SnapshotMessage());
        }
        finally { _gate.Release(); }
    }

    public async Task BroadcastPresenceAsync()
    {
        await _gate.WaitAsync();
        try { await hub.Clients.Group(roomCode).Presence(new ServerMessage<RosterDto>(Protocol.Version, _lastSeq, "presence", Roster())); }
        finally { _gate.Release(); }
    }

    public async Task SendSnapshotToAsync(string connectionId)
    {
        await _gate.WaitAsync();
        try { await hub.Clients.Client(connectionId).Snapshot(SnapshotMessage()); }
        finally { _gate.Release(); }
    }

    public async Task MarkDisconnectedAsync(string playerId, string connectionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_roster.TryGetValue(playerId, out var m) && m.ConnectionId == connectionId) { m.Connected = false; m.ConnectionId = null; }
            await hub.Clients.Group(roomCode).Presence(new ServerMessage<RosterDto>(Protocol.Version, _lastSeq, "presence", Roster()));
        }
        finally { _gate.Release(); }
    }

    // ---------- gameplay ----------

    public async Task<IntentAck> SubmitAsync(IntentEnvelope env)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_roster.TryGetValue(env.PlayerId, out var member) || !Crypto.FixedTimeEquals(Crypto.Hash(env.PlayerToken), member.TokenHash))
                return new IntentAck(false, null, "bad_token", env.ClientIntentId);

            if (_committedIntents.TryGetValue(env.ClientIntentId, out var prior))
                return new IntentAck(true, prior, null, env.ClientIntentId);

            Intent intent;
            try { intent = JsonSerializer.Deserialize<Intent>(env.Intent.GetRawText(), Json) ?? throw new JsonException("null"); }
            catch (Exception) { return new IntentAck(false, null, "intent_rejected", env.ClientIntentId); }

            var hostOnly = intent is SetRoomPasswordIntent or UndoIntent or HostOverrideIntent or EndShowdownIntent;
            if (hostOnly && !member.IsHost)
                return new IntentAck(false, null, "host_only", env.ClientIntentId);

            return intent switch
            {
                SetRoomPasswordIntent sp => await SetRoomPasswordAsync(sp, env.ClientIntentId),
                UndoIntent undo => await UndoAsync(undo, env.PlayerId, env.ClientIntentId),
                _ => await CommitAsync(intent, env.PlayerId, env.ClientIntentId),
            };
        }
        finally { _gate.Release(); }
    }

    // gate is held by SubmitAsync for all of the methods below.

    private async Task<IntentAck> CommitAsync(Intent intent, string actorPlayerId, string clientIntentId)
    {
        // Always mint the seed server-side so no client can rig the deck (and replay stays deterministic).
        if (intent is StartShowdownIntent ssi)
            intent = ssi with { MasterSeed = Crypto.NewSeed() };

        var seq = _lastSeq + 1;
        var result = ShowdownEngine.Reduce(_state, intent, seq, pack);
        if (!result.Accepted)
            return new IntentAck(false, null, result.RejectReason, clientIntentId);

        var now = DateTimeOffset.UtcNow;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.Journal.Add(new JournalRow { RoomCode = roomCode, Seq = seq, ActorPlayerId = actorPlayerId, IntentJson = JsonSerializer.Serialize(intent, Json), ClientIntentId = clientIntentId, CommittedAt = now });
            db.Snapshots.Add(SnapshotRowFor(seq, result.State, now));
            await UpdateRoomAsync(db, seq, result.State, now);
            await db.SaveChangesAsync(); // atomic: journal + snapshot + room in one transaction
        }

        // Only after the durable commit: advance in-memory state and tell clients.
        _state = result.State;
        _lastSeq = seq;
        _committedIntents[clientIntentId] = seq;
        _gameplaySeqs.Add(seq);
        await hub.Clients.Group(roomCode).Snapshot(SnapshotMessage());

        await BestEffortPruneAsync();
        return new IntentAck(true, seq, null, clientIntentId);
    }

    private async Task<IntentAck> UndoAsync(UndoIntent undo, string actorPlayerId, string clientIntentId)
    {
        var live = _gameplaySeqs.Where(s => !_reverted.Contains(s)).OrderBy(s => s).ToList();
        var toRevert = undo.TargetSeq is { } t ? live.Where(s => s > t).ToList() : live.TakeLast(Math.Max(1, undo.Count)).ToList();
        if (toRevert.Count == 0)
            return new IntentAck(false, null, "nothing-to-undo", clientIntentId);

        var minRev = toRevert.Min();
        var revertedAll = new HashSet<long>(_reverted);
        foreach (var s in toRevert) revertedAll.Add(s);

        var seq = _lastSeq + 1;
        var now = DateTimeOffset.UtcNow;
        ShowdownState? newState;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            // Re-fold from the nearest snapshot strictly before the earliest reverted seq, then
            // replay the (non-reverted) tail. With per-commit snapshots this is usually just the
            // base snapshot (no Reduce re-execution), which also avoids re-deriving against an
            // edited content pack.
            var baseSnap = await db.Snapshots.Where(s => s.RoomCode == roomCode && s.Seq < minRev).OrderByDescending(s => s.Seq).FirstOrDefaultAsync();
            var baseState = baseSnap?.StateJson is { } sj ? JsonSerializer.Deserialize<ShowdownState>(sj, Json) : null;
            var baseSeq = baseSnap?.Seq ?? -1;
            var rows = await db.Journal.Where(j => j.RoomCode == roomCode && j.Seq > baseSeq).OrderBy(j => j.Seq).ToListAsync();
            var tail = rows.Select(ToEntry).Where(e => !revertedAll.Contains(e.Seq)).ToList();
            newState = ShowdownEngine.Fold(baseState, tail, pack);

            db.Journal.Add(new JournalRow { RoomCode = roomCode, Seq = seq, ActorPlayerId = actorPlayerId, IntentJson = JsonSerializer.Serialize<Intent>(undo, Json), RevertedSeqsJson = JsonSerializer.Serialize(toRevert), ClientIntentId = clientIntentId, CommittedAt = now });
            db.Snapshots.Add(SnapshotRowFor(seq, newState, now));
            await UpdateRoomAsync(db, seq, newState, now);
            await db.SaveChangesAsync(); // atomic
        }

        _state = newState;
        _lastSeq = seq;
        foreach (var s in toRevert) _reverted.Add(s);
        _committedIntents[clientIntentId] = seq;
        await hub.Clients.Group(roomCode).Snapshot(SnapshotMessage());

        await BestEffortPruneAsync();
        return new IntentAck(true, seq, null, clientIntentId);
    }

    private async Task<IntentAck> SetRoomPasswordAsync(SetRoomPasswordIntent sp, string clientIntentId)
    {
        // Room-level control (not part of the gameplay journal). Intentionally not deduped by
        // ClientIntentId — it carries no seq and is idempotent in effect.
        _roomPasswordHash = string.IsNullOrEmpty(sp.Password) ? null : Crypto.Hash(sp.Password);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
        var room = await db.Rooms.FindAsync(roomCode);
        if (room is not null) { room.RoomPasswordHash = _roomPasswordHash; room.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        return new IntentAck(true, _lastSeq, null, clientIntentId);
    }

    // ---------- helpers ----------

    private SnapshotRow SnapshotRowFor(long seq, ShowdownState? state, DateTimeOffset at) => new()
    {
        RoomCode = roomCode, Seq = seq, ContractVersion = Protocol.Version,
        StateJson = state is null ? null : JsonSerializer.Serialize(state, Json), At = at,
    };

    private async Task UpdateRoomAsync(LanternDbContext db, long seq, ShowdownState? state, DateTimeOffset now)
    {
        var room = await db.Rooms.FindAsync(roomCode);
        if (room is null) return;
        room.LastSeq = seq;
        room.UpdatedAt = now;
        room.Status = state?.Status == ShowdownStatus.Ended ? "ended" : state is not null ? "showdown" : room.Status;
    }

    private async Task BestEffortPruneAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            var stale = await db.Snapshots.Where(s => s.RoomCode == roomCode).OrderByDescending(s => s.Seq).Skip(5).ToListAsync();
            if (stale.Count > 0) { db.Snapshots.RemoveRange(stale); await db.SaveChangesAsync(); }
        }
        catch (Exception ex) { logger.LogWarning(ex, "snapshot prune failed for {Room} (non-fatal)", roomCode); }
    }

    private void IndexCommitted(JournalEntry e)
    {
        if (e.ClientIntentId is { } cid) _committedIntents[cid] = e.Seq;
        if (e.Seq == 0) return;
        foreach (var r in e.RevertedSeqs) _reverted.Add(r);
        if (e.Intent is not null and not UndoIntent) _gameplaySeqs.Add(e.Seq);
    }

    private static JournalEntry ToEntry(JournalRow r) => new(
        r.RoomCode, r.Seq, r.ActorPlayerId, r.CommittedAt,
        r.IntentJson is null ? null : JsonSerializer.Deserialize<Intent>(r.IntentJson, Json),
        r.RevertedSeqsJson is null ? [] : [.. JsonSerializer.Deserialize<long[]>(r.RevertedSeqsJson, Json) ?? []],
        r.ClientIntentId);

    private RosterDto Roster() =>
        new([.. _roster.Values.Select(m => new RosterEntryDto(m.PlayerId, m.DisplayName, m.IsHost, m.Connected))]);

    private ServerMessage<SnapshotPayload> SnapshotMessage() =>
        new(Protocol.Version, _lastSeq, "snapshot", new SnapshotPayload(roomCode, _state, Roster()));
}
