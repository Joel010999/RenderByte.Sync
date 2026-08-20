using System.Text.Json;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Contracts;

namespace RenderByte.Sync.Agent;

public static class StocksSyncOnceAgent
{
    public static async Task<int> RunAsync(string sourceId, IStockReader reader, string[] args, CancellationToken ct, IStockStore? storeOverride = null, HttpMessageHandler? httpHandler = null)
    {
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

        Console.WriteLine($"[STOCKS] BranchId: {branchId}. Iniciando lectura del stock...");

        var snapshot = await reader.GetFullSnapshotAsync(branchId, ct);
        Console.WriteLine($"[STOCKS] Snapshot recuperado: {snapshot.Count} artículos de Alegon.");

        var states = await store.GetStockStatesAsync(ct);

        int news = 0;
        int changed = 0;
        int unchanged = 0;
        var presentKeysInSnapshot = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stock in snapshot)
        {
            var businessKey = StockCanonicalizer.ComputeBusinessKey(sourceId, stock.Depo, stock.ArticleId, stock.Bulto);
            var contentHash = StockCanonicalizer.ComputeContentHash(stock, isPresent: true);
            
            // Decidí no guardar todo el objeto original en payload de la BD SQLite
            // solo para stock, sino solo los decimales (para optimizar espacio si es necesario),
            // pero para consistencia con Products usaré el objeto serializado.
            // O mejor aún, el objeto Dto para evitar problemas de comas flotantes si lo leemos directo?
            // Dejamos el objeto puro de db Alegon.
            var payload = JsonSerializer.Serialize(stock);
            
            presentKeysInSnapshot.Add(businessKey);

            if (!states.TryGetValue(businessKey, out var state))
            {
                await store.UpsertStockStateAndOutboxAsync(sourceId, branchId, stock, businessKey, contentHash, payload, ct);
                news++;
            }
            else if (state.ContentHash != contentHash || !state.IsPresent)
            {
                await store.UpsertStockStateAndOutboxAsync(sourceId, branchId, stock, businessKey, contentHash, payload, ct);
                changed++;
            }
            else
            {
                unchanged++;
            }
        }

        // Detectar borrados
        int missing = 0;
        foreach (var state in states.Values)
        {
            if (state.IsPresent && !presentKeysInSnapshot.Contains(state.BusinessKey))
            {
                var tombstoneStock = new AlegonStock(state.Depo, state.ArticleId, state.Bulto, null, null, null, null);
                var tombstoneHash = StockCanonicalizer.ComputeContentHash(tombstoneStock, isPresent: false);

                await store.MarkStockMissingAndCreateTombstoneAsync(sourceId, branchId, state.BusinessKey, state.Depo, state.ArticleId, state.Bulto, tombstoneHash, ct);
                missing++;
            }
        }

        var outboxCreated = news + changed + missing;
        Console.WriteLine($"[STOCKS]");
        Console.WriteLine($"snapshot={snapshot.Count}");
        Console.WriteLine($"new={news}");
        Console.WriteLine($"changed={changed}");
        Console.WriteLine($"unchanged={unchanged}");
        Console.WriteLine($"missing={missing}");
        Console.WriteLine($"outbox created={outboxCreated}");

        var apiUrl = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("[ERROR] Faltan variables de entorno para API. Los stocks quedaron en outbox.");
            return 2;
        }

        using var client = new HttpSyncClient(apiUrl, apiKey, httpHandler);

        while (true)
        {
            var pending = await store.GetPendingStockOutboxAsync(1000, ct);
            if (pending.Count == 0)
            {
                break;
            }

            Console.WriteLine($"[STOCK SYNC] Enviando lote de {pending.Count} stocks a la API...");

            var dtos = pending.Select(m => new SyncStockDto
            {
                BusinessKey = m.BusinessKey,
                ContentHash = m.ContentHash,
                Depo = m.Depo,
                ArticleId = m.ArticleId,
                Bulto = m.Bulto,
                Costo = m.Costo?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Precio = m.Precio?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Saldo = m.Saldo?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Piezas = m.Piezas?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsPresent = m.IsPresent
            }).ToList();

            var batchId = Guid.NewGuid().ToString("D");
            var req = new SyncStockBatchRequest 
            { 
                SourceId = sourceId, 
                BranchId = branchId, 
                BatchId = batchId, 
                Stocks = dtos 
            };

            try
            {
                var res = await client.SendStocksBatchAsync(req, ct);

                if (res != null)
                {
                    Console.WriteLine($"accepted={res.Accepted}");
                    Console.WriteLine($"inserted={res.Inserted}");
                    Console.WriteLine($"updated={res.Updated}");
                    Console.WriteLine($"unchanged={res.Unchanged}");

                    if (res.BatchId != batchId)
                    {
                        Console.Error.WriteLine($"[STOCK SYNC] ERROR: El batch_id del ACK ({res.BatchId}) no coincide con el enviado ({batchId}).");
                        return 1;
                    }

                    if (res.Accepted == pending.Count && (res.Inserted + res.Updated + res.Unchanged) == res.Accepted)
                    {
                        foreach (var msg in pending)
                        {
                            await store.MarkStockOutboxSentAsync(msg.Id, ct);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("[STOCK SYNC] Discrepancia en la respuesta del ACK. No se marcaron como sent.");
                        return 1;
                    }
                }
            }
            catch (SyncApiException ex)
            {
                Console.Error.WriteLine($"[STOCK SYNC] ERROR HTTP {(int)ex.StatusCode}: {ex.Message}");
                foreach (var msg in pending)
                {
                    await store.MarkStockOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[STOCK SYNC] ERROR: {ex.Message}");
                foreach (var msg in pending)
                {
                    await store.MarkStockOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                return 1;
            }
        }

        return 0;
    }
}
