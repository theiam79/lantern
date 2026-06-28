namespace Lantern.Contracts;

// Handshake requests + acks. Results are returned from the hub invoke (not broadcast);
// the result carries the initial snapshot, so there is no duplicate pushed snapshot.

public sealed record CreateRoomRequest(
    string HostPassword,
    string DisplayName,
    string ContentPackId,
    string? RoomPassword,
    int ContractVersion
);

public sealed record JoinRoomRequest(string RoomCode, string DisplayName, string? RoomPassword, int ContractVersion);

public sealed record ResumeRequest(string RoomCode, string PlayerId, string PlayerToken, int ContractVersion);

public sealed record CreateRoomResult(
    string RoomCode,
    string PlayerId,
    string PlayerToken,
    int ContractVersion,
    ServerMessage<SnapshotPayload> Snapshot
);

public sealed record JoinRoomResult(
    string RoomCode,
    string PlayerId,
    string PlayerToken,
    int ContractVersion,
    ServerMessage<SnapshotPayload> Snapshot
);

public sealed record IntentAck(bool Accepted, long? CommittedSeq, string? RejectReason, string ClientIntentId);
