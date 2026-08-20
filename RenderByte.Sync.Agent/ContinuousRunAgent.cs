using System;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Infrastructure.Alegon;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Agent.Services;

namespace RenderByte.Sync.Agent;

public static class ContinuousRunAgent
{
    public static Func<TimeSpan, CancellationToken, Task> DelayTask { get; set; } = Task.Delay;
    public static Func<DateTimeOffset> GetUtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public static async Task<int> RunAsync(
        SyncAgentOptions options, 
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

            DateTimeOffset nextMovementAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextStockAttempt = DateTimeOffset.MinValue;
            DateTimeOffset nextProductAttempt = DateTimeOffset.MinValue;

            int movementErrors = 0;
            int stockErrors = 0;
            int productErrors = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = GetUtcNow();
                    var tolerance = TimeSpan.FromMilliseconds(50);
                    
                    TimeSpan delayMov = now + tolerance < nextMovementAttempt ? nextMovementAttempt - now : TimeSpan.Zero;
                    TimeSpan delayStk = now + tolerance < nextStockAttempt ? nextStockAttempt - now : TimeSpan.Zero;
                    TimeSpan delayPrd = now + tolerance < nextProductAttempt ? nextProductAttempt - now : TimeSpan.Zero;
                    
                    TimeSpan minDelay = delayMov;
                    if (delayStk < minDelay) minDelay = delayStk;
                    if (delayPrd < minDelay) minDelay = delayPrd;
                    
                    if (minDelay > TimeSpan.Zero)
                    {
                        await DelayTask(minDelay, ct);
                        now = GetUtcNow();
                    }

                    // Priorities: 1. Movements, 2. Stock, 3. Products
                    if (now + tolerance >= nextMovementAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await movementService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] MOVEMENTS capture failure: {ex.Message}");
                            hadError = true;
                        }

                        try { await movementService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] MOVEMENTS transport failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            movementErrors++;
                            var wait = GetBackoff(movementErrors);
                            Console.Error.WriteLine($"[SCHEDULER] MOVEMENTS backoff {wait.TotalSeconds}s.");
                            nextMovementAttempt = now + wait;
                        }
                        else
                        {
                            movementErrors = 0;
                            nextMovementAttempt = now + TimeSpan.FromSeconds(options.MovementIntervalSeconds);
                        }
                    }

                    now = GetUtcNow();
                    if (now + tolerance >= nextStockAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await stockService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] STOCK capture failure: {ex.Message}");
                            hadError = true;
                        }

                        try { await stockService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] STOCK transport failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            stockErrors++;
                            var wait = GetBackoff(stockErrors);
                            Console.Error.WriteLine($"[SCHEDULER] STOCK backoff {wait.TotalSeconds}s.");
                            nextStockAttempt = now + wait;
                        }
                        else
                        {
                            stockErrors = 0;
                            nextStockAttempt = now + TimeSpan.FromSeconds(options.StockIntervalSeconds);
                        }
                    }

                    now = GetUtcNow();
                    if (now + tolerance >= nextProductAttempt && !ct.IsCancellationRequested)
                    {
                        bool hadError = false;
                        try { await productService.CaptureAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] PRODUCTS capture failure: {ex.Message}");
                            hadError = true;
                        }

                        try { await productService.SendPendingAsync(ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[WARN] PRODUCTS transport failure: {ex.Message}");
                            hadError = true;
                        }

                        if (hadError)
                        {
                            productErrors++;
                            var wait = GetBackoff(productErrors);
                            Console.Error.WriteLine($"[SCHEDULER] PRODUCTS backoff {wait.TotalSeconds}s.");
                            nextProductAttempt = now + wait;
                        }
                        else
                        {
                            productErrors = 0;
                            nextProductAttempt = now + TimeSpan.FromSeconds(options.ProductIntervalSeconds);
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
