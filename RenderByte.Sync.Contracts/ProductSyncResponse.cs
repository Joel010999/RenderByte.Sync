using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record ProductSyncResponse(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("inserted")] int Inserted,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt
);
