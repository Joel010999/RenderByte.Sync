using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record SyncBatchRequest(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("branch_id")] int BranchId,
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("sent_at")] DateTimeOffset SentAt,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("movements")] IReadOnlyList<SyncMovementDto> Movements
);
