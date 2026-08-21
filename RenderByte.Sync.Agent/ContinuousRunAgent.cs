using System;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Infrastructure.Alegon;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Agent.Services;
using RenderByte.Sync.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace RenderByte.Sync.Agent;

public static class ContinuousRunAgent
{
    public static Func<TimeSpan, CancellationToken, Task> DelayTask { get; set; } = Task.Delay;
    public static Func<DateTimeOffset> GetUtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public static async Task<int> RunAsync(
        ResolvedSyncOptions options, 
        IAlegonReader reader, 
        CancellationToken ct, 
        ILogger logger,
        ISyncStatusWriter statusWriter,
        HttpMessageHandler? httpHandler = null,
        IProductReader? productReaderOverride = null,
        IStockReader? stockReaderOverride = null)
    {
        logger.LogInformation("[START] RenderByte Sync - Unified Continuous Mode");
        
        var dbPath = SyncDbPath.Resolve();
        await using var store = new SqliteSyncBatchStore(dbPath);

        int branchId = 0;
        bool isOfflineStartup = false;
        try
        {
            branchId = await reader.GetBranchNumberAsync(ct);
            await store.InitializeAsync(options.SourceId, branchId, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogCritical(ex, "[ERROR] {Message}. Agente detenido.", ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WARN] No se pudo obtener branchId de Alegon. Intentando arrancar en modo offline con metadata persistida.");
            try
            {
                await store.OpenExistingInstallationAsync(options.SourceId, ct);
                var cp = await store.GetCheckpointAsync(ct);
                if (cp != null)
                {
                    branchId = cp.BranchId;
                    isOfflineStartup = true;
                    logger.LogInformation("[INFO] Arrancando en modo offline usando branchId persistido: {branchId}", branchId);
                }
                else
                {
                    logger.LogError("[ERROR] No se puede arrancar offline: no hay checkpoint previo persistido.");
                    return 1;
                }
            }
            catch (InvalidOperationException offlineInvEx)
            {
                logger.LogCritical(offlineInvEx, "[ERROR] {Message}. Agente detenido.", offlineInvEx.Message);
                return 2;
            }
            catch (Exception offlineEx)
            {
                logger.LogError(offlineEx, "[ERROR] Falló la inicialización local offline.");
                return 1;
            }
        }

        logger.LogInformation("[source] {SourceId}", options.SourceId);
        logger.LogInformation("[branch] {BranchId}", branchId);

        var startCp = await store.GetCheckpointAsync(ct);
        if (startCp == null)
        {
            logger.LogError("[ERROR] No hay checkpoint persistido. M7 no admite backfill histórico. Ejecute bootstrap primero.");
            return 1;
        }

        var status = new SyncStatus(
            ServiceVersion: "0.12.0",
            SourceId: options.SourceId,
            BranchId: branchId,
            StartedAtUtc: GetUtcNow().UtcDateTime,
            LastUpdatedUtc: GetUtcNow().UtcDateTime,
            LastMovementSuccessUtc: null,
            LastStockSuccessUtc: null,
            LastProductSuccessUtc: null,
            MovementPending: 0,
            StockPending: 0,
            ProductPending: 0,
            LastError: null
        );
        await statusWriter.WriteStatusAsync(status, ct);

            using var client = new HttpSyncClient(options.ApiUrl, options.ApiKey, httpHandler);
            var transport = new SyncTransportService(store, client, options.SourceId);

            var movementReader = reader;
            var productReader = productReaderOverride ?? new AlegonProductReader(options.AlegonConnectionString);
            var stockReader = stockReaderOverride ?? new AlegonStockReader(options.AlegonConnectionString);

            var movementService = new MovementSyncService(movementReader, store, transport, branchId, options.ReadBatchSize, options.UploadBatchSize);
            var productService = new ProductSyncService(productReader, store, client, options.SourceId, branchId);
            var stockService = new StockSyncService(stockReader, store, client, options.SourceId, branchId);

            DateTimeOffset nextMovementCaptureAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextMovementTransportAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextStockCaptureAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextStockTransportAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextProductCaptureAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextProductTransportAttempt = DateTimeOffset.MinValue;

            int movementCaptureErrors = 0;
            int movementTransportErrors = 0;
            int stockCaptureErrors = 0;
            int stockTransportErrors = 0;
            int productCaptureErrors = 0;
            int productTransportErrors = 0;

            TimeSpan transportIdleInterval = TimeSpan.FromSeconds(60);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool wroteStatusThisCycle = false;
                    var now = GetUtcNow();
                    var tolerance = TimeSpan.FromMilliseconds(50);
                    
                    TimeSpan delayMovCap = now + tolerance < nextMovementCaptureAttempt ? nextMovementCaptureAttempt - now : TimeSpan.Zero;
                    TimeSpan delayMovTra = now + tolerance < nextMovementTransportAttempt ? nextMovementTransportAttempt - now : TimeSpan.Zero;
                    TimeSpan delayStkCap = now + tolerance < nextStockCaptureAttempt ? nextStockCaptureAttempt - now : TimeSpan.Zero;
                    TimeSpan delayStkTra = now + tolerance < nextStockTransportAttempt ? nextStockTransportAttempt - now : TimeSpan.Zero;
                    TimeSpan delayPrdCap = now + tolerance < nextProductCaptureAttempt ? nextProductCaptureAttempt - now : TimeSpan.Zero;
                    TimeSpan delayPrdTra = now + tolerance < nextProductTransportAttempt ? nextProductTransportAttempt - now : TimeSpan.Zero;
                    
                    TimeSpan[] delays = { delayMovCap, delayMovTra, delayStkCap, delayStkTra, delayPrdCap, delayPrdTra };
                    TimeSpan minDelay = delays[0];
                    for (int i = 1; i < delays.Length; i++)
                    {
                        if (delays[i] < minDelay) minDelay = delays[i];
                    }
                    
                    if (minDelay > TimeSpan.Zero)
                    {
                        await DelayTask(minDelay, ct);
                        now = GetUtcNow();
                    }

                    // 1. MOVEMENTS CAPTURE
                    if (now + tolerance >= nextMovementCaptureAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try 
                        { 
                            if (isOfflineStartup)
                            {
                                // Attempt to validate branch if offline
                                try
                                {
                                    var realBranch = await reader.GetBranchNumberAsync(ct);
                                    if (realBranch != branchId)
                                    {
                                        throw new InvalidOperationException($"[BRANCH MISMATCH] SCM Offline recovery: Persisted branch was {branchId}, but Alegon now reports {realBranch}.");
                                    }
                                    isOfflineStartup = false;
                                }
                                catch (Exception ex) when (ex is not InvalidOperationException)
                                {
                                    // Still offline, that's fine
                                }
                            }
                            await movementService.CaptureAsync(ct); 
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("[BRANCH MISMATCH]"))
                        {
                            logger.LogCritical(ex, "[FATAL] Inconsistencia de branch detectada.");
                            return 1;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] MOVEMENTS CAPTURE failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            movementCaptureErrors++;
                            var wait = GetBackoff(movementCaptureErrors);
                            logger.LogWarning("[SCHEDULER] MOVEMENTS CAPTURE backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextMovementCaptureAttempt = now + wait;
                        }
                        else
                        {
                            movementCaptureErrors = 0;
                            nextMovementCaptureAttempt = now + TimeSpan.FromSeconds(options.MovementIntervalSeconds);
                            status = status with { LastMovementSuccessUtc = GetUtcNow().UtcDateTime, LastUpdatedUtc = GetUtcNow().UtcDateTime };
                            wroteStatusThisCycle = true;
                        }
                    }

                    now = GetUtcNow();
                    // 2. MOVEMENTS TRANSPORT
                    if (now + tolerance >= nextMovementTransportAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await movementService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] MOVEMENTS SYNC failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            movementTransportErrors++;
                            var wait = GetBackoff(movementTransportErrors);
                            logger.LogWarning("[SCHEDULER] MOVEMENTS SYNC backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextMovementTransportAttempt = now + wait;
                        }
                        else
                        {
                            movementTransportErrors = 0;
                            nextMovementTransportAttempt = now + transportIdleInterval;
                        }
                    }

                    now = GetUtcNow();
                    // 3. STOCK CAPTURE
                    if (now + tolerance >= nextStockCaptureAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await stockService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] STOCK CAPTURE failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            stockCaptureErrors++;
                            var wait = GetBackoff(stockCaptureErrors);
                            logger.LogWarning("[SCHEDULER] STOCK CAPTURE backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextStockCaptureAttempt = now + wait;
                        }
                        else
                        {
                            stockCaptureErrors = 0;
                            nextStockCaptureAttempt = now + TimeSpan.FromSeconds(options.StockIntervalSeconds);
                            status = status with { LastStockSuccessUtc = GetUtcNow().UtcDateTime, LastUpdatedUtc = GetUtcNow().UtcDateTime };
                            wroteStatusThisCycle = true;
                        }
                    }

                    now = GetUtcNow();
                    // 4. STOCK TRANSPORT
                    if (now + tolerance >= nextStockTransportAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await stockService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] STOCK SYNC failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            stockTransportErrors++;
                            var wait = GetBackoff(stockTransportErrors);
                            logger.LogWarning("[SCHEDULER] STOCK SYNC backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextStockTransportAttempt = now + wait;
                        }
                        else
                        {
                            stockTransportErrors = 0;
                            nextStockTransportAttempt = now + transportIdleInterval;
                        }
                    }

                    now = GetUtcNow();
                    // 5. PRODUCTS CAPTURE
                    if (now + tolerance >= nextProductCaptureAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await productService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] PRODUCTS CAPTURE failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            productCaptureErrors++;
                            var wait = GetBackoff(productCaptureErrors);
                            logger.LogWarning("[SCHEDULER] PRODUCTS CAPTURE backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextProductCaptureAttempt = now + wait;
                        }
                        else
                        {
                            productCaptureErrors = 0;
                            nextProductCaptureAttempt = now + TimeSpan.FromSeconds(options.ProductIntervalSeconds);
                            status = status with { LastProductSuccessUtc = GetUtcNow().UtcDateTime, LastUpdatedUtc = GetUtcNow().UtcDateTime };
                            wroteStatusThisCycle = true;
                        }
                    }

                    now = GetUtcNow();
                    // 6. PRODUCTS TRANSPORT
                    if (now + tolerance >= nextProductTransportAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await productService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("[WARN] PRODUCTS SYNC failure: {Message}", ex.Message);
                            hadError = true;
                        }

                        if (hadError)
                        {
                            productTransportErrors++;
                            var wait = GetBackoff(productTransportErrors);
                            logger.LogWarning("[SCHEDULER] PRODUCTS SYNC backoff {WaitSeconds}s.", wait.TotalSeconds);
                            nextProductTransportAttempt = now + wait;
                        }
                        else
                        {
                            productTransportErrors = 0;
                            nextProductTransportAttempt = now + transportIdleInterval;
                        }
                    }

                    // Update and write status at the end of the loop iteration
                    if (wroteStatusThisCycle)
                    {
                        try
                        {
                            var movPending = (int)await store.GetPendingCountAsync(ct); // Approximation for status
                            // Assume stock/product pending are similarly derived, or just keep 0 for now as M12 scope
                            status = status with { 
                                MovementPending = movPending,
                                LastError = (movementCaptureErrors > 0 || movementTransportErrors > 0 || stockCaptureErrors > 0 || stockTransportErrors > 0 || productCaptureErrors > 0 || productTransportErrors > 0) ? "One or more pipelines are in error state." : null
                            };
                            await statusWriter.WriteStatusAsync(status, ct);
                        }
                        catch { /* Ignore status write failures to not crash the pipeline */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("[STOP] Agente detenido por cancelación.");
            }

            logger.LogInformation("[shutdown] Deteniendo RenderByte Sync...");
            return 0;
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
