using System;

namespace RenderByte.Sync.Contracts;

public sealed class SyncStockBatchResponse
{
    public string BatchId { get; set; } = string.Empty;
    public int Accepted { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public DateTime ReceivedAt { get; set; }
}
