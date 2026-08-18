using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public class SyncTransportService
{
    private readonly ISyncBatchStore _store;
    private readonly HttpSyncClient _client;
    private readonly string _sourceId;

    public SyncTransportService(ISyncBatchStore store, HttpSyncClient client, string sourceId)
    {
        _store = store;
        _client = client;
        _sourceId = sourceId;
    }

    /// <summary>
    /// Intenta enviar un lote de mensajes pendientes.
    /// Retorna true si envió mensajes con éxito o no había pendientes.
    /// Retorna false si ocurrió un error transitorio.
    /// Lanza excepción si ocurrió un error fatal (Auth/BadRequest).
    /// </summary>
    public async Task<(bool Success, int SentCount)> SendPendingAsync(int limit, CancellationToken ct)
    {
        var pending = await _store.GetPendingAsync(limit, ct);
        if (pending.Count == 0)
        {
            return (true, 0);
        }

        var batchId = Guid.NewGuid().ToString("N");
        var branchId = pending[0].BranchId;

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
            SourceId: _sourceId,
            BranchId: branchId,
            BatchId: batchId,
            SentAt: DateTimeOffset.UtcNow,
            Mode: "live",
            Movements: movements
        );

        try
        {
            var response = await _client.SendBatchAsync(request, ct);
            
            // Validación estricta M6
            if (response != null && response.Accepted == movements.Count && (response.Inserted + response.Duplicates) == response.Accepted)
            {
                var ids = pending.Select(p => p.Id).ToList();
                await _store.MarkBatchAsSentAsync(ids, batchId, ct);
                return (true, ids.Count);
            }
            else if (response != null)
            {
                await _store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"Invalid ACK count: accepted {response.Accepted} != {movements.Count}", ct);
                return (false, 0); // Considerado transitorio para retry
            }
            
            return (false, 0);
        }
        catch (SyncApiException ex)
        {
            var statusCode = (int)ex.StatusCode;
            if (statusCode == 400 || statusCode == 401 || statusCode == 403)
            {
                await _store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"FATAL HTTP {statusCode}: {ex.Message}", ct);
                throw; // Fatal, abortar flujo
            }
            
            await _store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"TRANSIENT HTTP {statusCode}: {ex.Message}", ct);
            return (false, 0);
        }
        catch (Exception ex)
        {
            await _store.MarkBatchAsFailedAsync(pending.Select(p => p.Id), $"NETWORK ERROR: {ex.Message}", ct);
            return (false, 0);
        }
    }
}
