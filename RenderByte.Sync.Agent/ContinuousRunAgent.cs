using System;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class ContinuousRunAgent
{
    // Función delegada para esperar (abstracción para tests)
    public static Func<TimeSpan, CancellationToken, Task> DelayTask { get; set; } = Task.Delay;

    public static async Task<int> RunAsync(SyncAgentOptions options, IAlegonReader reader, CancellationToken ct, HttpMessageHandler? httpHandler = null)
    {
        Console.WriteLine("[START] RenderByte Sync - Continuous Mode");
        
        SyncInstanceGuard? guard = null;
        try
        {
            guard = SyncInstanceGuard.AcquireOrThrow(options.SourceId);
        }
        catch (SyncAlreadyRunningException ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}. Agente detenido.");
            return 3;
        }

        using (guard)
        {
            var dbPath = SyncDbPath.Resolve();
            await using var store = new SqliteSyncBatchStore(dbPath);

            int branchId;
            try
            {
                branchId = await reader.GetBranchNumberAsync(ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] No se pudo obtener branchId de Alegon: {ex.Message}");
                return 1; // Fatal si no arranca
            }

            try
            {
                await store.InitializeAsync(options.SourceId, branchId, ct);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.Message}. Agente detenido.");
                return 2;
            }

            var cpRow = await store.GetCheckpointAsync(ct);
            if (cpRow == null)
            {
                Console.Error.WriteLine("[ERROR] No hay checkpoint persistido. M7 no admite backfill histórico. Ejecute bootstrap primero.");
                return 1;
            }

            var checkpoint = cpRow.ToMovementCheckpoint();
            Console.WriteLine($"[source] {options.SourceId}");
            Console.WriteLine($"[branch] {branchId}");
            Console.WriteLine($"[checkpoint] {checkpoint}");

            using var client = new HttpSyncClient(options.ApiUrl, options.ApiKey, httpHandler);
            var transport = new SyncTransportService(store, client, options.SourceId);

            int alegonErrors = 0;
            int httpErrors = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool capturedData = false;
                    bool sentData = false;

                    // 1. CAPTURE
                    try
                    {
                        var movements = await reader.GetMovementsAfterAsync(branchId, checkpoint, options.ReadBatchSize, ct);
                        if (movements.Count > 0)
                        {
                            var cpAfter = MovementCheckpoint.From(movements[^1]);
                            var res = await store.PersistBatchAndCheckpointAsync(branchId, movements, cpAfter, ct);
                            checkpoint = cpAfter; // Update local memory

                            Console.WriteLine($"[capture] {movements.Count} movimientos encontrados");
                            Console.WriteLine($"[outbox] inserted={res.Inserted} duplicates={res.DuplicatesSkipped}");
                            
                            alegonErrors = 0;
                            capturedData = true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        alegonErrors++;
                        var wait = GetBackoff(alegonErrors);
                        Console.Error.WriteLine($"[WARN] SQL Server no disponible: {ex.Message}. Reintento en {wait.TotalSeconds}s.");
                        await DelayTask(wait, ct);
                    }

                    // 2. SEND PENDING
                    if (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            var (success, sentCount) = await transport.SendPendingAsync(options.UploadBatchSize, ct);
                            if (success)
                            {
                                httpErrors = 0;
                                if (sentCount > 0)
                                {
                                    Console.WriteLine($"[sync] sending={sentCount}");
                                    Console.WriteLine($"[sync] marked sent={sentCount}");
                                    sentData = true;
                                }
                            }
                            else
                            {
                                httpErrors++;
                                var wait = GetBackoff(httpErrors);
                                Console.Error.WriteLine($"[WARN] Railway HTTP transitorio. Pending preservado. Reintento en {wait.TotalSeconds}s.");
                                await DelayTask(wait, ct);
                            }
                        }
                        catch (SyncApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden || ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            Console.Error.WriteLine($"[ERROR] FATAL HTTP {(int)ex.StatusCode}. Agente detenido.");
                            return 1;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            httpErrors++;
                            var wait = GetBackoff(httpErrors);
                            Console.Error.WriteLine($"[WARN] Error transporte: {ex.Message}. Reintento en {wait.TotalSeconds}s.");
                            await DelayTask(wait, ct);
                        }
                    }
                    
                    // 3. IDLE WAIT
                    if (!capturedData && !sentData && !ct.IsCancellationRequested && alegonErrors == 0 && httpErrors == 0)
                    {
                        Console.WriteLine($"[idle] sin novedades. Esperando {options.PollSeconds}s...");
                        await DelayTask(TimeSpan.FromSeconds(options.PollSeconds), ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[STOP] Agente detenido por cancelación.");
            }

            Console.WriteLine("[shutdown] Deteniendo RenderByte Sync...");
            return 0;
        }
    }

    private static TimeSpan GetBackoff(int errors)
    {
        return errors switch
        {
            1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(15),
            3 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(60)
        };
    }
}
