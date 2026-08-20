using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Persistence;

public sealed record StockState(
    string BusinessKey,
    int Depo,
    int ArticleId,
    string Bulto,
    string ContentHash,
    bool IsPresent
);

/// <summary>
/// Almacenamiento local para el estado y outbox de stock.
/// </summary>
public interface IStockStore
{
    Task<IReadOnlyDictionary<string, StockState>> GetStockStatesAsync(CancellationToken cancellationToken = default);

    Task UpsertStockStateAndOutboxAsync(
        string sourceId,
        int branchId,
        AlegonStock stock,
        string businessKey,
        string contentHash,
        string payload,
        CancellationToken cancellationToken = default);

    Task MarkStockMissingAndCreateTombstoneAsync(
        string sourceId,
        int branchId,
        string businessKey,
        int depo,
        int articleId,
        string bulto,
        string contentHash,
        CancellationToken cancellationToken = default);
        
    Task<long> GetPendingOutboxCountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockOutboxMessage>> GetPendingStockOutboxAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkStockOutboxSentAsync(long id, CancellationToken cancellationToken = default);
    Task MarkStockOutboxErrorAsync(long id, string error, CancellationToken cancellationToken = default);
}

public sealed record StockOutboxMessage(
    long Id,
    string BusinessKey,
    int Depo,
    int ArticleId,
    string Bulto,
    string ContentHash,
    decimal? Costo,
    decimal? Precio,
    decimal? Saldo,
    decimal? Piezas,
    bool IsPresent,
    string Status,
    int RetryCount
);
