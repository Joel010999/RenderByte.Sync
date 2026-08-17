using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Persistence;

/// <summary>
/// Representa la fila completa de <c>sync_checkpoint</c> tal como está almacenada en SQLite.
/// Incluye todos los campos de la DB, incluidos <see cref="BranchId"/> y <see cref="UpdatedAt"/>,
/// que no forman parte del cursor (<see cref="MovementCheckpoint"/>) pero son necesarios
/// para auditoría y validación.
/// </summary>
public sealed record StoredCheckpointData(
    int      BranchId,
    DateTime Fedepo,
    string   ClaveU,
    int      Item,
    DateTime UpdatedAt)
{
    /// <summary>
    /// Convierte al cursor compuesto <see cref="MovementCheckpoint"/> para usarlo en
    /// <c>GetMovementsAfterAsync</c>. Descarta <see cref="BranchId"/> y <see cref="UpdatedAt"/>
    /// que no son parte del cursor de lectura de Alegon.
    /// </summary>
    public MovementCheckpoint ToMovementCheckpoint() =>
        new(Fedepo, ClaveU, Item);
}
