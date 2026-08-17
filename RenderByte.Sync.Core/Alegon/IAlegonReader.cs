using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Contrato de solo lectura sobre la base Alegon (<c>sistema</c>).
/// Expone únicamente operaciones conocidas y nombradas.
/// No existe ningún método de ejecución SQL genérica o arbitraria.
/// </summary>
public interface IAlegonReader
{
    /// <summary>
    /// Verifica conectividad, existencia de la base y obtiene métricas básicas del sistema.
    /// No lee los 2.6 millones de movimientos.
    /// </summary>
    Task<AlegonHealthCheck> GetHealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene el número de sucursal configurado en <c>dbo.sisparam</c> (codi = 'NRO.SUCURS').
    /// </summary>
    Task<int> GetBranchNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene todos los artículos del catálogo desde <c>dbo.articulo</c>.
    /// </summary>
    Task<IReadOnlyList<AlegonProduct>> GetProductsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene el stock actual desde <c>dbo.artistock</c> filtrado por el depósito indicado.
    /// </summary>
    Task<IReadOnlyList<AlegonStock>> GetCurrentStockAsync(int branchNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene la fecha y hora del último movimiento insertado en <c>dbo.movistockdt</c>
    /// filtrado por el depósito indicado. Retorna null si no hay registros.
    /// </summary>
    Task<DateTime?> GetLatestMovementInsertionDateAsync(int branchNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene movimientos de <c>dbo.movistockdt</c> posteriores al cursor compuesto indicado,
    /// filtrados por el depósito local, ordenados de forma determinística, limitados a <paramref name="limit"/> filas.
    /// </summary>
    /// <param name="branchNumber">Número de depósito local (de <c>sisparam</c>).</param>
    /// <param name="checkpoint">
    /// Cursor compuesto. La query retorna filas donde:
    /// <c>fedepo &gt; checkpoint.Fedepo</c>
    /// OR <c>(fedepo = checkpoint.Fedepo AND (CLAVEU &gt; checkpoint.ClaveU OR (CLAVEU = checkpoint.ClaveU AND item &gt; checkpoint.Item)))</c>.
    /// Usar <see cref="MovementCheckpoint.Initial"/> para la primera lectura desde una fecha.
    /// </param>
    /// <param name="limit">Máximo de filas. Usar valores pequeños (10–1000) según el caso de uso.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <remarks>
    /// Compatible con SQL Server 2008 R2 (TOP @limit, sin OFFSET/FETCH).
    /// Nunca ejecuta SQL externo: la query es una constante privada en <c>AlegonReader</c>.
    /// El determinismo está garantizado porque CLAVEU + item identifica cada renglón lógico
    /// dentro del depósito local, por lo que (fedepo, CLAVEU, item) es única por fila.
    /// </remarks>
    Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(
        int                branchNumber,
        MovementCheckpoint checkpoint,
        int                limit,
        CancellationToken  cancellationToken = default);
}
