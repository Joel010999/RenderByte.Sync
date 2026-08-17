namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Cursor compuesto para lectura incremental de <c>dbo.movistockdt</c>.
/// Representa la posición exacta alcanzada en la secuencia de movimientos,
/// de forma que el siguiente batch comience justo después.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identidad lógica (confirmada por Claudio / Alegon)</b>: dentro del depósito local,
/// CLAVEU + item identifica unívocamente cada renglón de movimiento.
/// Por eso se usa (fedepo, CLAVEU, item) como cursor incremental: el ORDER BY sobre
/// esta terna es determinístico dentro del depósito sin necesidad de exponer la PK física.
/// La unicidad física completa está representada por la PK real de SQL Server y no es
/// necesario ni conveniente alterarla.
/// </para>
/// <para>
/// <b>Sentinel inicial</b>: <see cref="Initial"/> produce ClaveU = "" e Item = <see cref="short.MinValue"/>
/// (-32768). En SQL Server, CHAR(10) vacío se compara como 10 espacios, que es menor que cualquier
/// valor de CLAVEU real (alfanumérico). Esto hace que la query de cursor con <see cref="Initial"/>
/// retorne efectivamente TODAS las filas con fedepo &gt;= la fecha indicada.
/// </para>
/// <para>
/// <b>Deuda técnica — carga histórica</b>: la sincronización incremental usa este cursor
/// sobre fedepo. La carga inicial de ~2.6M movimientos históricos requerirá una estrategia
/// separada optimizada para el índice existente (probablemente por rangos de fecha),
/// y NO debe resolverse alterando índices de Alegon ni ejecutando queries sin límite.
/// </para>
/// </remarks>
public sealed record MovementCheckpoint(
    DateTime Fedepo,
    string   ClaveU,
    int      Item)
{
    /// <summary>
    /// Crea el checkpoint sentinel inicial desde una fecha.
    /// Equivale a "dame todo desde esta fecha en adelante" sin omitir ninguna fila.
    /// </summary>
    /// <param name="fedepo">Fecha a partir de la cual leer (inclusiva).</param>
    public static MovementCheckpoint Initial(DateTime fedepo) =>
        new(fedepo, string.Empty, short.MinValue);

    /// <summary>
    /// Crea el checkpoint resultante a partir del último movimiento leído en un batch.
    /// Pasar este checkpoint al siguiente <c>GetMovementsAfterAsync</c> excluye ese movimiento
    /// y comienza desde el siguiente, garantizando continuidad sin duplicados ni pérdidas.
    /// </summary>
    /// <param name="movement">Último movimiento del batch (debe tener fedepo no nulo).</param>
    /// <exception cref="ArgumentException">Si <paramref name="movement"/> tiene fedepo nulo.</exception>
    public static MovementCheckpoint From(AlegonMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (!movement.FechaDeposito.HasValue)
            throw new ArgumentException(
                $"El movimiento CLAVEU={movement.ClaveU} item={movement.Item} tiene fedepo NULL. " +
                "No se puede avanzar el cursor desde una fila sin fedepo.",
                nameof(movement));

        return new MovementCheckpoint(movement.FechaDeposito.Value, movement.ClaveU, movement.Item);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"fedepo={Fedepo:yyyy-MM-dd HH:mm:ss.fff}  CLAVEU={ClaveU}  item={Item}";
}
