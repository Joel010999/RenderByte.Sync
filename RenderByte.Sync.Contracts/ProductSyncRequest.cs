using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record ProductSyncDto(
    [property: JsonPropertyName("business_key")] string BusinessKey,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("article_id")] int ArticleId,
    [property: JsonPropertyName("payload")] string Payload
);

public sealed record ProductSyncRequest(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("branch_id")] int BranchId,
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("products")] IReadOnlyList<ProductSyncDto> Products
);
