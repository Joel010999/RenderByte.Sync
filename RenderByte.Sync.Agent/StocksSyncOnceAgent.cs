using System.Text.Json;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Contracts;

namespace RenderByte.Sync.Agent;

public static class StocksSyncOnceAgent
{
    public static async Task<int> RunAsync(string sourceId, IStockReader reader, string[] args, CancellationToken ct, IStockStore? storeOverride = null, HttpMessageHandler? httpHandler = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var activeLogger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        IStockStore store;
        if (storeOverride != null)
        {
            store = storeOverride;
        }
        else
        {
            var dbPath = SyncDbPath.Resolve();
            var realStore = new SqliteSyncBatchStore(dbPath);
            Console.WriteLine("[STOCKS] Conectando a SQLite local...");
            try
            {
                await realStore.OpenExistingInstallationAsync(sourceId, ct);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
                return 2;
            }
            store = realStore;
        }

        // Obtener branch_id desde el checkpoint existente para mantener la consistencia
        int branchId = 1; // Default
        if (store is SqliteSyncBatchStore sqStore)
        {
            var checkpoint = await sqStore.GetCheckpointAsync(ct);
            if (checkpoint == null)
            {
                Console.Error.WriteLine("[ERROR] No se puede determinar branch_id. Ejecute un checkpoint-test primero.");
                return 2;
            }
            branchId = checkpoint.BranchId;
        }

        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("[ERROR] Faltan variables de entorno para API. Los stocks quedaron en outbox.");
            return 2;
        }

        using var client = new HttpSyncClient(apiUrl, apiKey, httpHandler);
        var service = new RenderByte.Sync.Agent.Services.StockSyncService(reader, store, client, sourceId, branchId, activeLogger);

        try
        {
            await service.CaptureAsync(ct);
            await service.SendPendingAsync(ct);
        }
        catch (SyncApiException ex)
        {
            Console.Error.WriteLine($"[STOCK SYNC] ERROR HTTP {(int)ex.StatusCode}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[STOCK SYNC] ERROR: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
