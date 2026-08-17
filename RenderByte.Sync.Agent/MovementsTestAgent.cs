using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Agent;

/// <summary>
/// Modo de prueba para validar la lectura incremental de <c>dbo.movistockdt</c>.
/// Lee exactamente 10 movimientos a partir de un checkpoint de fedepo e imprime los campos RAW.
/// </summary>
/// <remarks>
/// Uso:
///   RenderByte.Sync.Agent.exe movements-test [YYYY-MM-DD HH:mm:ss]
///
/// Si no se pasa fecha, usa como checkpoint 7 días antes del instante actual.
/// No almacena nada. No escribe en SQL. Solo SELECT.
/// </remarks>
public static class MovementsTestAgent
{
    private const int TestLimit = 10;

    public static async Task<int> RunAsync(
        IAlegonReader reader,
        string?       checkpointArg,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ── 1. Detectar sucursal ──────────────────────────────────────────────
            Console.WriteLine("[movements-test] Conectando...");
            var branchNumber = await reader.GetBranchNumberAsync(cancellationToken);
            Console.WriteLine($"[movements-test] Sucursal detectada: {branchNumber}");

            // ── 2. Resolver checkpoint ────────────────────────────────────────────
            DateTime checkpoint;
            if (!string.IsNullOrWhiteSpace(checkpointArg))
            {
                if (!DateTime.TryParse(checkpointArg, out checkpoint))
                {
                    Console.Error.WriteLine(
                        $"[ERROR] Formato de fecha inválido: '{checkpointArg}'. " +
                        "Use: YYYY-MM-DD  o  \"YYYY-MM-DD HH:mm:ss\"");
                    return 2;
                }
                Console.WriteLine($"[movements-test] Checkpoint (argumento): {checkpoint:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                // Fallback: 7 días atrás — suficiente para pruebas en cualquier instalación activa
                checkpoint = DateTime.Now.AddDays(-7);
                Console.WriteLine($"[movements-test] Checkpoint (por defecto, -7 días): {checkpoint:yyyy-MM-dd HH:mm:ss}");
            }

            // ── 3. Leer movimientos ───────────────────────────────────────────────
            Console.WriteLine($"[movements-test] Leyendo hasta {TestLimit} movimientos con fedepo >= checkpoint...");
            Console.WriteLine();

            var initialCheckpoint = MovementCheckpoint.Initial(checkpoint);
            var movements = await reader.GetMovementsAfterAsync(
                branchNumber,
                initialCheckpoint,
                TestLimit,
                cancellationToken);

            if (movements.Count == 0)
            {
                Console.WriteLine("[movements-test] Sin resultados para ese checkpoint.");
                Console.WriteLine("                 Pruebe con una fecha más antigua.");
                return 0;
            }

            // ── 4. Imprimir resultados ────────────────────────────────────────────
            Console.WriteLine($"[movements-test] {movements.Count} movimiento(s) encontrado(s):");
            Console.WriteLine(new string('─', 72));

            int index = 1;
            foreach (var m in movements)
            {
                Console.WriteLine($"  #{index++}");
                Console.WriteLine($"    fedepo   : {(m.FechaDeposito.HasValue ? m.FechaDeposito.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(null)")}");
                Console.WriteLine($"    CLAVEU   : {m.ClaveU}");
                Console.WriteLine($"    item     : {m.Item}");
                Console.WriteLine($"    tipomov  : {m.TipoMovimiento}");
                Console.WriteLine($"    codcom   : {m.CodigoComprobante}");
                Console.WriteLine($"    idarti   : {m.ArticleId}");
                Console.WriteLine($"    cantidad : {(m.Cantidad.HasValue ? m.Cantidad.Value.ToString("G") : "(null)")}");
                Console.WriteLine($"    costo    : {(m.Costo.HasValue    ? m.Costo.Value.ToString("G")    : "(null)")}");
                Console.WriteLine($"    precio   : {(m.Precio.HasValue   ? m.Precio.Value.ToString("G")   : "(null)")}");
                Console.WriteLine();
            }

            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"[movements-test] OK — {movements.Count} fila(s) leída(s). Límite: {TestLimit}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] movements-test: {exception.Message}");
            return 1;
        }
    }
}
