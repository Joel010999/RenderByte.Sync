using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Persistence;

/// <summary>
/// Contrato de persistencia de RenderByte Sync para guardar el batch de movimientos
/// de forma atómica junto con la actualización del checkpoint.
/// </summary>
public interface ISyncBatchStore : IAsyncDisposable
{
    /// <summary>
    /// Abre (o crea) la base de datos, aplica migraciones idempotentes de schema,
    /// activa pragmas (WAL, FK), valida el <paramref name="sourceId"/> contra la
    /// instalación registrada y valida el <paramref name="branchId"/> contra el checkpoint.
    /// </summary>
    /// <param name="sourceId">
    /// Identificador único de esta instalación de RenderByte Sync.
    /// Proviene de <c>RENDERBYTE_SYNC_SOURCE_ID</c>. No puede ser nulo ni vacío.
    /// Si la base de datos ya fue inicializada con un source_id distinto, se lanza
    /// <see cref="InvalidOperationException"/> con el mensaje <c>[SOURCE MISMATCH]</c>.
    /// </param>
    /// <param name="branchId">Número de sucursal de Alegon.</param>
    Task InitializeAsync(string sourceId, int branchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre una base de datos local existente y valida su metadata de instalación.
    /// Útil para operaciones de solo lectura/sincronización (como outbox-sync) 
    /// que no deben crear una nueva DB ni dependen de Alegon para conocer el branch_id.
    /// </summary>
    /// <param name="sourceId">Identificador único de esta instalación de RenderByte Sync.</param>
    Task OpenExistingInstallationAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna el checkpoint persistido, o nulo si no existe (primera ejecución).
    /// </summary>
    Task<StoredCheckpointData?> GetCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persiste atómicamente el batch de movimientos (outbox) y avanza el cursor (checkpoint)
    /// en la misma transacción SQLite. Si el batch está vacío, es un no-op (retorna inmediatamente).
    /// </summary>
    /// <returns>
    /// <see cref="PersistBatchResult"/> con los conteos exactos de insertados y duplicados,
    /// calculados directamente desde las filas afectadas de cada INSERT.
    /// </returns>
    Task<PersistBatchResult> PersistBatchAndCheckpointAsync(
        int branchId,
        IReadOnlyList<AlegonMovement> movements,
        MovementCheckpoint checkpointAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna el recuento de mensajes pendientes en el outbox mediante
    /// <c>SELECT COUNT(*) FROM sync_outbox WHERE status='pending'</c>.
    /// No materializa las filas.
    /// </summary>
    Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene los siguientes N mensajes pendientes de ser enviados,
    /// en orden estricto de inserción (ID ascendente).
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna un mensaje específico del Outbox por su ID local, independientemente de su estado.
    /// Retorna null si no existe.
    /// </summary>
    Task<OutboxMessage?> GetMessageByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca un conjunto de mensajes como enviados con un batchId específico.
    /// </summary>
    Task MarkBatchAsSentAsync(IEnumerable<long> messageIds, string batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca un conjunto de mensajes como fallidos con un mensaje de error.
    /// </summary>
    Task MarkBatchAsFailedAsync(IEnumerable<long> messageIds, string error, CancellationToken cancellationToken = default);
}
