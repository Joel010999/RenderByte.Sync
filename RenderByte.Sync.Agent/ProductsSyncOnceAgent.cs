using System.Text.Json;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Persistence;
using RenderByte.Sync.Contracts;

namespace RenderByte.Sync.Agent;

public static class ProductsSyncOnceAgent
{
    public static async Task<int> RunAsync(string sourceId, IProductReader reader, string[] args, CancellationToken ct, IProductStore? storeOverride = null, HttpMessageHandler? httpHandler = null)
    {
        IProductStore store;
        if (storeOverride != null)
        {
            store = storeOverride;
        }
        else
        {
            var dbPath = SyncDbPath.Resolve();
            var realStore = new SqliteSyncBatchStore(dbPath);
            Console.WriteLine("[PRODUCTS] Conectando a SQLite local...");
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

        Console.WriteLine($"[PRODUCTS] BranchId: {branchId}. Iniciando lectura del catálogo...");

        var snapshot = await reader.GetFullSnapshotAsync(ct);
        Console.WriteLine($"[PRODUCTS] Snapshot recuperado: {snapshot.Count} artículos de Alegon.");

        var states = await store.GetStatesAsync(ct);

        int news = 0;
        int changed = 0;
        int unchanged = 0;
        var presentKeysInSnapshot = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prod in snapshot)
        {
            var businessKey = ProductCanonicalizer.ComputeBusinessKey(sourceId, prod.ArticleId);
            var contentHash = ProductCanonicalizer.ComputeContentHash(prod);
            var payload = JsonSerializer.Serialize(prod);
            presentKeysInSnapshot.Add(businessKey);

            if (!states.TryGetValue(businessKey, out var state))
            {
                await store.UpsertStateAndOutboxAsync(sourceId, branchId, prod, businessKey, contentHash, payload, ct);
                news++;
            }
            else if (state.ContentHash != contentHash || !state.IsPresent)
            {
                await store.UpsertStateAndOutboxAsync(sourceId, branchId, prod, businessKey, contentHash, payload, ct);
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
                await store.MarkMissingAndCreateTombstoneAsync(sourceId, branchId, state.BusinessKey, state.ArticleId, ct);
                missing++;
            }
        }

        var outboxCreated = news + changed + missing;
        Console.WriteLine($"[PRODUCTS]");
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
            Console.Error.WriteLine("[ERROR] Faltan variables de entorno para API. Los productos quedaron en outbox.");
            return 2;
        }

        using var client = new HttpSyncClient(apiUrl, apiKey, httpHandler);

        while (true)
        {
            var pending = await store.GetPendingOutboxAsync(1000, ct);
            if (pending.Count == 0)
            {
                break;
            }

            Console.WriteLine($"[PRODUCT SYNC] Enviando lote de {pending.Count} productos a la API...");

            var dtos = pending.Select(m => new ProductSyncDto(
                BusinessKey: m.BusinessKey,
                ContentHash: m.ContentHash,
                ArticleId: m.ArticleId,
                Payload: m.Payload
            )).ToList();

            var batchId = Guid.NewGuid().ToString("D");
            var req = new ProductSyncRequest(sourceId, branchId, batchId, dtos);

            try
            {
                var res = await client.SendProductsBatchAsync(req, ct);

                if (res != null)
                {
                    Console.WriteLine($"accepted={res.Accepted}");
                    Console.WriteLine($"inserted={res.Inserted}");
                    Console.WriteLine($"updated={res.Updated}");
                    Console.WriteLine($"unchanged={res.Unchanged}");

                    if (res.Accepted == pending.Count && (res.Inserted + res.Updated + res.Unchanged) == res.Accepted)
                    {
                        foreach (var msg in pending)
                        {
                            await store.MarkOutboxSentAsync(msg.Id, ct);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("[PRODUCT SYNC] Discrepancia en la respuesta del ACK. No se marcaron como sent.");
                        return 1;
                    }
                }
            }
            catch (SyncApiException ex)
            {
                Console.Error.WriteLine($"[PRODUCT SYNC] ERROR HTTP {(int)ex.StatusCode}: {ex.Message}");
                foreach (var msg in pending)
                {
                    await store.MarkOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PRODUCT SYNC] ERROR: {ex.Message}");
                foreach (var msg in pending)
                {
                    await store.MarkOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                return 1;
            }
        }

        return 0;
    }
}
