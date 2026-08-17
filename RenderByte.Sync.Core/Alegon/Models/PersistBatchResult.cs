using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Resultado de una operación <c>PersistBatchAndCheckpointAsync</c>.
/// Informa exactamente cuántos movimientos del batch se insertaron vs. cuántos
/// ya existían (duplicados silenciados por <c>ON CONFLICT(movement_key) DO NOTHING</c>).
/// </summary>
/// <param name="Attempted">Total de movimientos en el batch procesado.</param>
/// <param name="Inserted">Filas efectivamente insertadas en sync_outbox.</param>
/// <param name="DuplicatesSkipped">Filas ignoradas por movement_key ya existente.</param>
/// <param name="CheckpointAfter">Checkpoint que quedó persistido tras la transacción.</param>
public sealed record PersistBatchResult(
    int Attempted,
    int Inserted,
    int DuplicatesSkipped,
    MovementCheckpoint CheckpointAfter)
{
    /// <summary>
    /// Resultado para batches vacíos (operación no-op, checkpoint sin cambios).
    /// </summary>
    public static PersistBatchResult Empty(MovementCheckpoint checkpoint) =>
        new(0, 0, 0, checkpoint);
}
