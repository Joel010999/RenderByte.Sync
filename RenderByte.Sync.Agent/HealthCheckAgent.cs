using RenderByte.Sync.Core.Alegon;

namespace RenderByte.Sync.Agent;

/// <summary>
/// Ejecuta el health check de conexión a Alegon e imprime el resultado por consola.
/// Depende de <see cref="IAlegonReader"/> — no conoce ninguna implementación concreta.
/// La implementación concreta (<c>AlegonReader</c>) la inyecta <c>Program.cs</c>.
/// </summary>
public static class HealthCheckAgent
{
    public static async Task<int> RunAsync(IAlegonReader reader, CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await reader.GetHealthCheckAsync(cancellationToken);

            Console.WriteLine("[OK] SQL Server conectado");

            if (!health.DatabaseFound)
            {
                Console.Error.WriteLine("[ERROR] Base 'sistema' no encontrada en SQL Server.");
                return 1;
            }

            Console.WriteLine("[OK] Base sistema encontrada");

            var branchLabel = health.BranchName is not null
                ? $"{health.BranchNumber} - {health.BranchName}"
                : $"{health.BranchNumber}";

            Console.WriteLine($"[OK] Sucursal detectada: {branchLabel}");
            Console.WriteLine($"[OK] Productos: {health.ProductCount:N0}");
            Console.WriteLine($"[OK] Stock local: {health.LocalStockRecordCount:N0} registros");

            if (health.LastMovementInsertedAt.HasValue)
                Console.WriteLine($"[OK] Último movimiento: {health.LastMovementInsertedAt.Value:yyyy-MM-dd HH:mm:ss}");
            else
                Console.WriteLine("[WARN] Sin movimientos registrados para este depósito.");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] Health check de Alegon: {exception.Message}");
            return 1;
        }
    }
}
