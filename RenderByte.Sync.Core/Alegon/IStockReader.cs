using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Provee acceso de solo lectura al stock actual en la base de datos de Alegon.
/// </summary>
public interface IStockReader
{
    /// <summary>
    /// Lee el snapshot completo del stock actual para un depósito específico.
    /// Retorna una lista materializada en memoria.
    /// </summary>
    /// <param name="branchId">El depósito/sucursal a leer (e.g. 1, 2, 3).</param>
    /// <param name="cancellationToken">Token para cancelar la operación.</param>
    Task<IReadOnlyList<AlegonStock>> GetFullSnapshotAsync(int branchId, CancellationToken cancellationToken = default);
}
