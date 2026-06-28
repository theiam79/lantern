using System.Collections.Immutable;

namespace Lantern.Contracts;

// Server → client payloads. For the MVP every state change is a full-state push
// (one message type carries the whole projected state at a seq); the client renders
// the latest and never folds incremental effects. See SPEC §11.1.

public sealed record SnapshotPayload(string RoomCode, ShowdownState? State, RosterDto Roster);

public sealed record RoomErrorPayload(string Code, string Message, string? RejectedIntentId);

public sealed record RosterEntryDto(string PlayerId, string DisplayName, bool IsHost, bool Connected);

public sealed record RosterDto(ImmutableArray<RosterEntryDto> Players);
