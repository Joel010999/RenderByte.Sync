using System;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Infrastructure.Alegon;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Agent.Services;
using RenderByte.Sync.Agent.Configuration;

namespace RenderByte.Sync.Agent;

public static class ContinuousRunAgent
{
    public static Func<TimeSpan, CancellationToken, Task> DelayTask { get; set; } = Task.Delay;
    public static Func<DateTimeOffset> GetUtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public static async Task<int> RunAsync(
        ResolvedSyncOptions options, 
        IAlegonReader reader, 
        CancellationToken ct, 
        HttpMessageHandler? httpHandler = null,
        IProductReader? productReaderOverride = null,
        IStockReader? stockReaderOverride = null)
    {
        Console.WriteLine("[START] RenderByte Sync - Unified Continuous Mode");
        
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
                return 1;
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

            Console.WriteLine($"[source] {options.SourceId}");
            Console.WriteLine($"[branch] {branchId}");

            var cp = await store.GetCheckpointAsync(ct);
            if (cp == null)
            {
                Console.Error.WriteLine("[ERROR] No hay checkpoint persistido. M7 no admite backfill histórico. Ejecute bootstrap primero.");
                return 1;
            }

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
                        try { await movementService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] MOVEMENTS CAPTURE failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            movementCaptureErrors++;
                            var wait = GetBackoff(movementCaptureErrors);
                            Console.Error.WriteLine($"[SCHEDULER] MOVEMENTS CAPTURE backoff {wait.TotalSeconds}s.");
                            nextMovementCaptureAttempt = now + wait;
                        }
                        else
                        {
                            movementCaptureErrors = 0;
                            nextMovementCaptureAttempt = now + TimeSpan.FromSeconds(options.MovementIntervalSeconds);
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
                            Console.Error.WriteLine($"[WARN] MOVEMENTS SYNC failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            movementTransportErrors++;
                            var wait = GetBackoff(movementTransportErrors);
                            Console.Error.WriteLine($"[SCHEDULER] MOVEMENTS SYNC backoff {wait.TotalSeconds}s.");
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
                            Console.Error.WriteLine($"[WARN] STOCK CAPTURE failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            stockCaptureErrors++;
                            var wait = GetBackoff(stockCaptureErrors);
                            Console.Error.WriteLine($"[SCHEDULER] STOCK CAPTURE backoff {wait.TotalSeconds}s.");
                            nextStockCaptureAttempt = now + wait;
                        }
                        else
                        {
                            stockCaptureErrors = 0;
                            nextStockCaptureAttempt = now + TimeSpan.FromSeconds(options.StockIntervalSeconds);
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
                            Console.Error.WriteLine($"[WARN] STOCK SYNC failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            stockTransportErrors++;
                            var wait = GetBackoff(stockTransportErrors);
                            Console.Error.WriteLine($"[SCHEDULER] STOCK SYNC backoff {wait.TotalSeconds}s.");
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
                            Console.Error.WriteLine($"[WARN] PRODUCTS CAPTURE failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            productCaptureErrors++;
                            var wait = GetBackoff(productCaptureErrors);
                            Console.Error.WriteLine($"[SCHEDULER] PRODUCTS CAPTURE backoff {wait.TotalSeconds}s.");
                            nextProductCaptureAttempt = now + wait;
                        }
                        else
                        {
                            productCaptureErrors = 0;
                            nextProductCaptureAttempt = now + TimeSpan.FromSeconds(options.ProductIntervalSeconds);
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
                            Console.Error.WriteLine($"[WARN] PRODUCTS SYNC failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            productTransportErrors++;
                            var wait = GetBackoff(productTransportErrors);
                            Console.Error.WriteLine($"[SCHEDULER] PRODUCTS SYNC backoff {wait.TotalSeconds}s.");
                            nextProductTransportAttempt = now + wait;
                        }
                        else
                        {
                            productTransportErrors = 0;
                            nextProductTransportAttempt = now + transportIdleInterval;
                        }
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
