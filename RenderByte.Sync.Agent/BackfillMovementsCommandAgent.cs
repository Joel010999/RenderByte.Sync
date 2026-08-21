using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Agent;

public static class BackfillMovementsCommandAgent
{
    public static async Task<int> RunAsync(
        ResolvedSyncOptions options,
        IAlegonReader reader,
        string[] args,
        CancellationToken ct,
        System.Net.Http.HttpMessageHandler? httpHandler = null)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" RENDERYBYTE SYNC - HISTORICAL MOVEMENTS BACKFILL");
        Console.WriteLine("==================================================");

        var fromDateStr = "2024-01-01";
        
        // Parse args: [--from yyyy-MM-dd]
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--from" && i + 1 < args.Length)
            {
                fromDateStr = args[i + 1];
                i++;
            }
        }

        if (!DateTime.TryParseExact(fromDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
        {
            Console.Error.WriteLine($"[ERROR] Formato de fecha --from inválido: '{fromDateStr}'. Use yyyy-MM-dd.");
            return 1;
        }

        Console.WriteLine($"[BACKFILL] Fecha solicitada: {fromDateStr}");

        var store = new BackfillCheckpointStore();
        var checkpoint = await store.LoadAsync(ct);

        if (checkpoint != null)
        {
            Console.WriteLine($"[BACKFILL] Resumiendo desde checkpoint guardado:");
            Console.WriteLine($"[BACKFILL] Cursor: {checkpoint}");
        }
        else
        {
            checkpoint = MovementCheckpoint.Initial(startDate);
            Console.WriteLine($"[BACKFILL] Iniciando desde: {startDate:yyyy-MM-dd}");
        }

        int branchId = 0;
        try
        {
            branchId = await reader.GetBranchNumberAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] No se pudo obtener branch_id de Alegon: {ex.Message}");
            return 2;
        }

        using var client = new HttpSyncClient(options.ApiUrl, options.ApiKey, httpHandler);
        var batchReader = new MovementBatchReader(reader, branchId, options.ReadBatchSize);

        int totalProcessed = 0;
        int totalAccepted = 0;
        int totalDuplicates = 0;
        int batchNumber = 1;
        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            BatchResult result;
            try
            {
                result = await batchReader.ReadNextBatchAsync(checkpoint, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Fallo al leer Alegon: {ex.Message}");
                return 2;
            }

            if (result.IsEmpty)
            {
                Console.WriteLine($"[BACKFILL] Fin de los datos disponibles. No hay más movimientos después de {checkpoint}.");
                break;
            }

            Console.WriteLine($"[BACKFILL] batch={batchNumber} rows={result.Movements.Count}");
            
            var request = BuildRequest(options.SourceId, branchId, result.Movements);
            
            try
            {
                var response = await client.SendBatchAsync(request, ct);
                
                if (response != null && response.Accepted == request.Movements.Count)
                {
                    totalProcessed += request.Movements.Count;
                    totalAccepted += response.Inserted;
                    totalDuplicates += response.Duplicates;
                    
                    Console.WriteLine($"[BACKFILL]   => sent={request.Movements.Count} | inserted={response.Inserted} | duplicates={response.Duplicates}");
                    Console.WriteLine($"[BACKFILL] cursor={result.CheckpointAfter}");
                    
                    // Solo actualizamos progreso si fue aceptado enteramente
                    await store.SaveAsync(result.CheckpointAfter, ct);
                    checkpoint = result.CheckpointAfter;
                    batchNumber++;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] Respuesta inválida de la API: Accepted={(response?.Accepted ?? 0)} vs Expected={request.Movements.Count}");
                    return 3;
                }
            }
            catch (SyncApiException ex)
            {
                Console.Error.WriteLine($"[ERROR] Fallo de API: HTTP {(int)ex.StatusCode} - {ex.Message}");
                // Si es un error de negocio o de token, detenemos para no martillar el server.
                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Fallo de red/transporte al enviar a Railway: {ex.Message}");
                Console.Error.WriteLine($"[BACKFILL] El progreso se guardó hasta el batch anterior ({checkpoint}). Relance el comando para retomar.");
                return 5;
            }
            
            // Un pequeño respiro para el SQL Server heredado.
            await Task.Delay(50, ct);
        }

        sw.Stop();
        
        Console.WriteLine("\n==================================================");
        Console.WriteLine("[BACKFILL COMPLETE]");
        Console.WriteLine($"from={fromDateStr}");
        Console.WriteLine($"processed={totalProcessed}");
        Console.WriteLine($"inserted={totalAccepted}");
        Console.WriteLine($"duplicates={totalDuplicates}");
        Console.WriteLine($"duration={sw.Elapsed}");
        Console.WriteLine("==================================================");

        return 0;
    }

    private static SyncBatchRequest BuildRequest(string sourceId, int branchId, IReadOnlyList<AlegonMovement> movements)
    {
        var dtos = movements.Select(m => new SyncMovementDto(
            MovementKey: m.GetMovementKey(sourceId),
            BusinessKey: m.GetBusinessKey(sourceId, branchId),
            Depo: (short)m.Depo,
            TipoMov: m.TipoMovimiento,
            Fecha: DateTime.SpecifyKind(m.Fecha, DateTimeKind.Unspecified).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            CodCom: m.CodigoComprobante,
            PtoVta: m.PuntoVenta,
            Numero: m.Numero,
            Proveedor: m.Proveedor,
            IdArti: m.ArticleId,
            Bulto: m.Bulto,
            Local: (short)m.Local,
            Item: (short)m.Item,
            Fedepo: m.FechaDeposito?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Oferta: m.Oferta,
            Cantidad: m.Cantidad?.ToString(CultureInfo.InvariantCulture),
            Saldo: m.Saldo?.ToString(CultureInfo.InvariantCulture),
            Costo: m.Costo?.ToString(CultureInfo.InvariantCulture),
            Precio: m.Precio?.ToString(CultureInfo.InvariantCulture),
            ClaveU: m.ClaveU,
            Piezas: m.Piezas?.ToString(CultureInfo.InvariantCulture)
        )).ToList();

        return new SyncBatchRequest(
            SourceId: sourceId,
            BranchId: branchId,
            BatchId: Guid.NewGuid().ToString("N"),
            SentAt: DateTimeOffset.UtcNow,
            Mode: "backfill", // Mode backfill para Railway
            Movements: dtos
        );
    }
}
