using System.Collections.Concurrent;
using Lantern.Contracts;
using Lantern.Server.Content;
using Lantern.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lantern.Server.Rooms;

public interface IGameRoomService
{
    Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest req, string connectionId);
    Task<JoinRoomResult> JoinRoomAsync(JoinRoomRequest req, string connectionId);
    Task<JoinRoomResult> ResumeAsync(ResumeRequest req, string connectionId);
    Task<IntentAck> SubmitIntentAsync(IntentEnvelope env);
    Task RequestSnapshotAsync(string roomCode, string connectionId);
    Task OnDisconnectedAsync(string connectionId);
}

public sealed class GameRoomService(
    IServiceScopeFactory scopes,
    IHubContext<GameHubV1, IGameClient> hub,
    ContentService content,
    IConfiguration config,
    ILoggerFactory loggers) : IGameRoomService
{
    private readonly ConcurrentDictionary<string, RoomActor> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Room, string Player)> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ILogger _log = loggers.CreateLogger<GameRoomService>();

    public async Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest req, string connectionId)
    {
        RequireContractVersion(req.ContractVersion);

        var required = config["LANTERN_HOST_PASSWORD"];
        if (!string.IsNullOrEmpty(required) && !Crypto.FixedTimeEquals(req.HostPassword, required))
            throw new HubException("host_auth_failed");
        if (string.IsNullOrEmpty(required))
            _log.LogWarning("LANTERN_HOST_PASSWORD is not set — room creation is unauthenticated (dev only).");

        var pack = content.GetPack(req.ContentPackId) ?? throw new HubException("unknown_content_pack");

        var actor = new RoomActor(Crypto.NewRoomCode(), pack, scopes, hub, loggers.CreateLogger<RoomActor>());
        var result = await actor.InitNewAsync(req, connectionId);
        _rooms[result.RoomCode] = actor;

        await hub.Groups.AddToGroupAsync(connectionId, result.RoomCode);
        _connections[connectionId] = (result.RoomCode, result.PlayerId);
        return result;
    }

    public async Task<JoinRoomResult> JoinRoomAsync(JoinRoomRequest req, string connectionId)
    {
        RequireContractVersion(req.ContractVersion);
        var actor = await GetOrLoadAsync(req.RoomCode) ?? throw new HubException("room_not_found");

        var result = await actor.JoinAsync(req, connectionId);
        await hub.Groups.AddToGroupAsync(connectionId, req.RoomCode);
        _connections[connectionId] = (req.RoomCode, result.PlayerId);
        await actor.BroadcastPresenceAsync();
        return result;
    }

    public async Task<JoinRoomResult> ResumeAsync(ResumeRequest req, string connectionId)
    {
        RequireContractVersion(req.ContractVersion);
        var actor = await GetOrLoadAsync(req.RoomCode) ?? throw new HubException("room_not_found");

        var result = await actor.ResumeAsync(req, connectionId);
        await hub.Groups.AddToGroupAsync(connectionId, req.RoomCode);
        _connections[connectionId] = (req.RoomCode, result.PlayerId);
        await actor.BroadcastPresenceAsync();
        return result;
    }

    public async Task<IntentAck> SubmitIntentAsync(IntentEnvelope env)
    {
        var actor = await GetOrLoadAsync(env.RoomCode);
        if (actor is null) return new IntentAck(false, null, "room_not_found", env.ClientIntentId);
        return await actor.SubmitAsync(env);
    }

    public async Task RequestSnapshotAsync(string roomCode, string connectionId)
    {
        var actor = await GetOrLoadAsync(roomCode);
        if (actor is not null) await actor.SendSnapshotToAsync(connectionId);
    }

    public async Task OnDisconnectedAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var info) && _rooms.TryGetValue(info.Room, out var actor))
            await actor.MarkDisconnectedAsync(info.Player, connectionId);
    }

    private static void RequireContractVersion(int v)
    {
        if (v < Protocol.MinSupported || v > Protocol.Version) throw new HubException("contract_version_mismatch");
    }

    private async Task<RoomActor?> GetOrLoadAsync(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var existing)) return existing;

        await _loadLock.WaitAsync();
        try
        {
            if (_rooms.TryGetValue(roomCode, out existing)) return existing;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LanternDbContext>();
            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.RoomCode == roomCode);
            if (room is null || room.Status == "ended") return null;

            var pack = content.GetPack(room.ContentPackId);
            if (pack is null) { _log.LogError("Room {Room} references missing pack {Pack}", roomCode, room.ContentPackId); return null; }

            var actor = new RoomActor(roomCode, pack, scopes, hub, loggers.CreateLogger<RoomActor>());
            await actor.RehydrateAsync();
            _rooms[roomCode] = actor;
            return actor;
        }
        finally { _loadLock.Release(); }
    }
}
