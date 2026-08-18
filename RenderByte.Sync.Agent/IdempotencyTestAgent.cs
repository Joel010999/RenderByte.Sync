using System.Net.Http;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class IdempotencyTestAgent
{
    public static async Task<int> RunAsync(string sourceId, string[] args, CancellationToken ct, HttpMessageHandler? httpHandler = null)
    {
        if (args.Length == 0 || !long.TryParse(args[0], out var id))
        {
            Console.Error.WriteLine("[ERROR] Se requiere el ID local del outbox (ej. idempotency-test 1).");
            return 1;
        }

        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("[ERROR CONFIGURACIÓN] Faltan variables RENDERBYTE_SYNC_API_URL o RENDERBYTE_SYNC_API_KEY.");
            return 1;
        }

        Console.WriteLine("[TEST] Reenvío idempotente. No modifica estado local.");

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

        var message = await store.GetMessageByIdAsync(id, ct);
        if (message == null)
        {
            Console.Error.WriteLine($"[ERROR] No se encontró el registro Outbox con ID {id}.");
            return 1;
        }

        var p = message;
        var dto = new SyncMovementDto(
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
        );

        var request = new SyncBatchRequest(
            SourceId: sourceId,
            BranchId: message.BranchId,
            BatchId: Guid.NewGuid().ToString("N"),
            SentAt: DateTimeOffset.UtcNow,
            Mode: "live",
            Movements: new[] { dto }
        );

        using var client = new HttpSyncClient(apiUrl, apiKey, httpHandler);
        
        try
        {
            var response = await client.SendBatchAsync(request, ct);
            Console.WriteLine($"[EXITO] HTTP 200 OK. Resumen del servidor:");
            Console.WriteLine($"        Aceptados: {response!.Accepted}");
            Console.WriteLine($"        Insertados: {response.Inserted}");
            Console.WriteLine($"        Duplicados: {response.Duplicates}");
            return 0;
        }
        catch (SyncApiException ex)
        {
            Console.Error.WriteLine($"[ERROR HTTP {(int)ex.StatusCode}] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR FATAL] Fallo al enviar mensaje: {ex.Message}");
            return 1;
        }
    }
}
