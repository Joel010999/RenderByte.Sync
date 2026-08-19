using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Persistence;

public record ProductState(
    string BusinessKey,
    int ArticleId,
    string ContentHash,
    bool IsPresent);

public record ProductOutboxMessage(
    long Id,
    string BusinessKey,
    int ArticleId,
    string ContentHash,
    string Payload,
    string Status,
    int RetryCount);

/// <summary>
/// Provee persistencia local para el estado y el outbox de productos.
/// </summary>
public interface IProductStore
{
    /// <summary>
    /// Inicializa la base de datos o verifica la instalación existente.
    /// </summary>
    Task InitializeAsync(string sourceId, int branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve el estado de todos los productos conocidos localmente.
    /// Clave: business_key. Valor: estado (hash e is_present).
    /// </summary>
    Task<IReadOnlyDictionary<string, ProductState>> GetStatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Actualiza el estado e inserta en el outbox de forma atómica.
    /// Solo se llama para productos que son nuevos o cuyo hash ha cambiado.
    /// </summary>
    Task UpsertStateAndOutboxAsync(
        string sourceId,
        int branchId,
        AlegonProductMaster product,
        string businessKey,
        string contentHash,
        string payloadJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca un producto como "no presente" (borrado en origen) y genera un tombstone en el outbox.
    /// </summary>
    Task MarkMissingAndCreateTombstoneAsync(
        string sourceId,
        int branchId,
        string businessKey,
        int articleId,
        CancellationToken cancellationToken = default);

    // Métodos para enviar el outbox...
    Task<IReadOnlyList<ProductOutboxMessage>> GetPendingOutboxAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkOutboxSentAsync(long id, CancellationToken cancellationToken = default);
    Task MarkOutboxErrorAsync(long id, string error, CancellationToken cancellationToken = default);
}
