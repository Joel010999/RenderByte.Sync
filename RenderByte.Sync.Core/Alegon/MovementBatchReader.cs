using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Orquesta la lectura incremental de <c>dbo.movistockdt</c> en batches sucesivos,
/// avanzando el cursor en memoria sin persistir nada.
/// </summary>
/// <remarks>
/// <para>
/// Patrón de uso:
/// <code>
/// var batchReader = new MovementBatchReader(reader, branchNumber, batchSize: 100);
/// var checkpoint  = MovementCheckpoint.Initial(startDate);
///
/// while (true)
/// {
///     var result = await batchReader.ReadNextBatchAsync(checkpoint, ct);
///     if (result.IsEmpty) break;
///
///     // procesar result.Movements ...
///
///     checkpoint = result.CheckpointAfter;
/// }
/// </code>
/// </para>
/// <para>
/// No escribe en SQL. No persiste estado. Solo llama a <see cref="IAlegonReader.GetMovementsAfterAsync"/>.
/// </para>
/// <para>
/// Incluye validación defensiva que aborta si detecta movimientos fuera de orden,
/// iguales al checkpoint o con fedepo nulo inesperado.
/// </para>
/// </remarks>
public sealed class MovementBatchReader
{
    private readonly IAlegonReader _reader;
    private readonly int           _branchNumber;
    private readonly int           _batchSize;
    private readonly bool          _salesOnly;

    /// <param name="reader">Lector de Alegon (solo lectura).</param>
    /// <param name="branchNumber">Depósito local (de sisparam).</param>
    /// <param name="batchSize">Cantidad máxima de filas por batch. Debe ser &gt; 0.</param>
    /// <param name="salesOnly">Si es true, filtra solo ventas.</param>
    public MovementBatchReader(IAlegonReader reader, int branchNumber, int batchSize, bool salesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "El tamaño de batch debe ser mayor a cero.");

        _reader       = reader;
        _branchNumber = branchNumber;
        _batchSize    = batchSize;
        _salesOnly    = salesOnly;
    }

    /// <summary>Número de depósito local configurado.</summary>
    public int BranchNumber => _branchNumber;

    /// <summary>Tamaño máximo de filas por batch.</summary>
    public int BatchSize => _batchSize;

    /// <summary>
    /// Lee el siguiente batch de movimientos a partir del checkpoint dado.
    /// Si el resultado no está vacío, <see cref="BatchResult.CheckpointAfter"/> apunta al último
    /// movimiento leído — páselo al siguiente llamado para continuar sin duplicados ni pérdidas.
    /// Si el resultado está vacío, el cursor no avanzó: se llegó al final de los datos disponibles.
    /// </summary>
    /// <param name="checkpoint">Posición actual del cursor.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <exception cref="InvalidOperationException">
    /// Si la validación defensiva detecta un movimiento fuera de orden, igual al checkpoint,
    /// con fedepo nulo, o si el checkpoint no avanzó pese a haber filas.
    /// </exception>
    public async Task<BatchResult> ReadNextBatchAsync(
        MovementCheckpoint checkpoint,
        CancellationToken  cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var movements = await _reader.GetMovementsAfterAsync(
            _branchNumber,
            checkpoint,
            _batchSize,
            _salesOnly,
            cancellationToken);

        if (movements.Count == 0)
            return BatchResult.Empty(checkpoint);

        // ── Validación defensiva ──────────────────────────────────────────────
        // Detecta bugs de query o mapping antes de que corrompan el cursor en producción.
        ValidateBatch(movements, checkpoint);

        var nextCheckpoint = MovementCheckpoint.From(movements[^1]);

        // Garantía final: el checkpoint debe haber avanzado.
        // (ValidateBatch ya lo garantiza, pero se verifica explícitamente como última línea de defensa.)
        if (nextCheckpoint == checkpoint)
            throw new InvalidOperationException(
                $"[BUG] El checkpoint no avanzó tras leer {movements.Count} fila(s). " +
                $"Checkpoint: {checkpoint}. Esto indica un error en la lógica del cursor.");

        return new BatchResult(movements, nextCheckpoint, movements.Count);
    }

    // ─── Validación defensiva ─────────────────────────────────────────────────

    /// <summary>
    /// Verifica que todos los movimientos del batch sean estrictamente posteriores al checkpoint
    /// de entrada y que estén en orden estrictamente creciente (fedepo, CLAVEU, item).
    /// </summary>
    /// <exception cref="InvalidOperationException">Si se detecta cualquier violación.</exception>
    private static void ValidateBatch(IReadOnlyList<AlegonMovement> movements, MovementCheckpoint checkpoint)
    {
        for (int i = 0; i < movements.Count; i++)
        {
            var m = movements[i];

            // 1. Cada movimiento debe ser estrictamente posterior al checkpoint de entrada
            if (!IsStrictlyAfterCheckpoint(m, checkpoint))
                throw new InvalidOperationException(
                    $"[BUG] Movimiento #{i} (CLAVEU={m.ClaveU} item={m.Item} " +
                    $"fedepo={m.FechaDeposito}) no es estrictamente posterior al checkpoint " +
                    $"de entrada ({checkpoint}). Posible bug en query o mapping del cursor.");

            // 2. Dentro del batch, orden estrictamente creciente
            if (i > 0 && ComparePosition(movements[i - 1], m) >= 0)
                throw new InvalidOperationException(
                    $"[BUG] Batch desordenado en posición #{i}: " +
                    $"(CLAVEU={movements[i - 1].ClaveU} item={movements[i - 1].Item}) " +
                    $">= (CLAVEU={m.ClaveU} item={m.Item}). " +
                    "El batch debe estar en orden fedepo ASC, CLAVEU ASC, item ASC.");
        }
    }

    /// <summary>
    /// Retorna true si el movimiento es estrictamente posterior al checkpoint
    /// según la comparación del cursor (fedepo, CLAVEU, item).
    /// </summary>
    /// <remarks>
    /// Usa <see cref="StringComparison.Ordinal"/> para CLAVEU. En producción, SQL Server
    /// usa la collation del servidor (típicamente CI_AS en Alegon). Para la validación
    /// defensiva, Ordinal es suficiente para detectar bugs obvios de ordering o mapping.
    /// </remarks>
    private static bool IsStrictlyAfterCheckpoint(AlegonMovement m, MovementCheckpoint cp)
    {
        if (!m.FechaDeposito.HasValue)
            throw new InvalidOperationException(
                $"[BUG] Movimiento CLAVEU={m.ClaveU} item={m.Item} tiene fedepo NULL inesperado. " +
                "Todos los movimientos retornados por el cursor deben tener fedepo no nulo.");

        var fedepo = m.FechaDeposito.Value;

        if (fedepo > cp.Fedepo) return true;
        if (fedepo < cp.Fedepo) return false;

        // fedepo == cp.Fedepo
        var claveComp = string.Compare(m.ClaveU, cp.ClaveU, StringComparison.Ordinal);
        if (claveComp > 0) return true;
        if (claveComp < 0) return false;

        // CLAVEU == cp.ClaveU
        return m.Item > cp.Item;
    }

    /// <summary>
    /// Compara dos movimientos por su posición en el cursor (fedepo, CLAVEU, item).
    /// Retorna negativo si a &lt; b, cero si iguales, positivo si a &gt; b.
    /// </summary>
    private static int ComparePosition(AlegonMovement a, AlegonMovement b)
    {
        var fa = a.FechaDeposito ?? DateTime.MinValue;
        var fb = b.FechaDeposito ?? DateTime.MinValue;

        var c = fa.CompareTo(fb);
        if (c != 0) return c;

        c = string.Compare(a.ClaveU, b.ClaveU, StringComparison.Ordinal);
        if (c != 0) return c;

        return a.Item.CompareTo(b.Item);
    }
}

