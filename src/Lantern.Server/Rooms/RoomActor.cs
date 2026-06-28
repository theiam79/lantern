using System.Collections.Immutable;
using System.Text.Json;
using Lantern.Contracts;
using Lantern.Engine;
using Lantern.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lantern.Server.Rooms;

/// <summary>
/// Authoritative single-writer for one room. A SemaphoreSlim serializes all mutations, so
/// intents commit in order: validate → dedupe → Reduce → persist (journal + room in one
/// SaveChanges, before broadcast) → full-state Snapshot push. State is held in memory and
/// rebuilt from snapshot + journal on rehydrate.
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
    private string _contentPackId = pack.PackId;
    private string? _roomPasswordHash;
    private string _hostPlayerId = "";

    public string RoomCode => roomCode;

    private sealed class Member
    {
        public required string PlayerId { get; init; }
        public required string DisplayName { get; set; }
        public required bool IsHost { get; init; }
        public required string TokenHash { get; init; }
        public bool Connected { get; set; }
        public string? ConnectionId { get; set; }
    }

    // ---------- lifecycle ----------

    public async Task<CreateRoomResult> InitNewAsync(CreateRoomRequest req)
    {
        var playerId = Guid.NewGuid().ToString("N");
        var token = Crypto.NewToken();
        _hostPlayerId = playerId;
        _contentPackId = req.ContentPackId;
        _roomPasswordHash = string.IsNullOrEmpty(req.RoomPassword) ? null : Crypto.Hash(req.RoomPassword);
        var now = DateTimeOffset.UtcNow;
        _roster[playerId] = new Member { PlayerId = playerId, DisplayName = req.DisplayName, IsHost = true, TokenHash = Crypto.Hash(token), Connected = true };

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
        db.Rooms.Add(new RoomRow
        {
            RoomCode = roomCode, ContentPackId = _contentPackId, RoomPasswordHash = _roomPasswordHash,
            HostPlayerId = playerId, LastSeq = 0, Status = "lobby", ContractVersion = Protocol.Version,
            CreatedAt = now, UpdatedAt = now,
        });
        db.RoomMembers.Add(new RoomMemberRow
        {
            RoomCode = roomCode, PlayerId = playerId, DisplayName = req.DisplayName, IsHost = true,
            PlayerTokenHash = _roster[playerId].TokenHash, JoinedAt = now, LastSeenAt = now,
        });
        db.Journal.Add(new JournalRow { RoomCode = roomCode, Seq = 0, ActorPlayerId = playerId, CommittedAt = now });
        db.Snapshots.Add(new SnapshotRow { RoomCode = roomCode, Seq = 0, ContractVersion = Protocol.Version, StateJson = null, At = now });
        await db.SaveChangesAsync();

        return new CreateRoomResult(roomCode, playerId, token, Protocol.Version, SnapshotMessage());
    }

    public async Task RehydrateAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
        var room = await db.Rooms.FindAsync(roomCode) ?? throw new InvalidOperationException("room missing");
        _contentPackId = room.ContentPackId;
        _roomPasswordHash = room.RoomPasswordHash;
        _hostPlayerId = room.HostPlayerId;
        _lastSeq = room.LastSeq;

        foreach (var m in await db.RoomMembers.Where(m => m.RoomCode == roomCode).ToListAsync())
            _roster[m.PlayerId] = new Member { PlayerId = m.PlayerId, DisplayName = m.DisplayName, IsHost = m.IsHost, TokenHash = m.PlayerTokenHash };

        var snap = await db.Snapshots.Where(s => s.RoomCode == roomCode).OrderByDescending(s => s.Seq).FirstOrDefaultAsync();
        var fromState = snap?.StateJson is { } sj ? JsonSerializer.Deserialize<ShowdownState>(sj, Json) : null;
        var fromSeq = snap?.Seq ?? -1;

        var rows = await db.Journal.Where(j => j.RoomCode == roomCode && j.Seq > fromSeq).OrderBy(j => j.Seq).ToListAsync();
        var tail = rows.Select(ToEntry).ToList();
        _state = ShowdownEngine.Fold(fromState, tail, pack);

        foreach (var r in await db.Journal.Where(j => j.RoomCode == roomCode).OrderBy(j => j.Seq).ToListAsync())
            IndexCommitted(ToEntry(r));

        logger.LogInformation("Rehydrated room {Room} at seq {Seq} ({Members} members)", roomCode, _lastSeq, _roster.Count);
    }

    public async Task<JoinRoomResult> JoinAsync(JoinRoomRequest req)
    {
        await _gate.WaitAsync();
        try
        {
            if (_roomPasswordHash is not null && (req.RoomPassword is null || Crypto.Hash(req.RoomPassword) != _roomPasswordHash))
                throw new HubException("room_password_required");

            var playerId = Guid.NewGuid().ToString("N");
            var token = Crypto.NewToken();
            var now = DateTimeOffset.UtcNow;
            _roster[playerId] = new Member { PlayerId = playerId, DisplayName = req.DisplayName, IsHost = false, TokenHash = Crypto.Hash(token), Connected = true };

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.RoomMembers.Add(new RoomMemberRow
            {
                RoomCode = roomCode, PlayerId = playerId, DisplayName = req.DisplayName, IsHost = false,
                PlayerTokenHash = _roster[playerId].TokenHash, JoinedAt = now, LastSeenAt = now,
            });
            await db.SaveChangesAsync();

            return new JoinRoomResult(roomCode, playerId, token, Protocol.Version, SnapshotMessage());
        }
        finally { _gate.Release(); }
    }

    public async Task<JoinRoomResult> ResumeAsync(ResumeRequest req)
    {
        if (!_roster.TryGetValue(req.PlayerId, out var m) || Crypto.Hash(req.PlayerToken) != m.TokenHash)
            throw new HubException("bad_token");
        await Task.CompletedTask;
        return new JoinRoomResult(roomCode, req.PlayerId, req.PlayerToken, Protocol.Version, SnapshotMessage());
    }

    public void BindConnection(string playerId, string connectionId)
    {
        if (_roster.TryGetValue(playerId, out var m)) { m.ConnectionId = connectionId; m.Connected = true; }
    }

    public async Task BroadcastPresenceAsync() =>
        await hub.Clients.Group(roomCode).Presence(new ServerMessage<RosterDto>(Protocol.Version, _lastSeq, "presence", Roster()));

    public async Task SendSnapshotToAsync(string connectionId) =>
        await hub.Clients.Client(connectionId).Snapshot(SnapshotMessage());

    public async Task MarkDisconnectedAsync(string playerId, string connectionId)
    {
        if (_roster.TryGetValue(playerId, out var m) && m.ConnectionId == connectionId) { m.Connected = false; m.ConnectionId = null; }
        await BroadcastPresenceAsync();
    }

    // ---------- gameplay ----------

    public async Task<IntentAck> SubmitAsync(IntentEnvelope env)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_roster.TryGetValue(env.PlayerId, out var member) || Crypto.Hash(env.PlayerToken) != member.TokenHash)
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

    private async Task<IntentAck> CommitAsync(Intent intent, string actorPlayerId, string clientIntentId)
    {
        // Resolve a deterministic seed at StartShowdown so replay is stable (journal the resolved intent).
        if (intent is StartShowdownIntent { MasterSeed: null } ssi)
            intent = ssi with { MasterSeed = Crypto.NewSeed() };

        var seq = _lastSeq + 1;
        var result = ShowdownEngine.Reduce(_state, intent, seq, pack);
        if (!result.Accepted)
            return new IntentAck(false, null, result.RejectReason, clientIntentId);

        var now = DateTimeOffset.UtcNow;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.Journal.Add(new JournalRow
            {
                RoomCode = roomCode, Seq = seq, ActorPlayerId = actorPlayerId,
                IntentJson = JsonSerializer.Serialize(intent, Json), ClientIntentId = clientIntentId, CommittedAt = now,
            });
            db.Snapshots.Add(new SnapshotRow
            {
                RoomCode = roomCode, Seq = seq, ContractVersion = Protocol.Version,
                StateJson = result.State is null ? null : JsonSerializer.Serialize(result.State, Json), At = now,
            });
            var room = await db.Rooms.FindAsync(roomCode);
            if (room is not null)
            {
                room.LastSeq = seq;
                room.UpdatedAt = now;
                room.Status = result.State?.Status == ShowdownStatus.Ended ? "ended"
                    : result.State is not null ? "showdown" : room.Status;
            }
            await db.SaveChangesAsync();
            await PruneSnapshotsAsync(db);
        }

        _state = result.State;
        _lastSeq = seq;
        _committedIntents[clientIntentId] = seq;
        _gameplaySeqs.Add(seq);

        await hub.Clients.Group(roomCode).Snapshot(SnapshotMessage());
        return new IntentAck(true, seq, null, clientIntentId);
    }

    private async Task<IntentAck> SetRoomPasswordAsync(SetRoomPasswordIntent sp, string clientIntentId)
    {
        _roomPasswordHash = string.IsNullOrEmpty(sp.Password) ? null : Crypto.Hash(sp.Password);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
        var room = await db.Rooms.FindAsync(roomCode);
        if (room is not null) { room.RoomPasswordHash = _roomPasswordHash; room.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        _committedIntents[clientIntentId] = _lastSeq;
        return new IntentAck(true, _lastSeq, null, clientIntentId);
    }

    private async Task<IntentAck> UndoAsync(UndoIntent undo, string actorPlayerId, string clientIntentId)
    {
        var live = _gameplaySeqs.Where(s => !_reverted.Contains(s)).OrderBy(s => s).ToList();
        var toRevert = undo.TargetSeq is { } t
            ? live.Where(s => s > t).ToList()
            : live.TakeLast(Math.Max(1, undo.Count)).ToList();
        if (toRevert.Count == 0)
            return new IntentAck(false, null, "nothing-to-undo", clientIntentId);

        var seq = _lastSeq + 1;
        var now = DateTimeOffset.UtcNow;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            db.Journal.Add(new JournalRow
            {
                RoomCode = roomCode, Seq = seq, ActorPlayerId = actorPlayerId,
                IntentJson = JsonSerializer.Serialize<Intent>(undo, Json),
                RevertedSeqsJson = JsonSerializer.Serialize(toRevert), ClientIntentId = clientIntentId, CommittedAt = now,
            });
            var room = await db.Rooms.FindAsync(roomCode);
            if (room is not null) { room.LastSeq = seq; room.UpdatedAt = now; }
            await db.SaveChangesAsync();

            // Re-fold from genesis over the full journal (small at 4 players).
            var rows = await db.Journal.Where(j => j.RoomCode == roomCode).OrderBy(j => j.Seq).ToListAsync();
            _state = ShowdownEngine.Fold(null, rows.Select(ToEntry).ToList(), pack);
            db.Snapshots.Add(new SnapshotRow
            {
                RoomCode = roomCode, Seq = seq, ContractVersion = Protocol.Version,
                StateJson = _state is null ? null : JsonSerializer.Serialize(_state, Json), At = now,
            });
            await db.SaveChangesAsync();
            await PruneSnapshotsAsync(db);
        }

        foreach (var s in toRevert) _reverted.Add(s);
        _lastSeq = seq;
        _committedIntents[clientIntentId] = seq;

        await hub.Clients.Group(roomCode).Snapshot(SnapshotMessage());
        return new IntentAck(true, seq, null, clientIntentId);
    }

    // ---------- helpers ----------

    private async Task PruneSnapshotsAsync(LanternDbContext db)
    {
        var keep = await db.Snapshots.Where(s => s.RoomCode == roomCode).OrderByDescending(s => s.Seq).Skip(5).ToListAsync();
        if (keep.Count > 0) { db.Snapshots.RemoveRange(keep); await db.SaveChangesAsync(); }
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
