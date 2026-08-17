using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record SyncBatchResponse(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("inserted")] int Inserted,
    [property: JsonPropertyName("duplicates")] int Duplicates,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt
);
