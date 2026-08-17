using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record SyncErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("batch_id")] string? BatchId,
    [property: JsonPropertyName("retry_after_seconds")] int? RetryAfterSeconds
);
