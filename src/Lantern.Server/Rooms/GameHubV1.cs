using Lantern.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Lantern.Server.Rooms;

/// <summary>Client callbacks. MVP uses full-state <see cref="Snapshot"/> pushes (no incremental deltas).</summary>
public interface IGameClient
{
    Task Snapshot(ServerMessage<SnapshotPayload> msg);
    Task RoomError(ServerMessage<RoomErrorPayload> msg);
    Task Presence(ServerMessage<RosterDto> msg);
}

/// <summary>
/// Transient, stateless hub at /hub/v1. Resolves connection context and delegates to the
/// singleton <see cref="IGameRoomService"/>; never holds game state. Handshake failures throw
/// <see cref="HubException"/> (the client's invoke rejects); gameplay rejects come back in the ack.
/// </summary>
public sealed class GameHubV1(IGameRoomService rooms) : Hub<IGameClient>
{
    public Task<CreateRoomResult> CreateRoom(CreateRoomRequest req) => rooms.CreateRoomAsync(req, Context.ConnectionId);

    public Task<JoinRoomResult> JoinRoom(JoinRoomRequest req) => rooms.JoinRoomAsync(req, Context.ConnectionId);

    public Task<JoinRoomResult> Resume(ResumeRequest req) => rooms.ResumeAsync(req, Context.ConnectionId);

    public Task<IntentAck> SubmitIntent(IntentEnvelope env) => rooms.SubmitIntentAsync(env);

    public Task RequestSnapshot(string roomCode) => rooms.RequestSnapshotAsync(roomCode, Context.ConnectionId);

    public override Task OnDisconnectedAsync(Exception? exception) => rooms.OnDisconnectedAsync(Context.ConnectionId);
}
