using System.Collections.Generic;

namespace RenderByte.Sync.Contracts;

public sealed class SyncStockBatchRequest
{
    public string BatchId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public int BranchId { get; set; }
    
    public List<SyncStockDto> Stocks { get; set; } = new();
}

public sealed class SyncStockDto
{
    public string BusinessKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Depo { get; set; }
    public int ArticleId { get; set; }
    public string Bulto { get; set; } = string.Empty;
    
    // Decimales representados como string invariant
    public string? Costo { get; set; }
    public string? Precio { get; set; }
    public string? Saldo { get; set; }
    public string? Piezas { get; set; }
    
    public bool IsPresent { get; set; }
}
