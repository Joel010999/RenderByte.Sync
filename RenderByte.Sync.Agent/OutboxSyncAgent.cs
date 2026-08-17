using RenderByte.Sync.Contracts;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class OutboxSyncAgent
{
    public static async Task<int> RunAsync(string sourceId, string[] args, CancellationToken ct)
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
        
        // No necesitamos inicializar con branchId porque solo vamos a leer/actualizar
        // Pero SqliteSyncBatchStore.InitializeAsync requiere branchId.
        // Haremos un hacky inicialize con branch 0 o podemos cambiar InitializeAsync para ser tolerante.
        // O mejor: leer el branch_id del primer outbox message o pasarlo si es estricto.
        // Como M5.1 requiere branch_id para verificar inconsistencia, vamos a pasarlo o ignorar.
        
        // Mejor: SqliteSyncBatchStore.EnsureInitialized se llama solo si usamos métodos que lo requieren.
        // Sin embargo, getPending no lo requiere per se pero lo llama.
        // Cambiaremos SqliteSyncBatchStore para poder inicializar sin branch_id estricto.
        // Por ahora, asumimos que ya está inicializada por un checkpoint-test previo.
        
        var pending = await store.GetPendingAsync(limit, ct);
        if (pending.Count == 0)
        {
            Console.WriteLine("[outbox-sync] No hay mensajes pendientes.");
            return 0;
        }

        Console.WriteLine($"[outbox-sync] {pending.Count} pendientes encontrados.");

        var batchId = Guid.NewGuid().ToString();
        var branchId = pending[0].BranchId; // Asumimos mismo branch para todos
        
        var movements = pending.Select(p => new SyncMovementDto(
            MovementKey: p.MovementKey,
            BusinessKey: p.BusinessKey,
            Depo: (short)p.Depo,
            TipoMov: p.TipoMovimiento,
            Fecha: p.Fecha,
            CodCom: p.CodigoComprobante,
            PtoVta: p.PuntoVenta,
            Numero: p.Numero,
            Proveedor: p.Proveedor,
            IdArti: p.ArticleId,
            Bulto: p.Bulto,
            Local: (short)p.Local,
            Item: (short)p.Item,
            Fedepo: p.Fedepo,
            Oferta: p.Oferta,
            Cantidad: p.Cantidad,
            Saldo: p.Saldo,
            Costo: p.Costo,
            Precio: p.Precio,
            ClaveU: p.ClaveU,
            Piezas: p.Piezas
        )).ToList();

        var request = new SyncBatchRequest(
            SourceId: sourceId,
            BranchId: branchId,
            BatchId: batchId,
            SentAt: DateTimeOffset.UtcNow,
            Mode: "live",
            Movements: movements
        );

        using var client = new HttpSyncClient(apiUrl, apiKey);
        
        try
        {
            var response = await client.SendBatchAsync(request, ct);
            if (response != null && response.Accepted == movements.Count)
            {
                Console.WriteLine($"[outbox-sync] ACK válido recibido: accepted={response.Accepted}, inserted={response.Inserted}, duplicates={response.Duplicates}");
                
                var ids = pending.Select(p => p.Id).ToList();
                await store.MarkBatchAsSentAsync(ids, batchId, ct);
                
                Console.WriteLine($"[outbox-sync] {ids.Count} mensajes marcados como sent.");
            }
            else if (response != null)
            {
                Console.WriteLine($"[outbox-sync] ERROR: ACK inválido (accepted {response.Accepted} != count {movements.Count})");
                await store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), "Invalid ACK count", ct);
                return 1;
            }
        }
        catch (SyncApiException ex)
        {
            Console.Error.WriteLine($"[outbox-sync] ERROR API: HTTP {(int)ex.StatusCode} - {ex.ErrorCode}: {ex.Message}");
            
            var statusCode = (int)ex.StatusCode;
            if (statusCode == 400 || statusCode == 401 || statusCode == 403)
            {
                // Errores fatales (Bad Request, Auth) -> No reintentar agresivamente, dejar en fail para intervención manual o fix.
                // Podríamos marcar status = 'fatal' pero por diseño marcamos fail y limitamos max_retries en getPending.
                await store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"FATAL HTTP {statusCode}: {ex.Message}", ct);
            }
            else
            {
                // Timeout, 429, 500+ -> Fallos transitorios
                await store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"TRANSIENT HTTP {statusCode}: {ex.Message}", ct);
            }
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[outbox-sync] ERROR RED: {ex.Message}");
            await store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"NETWORK ERROR: {ex.Message}", ct);
            return 1;
        }

        return 0;
    }
}
