namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Resultado de un batch de lectura de movimientos.
/// </summary>
public sealed record BatchResult(
    IReadOnlyList<AlegonMovement> Movements,
    MovementCheckpoint            CheckpointAfter,
    int                           Count)
{
    /// <summary>Retorna true si el batch no trajo ninguna fila.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Construye un resultado vacío preservando el checkpoint actual sin avanzar.</summary>
    public static BatchResult Empty(MovementCheckpoint current) =>
        new(Array.Empty<AlegonMovement>(), current, 0);
}
