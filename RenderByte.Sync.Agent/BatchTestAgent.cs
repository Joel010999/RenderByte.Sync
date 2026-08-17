using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Agent;

/// <summary>
/// Modo de prueba para validar la lectura incremental en batches sucesivos.
/// Lee N batches de M movimientos cada uno, avanzando el cursor en memoria.
/// No persiste nada. No escribe en SQL. Solo SELECT.
/// </summary>
/// <remarks>
/// Uso:
///   RenderByte.Sync.Agent.exe batch-test "2026-08-14 17:00:00" 10 3
///
/// Argumentos (en ese orden):
///   [0]  fecha del checkpoint inicial  — YYYY-MM-DD o "YYYY-MM-DD HH:mm:ss"
///   [1]  batch size                    — entero positivo (ej. 10, 100, 1000)
///   [2]  máximo de batches             — entero positivo (ej. 1–100)
///
/// El checkpoint inicial usa MovementCheckpoint.Initial(fecha), que aplica el sentinel
/// ClaveU="" e item=short.MinValue, garantizando que se retornan TODAS las filas
/// con fedepo >= fecha sin omitir ninguna.
/// </remarks>
public static class BatchTestAgent
{
    private const int MaxAllowedBatches = 100;  // límite de seguridad en modo prueba

    public static async Task<int> RunAsync(
        IAlegonReader reader,
        string[]      args,
        CancellationToken cancellationToken = default)
    {
        // ── 1. Parsear argumentos ─────────────────────────────────────────────
        if (args.Length < 3)
        {
            Console.Error.WriteLine("[ERROR] batch-test requiere 3 argumentos:");
            Console.Error.WriteLine("        batch-test <fecha-checkpoint> <batch-size> <max-batches>");
            Console.Error.WriteLine("        Ejemplo: batch-test \"2026-08-14 17:00:00\" 10 3");
            return 2;
        }

        if (!DateTime.TryParse(args[0], out var startDate))
        {
            Console.Error.WriteLine($"[ERROR] Fecha inválida: '{args[0]}'. Use YYYY-MM-DD o \"YYYY-MM-DD HH:mm:ss\".");
            return 2;
        }

        if (!int.TryParse(args[1], out var batchSize) || batchSize <= 0)
        {
            Console.Error.WriteLine($"[ERROR] batch-size debe ser un entero positivo. Recibido: '{args[1]}'.");
            return 2;
        }

        if (!int.TryParse(args[2], out var maxBatches) || maxBatches <= 0 || maxBatches > MaxAllowedBatches)
        {
            Console.Error.WriteLine($"[ERROR] max-batches debe ser entre 1 y {MaxAllowedBatches}. Recibido: '{args[2]}'.");
            return 2;
        }

        try
        {
            // ── 2. Conectar y detectar sucursal ───────────────────────────────
            Console.WriteLine("[batch-test] Conectando...");
            var branchNumber = await reader.GetBranchNumberAsync(cancellationToken);
            Console.WriteLine($"[batch-test] Sucursal: {branchNumber}");
            Console.WriteLine($"[batch-test] Checkpoint inicial: {startDate:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"[batch-test] Batch size: {batchSize}  |  Máx batches: {maxBatches}");
            Console.WriteLine();

            // ── 3. Preparar cursor y batchReader ─────────────────────────────
            var batchReader = new MovementBatchReader(reader, branchNumber, batchSize);

            // Sentinel: ClaveU="" e item=short.MinValue garantizan que fedepo=startDate
            // devuelve TODAS las filas con esa fecha (ver MovementCheckpoint.Initial).
            var currentCheckpoint = MovementCheckpoint.Initial(startDate);
            int totalRead = 0;

            // ── 4. Loop de batches ────────────────────────────────────────────
            for (int batchIndex = 1; batchIndex <= maxBatches; batchIndex++)
            {
                Console.WriteLine($"┌─ Batch #{batchIndex} " + new string('─', 54));
                Console.WriteLine($"│  checkpoint entrada : {currentCheckpoint}");

                var result = await batchReader.ReadNextBatchAsync(currentCheckpoint, cancellationToken);

                if (result.IsEmpty)
                {
                    Console.WriteLine($"│  Sin más movimientos. Fin de datos disponibles.");
                    Console.WriteLine($"└" + new string('─', 60));
                    Console.WriteLine();
                    break;
                }

                // ── 4a. Verificar que el cursor avanzó ────────────────────────
                if (result.CheckpointAfter == currentCheckpoint)
                {
                    // Bug defensivo: si el cursor no avanzó pero hay filas, hay un error de lógica.
                    Console.Error.WriteLine($"[CRITICAL] El cursor no avanzó después del batch #{batchIndex}.");
                    Console.Error.WriteLine($"           Checkpoint: {currentCheckpoint}");
                    Console.Error.WriteLine($"           Esto indica un bug en la lógica del cursor. Abortando.");
                    return 1;
                }

                // ── 4b. Resumen del batch ─────────────────────────────────────
                var first = result.Movements[0];
                var last  = result.Movements[^1];

                Console.WriteLine($"│  leídos             : {result.Count}");
                Console.WriteLine($"│  primero            : fedepo={FormatFedepo(first.FechaDeposito)}  CLAVEU={first.ClaveU}  item={first.Item}");
                Console.WriteLine($"│  último             : fedepo={FormatFedepo(last.FechaDeposito)}  CLAVEU={last.ClaveU}  item={last.Item}");
                Console.WriteLine($"│  checkpoint salida  : {result.CheckpointAfter}");
                Console.WriteLine($"└" + new string('─', 60));
                Console.WriteLine();

                totalRead        += result.Count;
                currentCheckpoint = result.CheckpointAfter;

                // Si el batch retornó menos filas que el tamaño, llegamos al final
                if (result.Count < batchSize)
                {
                    Console.WriteLine($"[batch-test] Batch #{batchIndex} retornó {result.Count} < {batchSize}. Fin de datos.");
                    break;
                }
            }

            Console.WriteLine($"[batch-test] OK — {totalRead} movimiento(s) leído(s) en total.");
            Console.WriteLine($"[batch-test] Checkpoint final: {currentCheckpoint}");
            Console.WriteLine($"[batch-test] Nada persistido. Nada escrito en SQL.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] batch-test: {exception.Message}");
            return 1;
        }
    }

    private static string FormatFedepo(DateTime? fedepo) =>
        fedepo.HasValue ? fedepo.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "(null)";
}
