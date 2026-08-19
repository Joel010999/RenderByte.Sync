using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Provee acceso a la tabla maestra de productos en Alegon.
/// </summary>
public interface IProductReader
{
    /// <summary>
    /// Lee la tabla completa de dbo.articulo.
    /// Recupera los 34 campos seleccionados explícitamente para M8.1.
    /// Operación de sólo lectura.
    /// </summary>
    Task<IReadOnlyList<AlegonProductMaster>> GetFullSnapshotAsync(CancellationToken cancellationToken = default);
}
