using System.Net.Http;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class OutboxSyncAgent
{
    public static async Task<int> RunAsync(string sourceId, string[] args, CancellationToken ct, HttpMessageHandler? httpHandler = null)
    {
        var limit = args.Length > 0 && int.TryParse(args[0], out var l) ? l : 200;

        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("[ERROR] Faltan variables RENDERBYTE_SYNC_API_URL o RENDERBYTE_SYNC_API_KEY.");
            return 2;
        }

        Console.WriteLine($"[outbox-sync] Conectando a {apiUrl} (límite: {limit})");

        var dbPath = SyncDbPath.Resolve();
        await using var store = new SqliteSyncBatchStore(dbPath);
        try
        {
            await store.OpenExistingInstallationAsync(sourceId, ct);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            return 2;
        }
        
        var pendingCount = await store.GetPendingCountAsync(ct);
        if (pendingCount == 0)
        {
            Console.WriteLine("[outbox-sync] No hay mensajes pendientes.");
            return 0;
        }

        Console.WriteLine($"[outbox-sync] {pendingCount} pendientes encontrados en BD.");

        using var client = new HttpSyncClient(apiUrl, apiKey, httpHandler);
        var transport = new SyncTransportService(store, client, sourceId);

        try
        {
            var (success, sentCount) = await transport.SendPendingAsync(limit, ct);
            if (success)
            {
                if (sentCount > 0)
                    Console.WriteLine($"[outbox-sync] {sentCount} mensajes enviados y marcados como sent.");
                else
                    Console.WriteLine($"[outbox-sync] No se enviaron mensajes (ya enviados o fallidos localmente).");
                return 0;
            }
            else
            {
                Console.WriteLine($"[outbox-sync] Ocurrió un error transitorio al enviar.");
                return 1;
            }
        }
        catch (SyncApiException ex)
        {
            Console.Error.WriteLine($"[outbox-sync] ERROR FATAL HTTP {(int)ex.StatusCode}: {ex.Message}");
            return 1;
        }
    }
}
