using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent.Services;

public class ProductSyncService
{
    private readonly IProductReader _reader;
    private readonly IProductStore _store;
    private readonly HttpSyncClient _client;
    private readonly string _sourceId;
    private readonly int _branchId;

    public ProductSyncService(
        IProductReader reader,
        IProductStore store,
        HttpSyncClient client,
        string sourceId,
        int branchId)
    {
        _reader = reader;
        _store = store;
        _client = client;
        _sourceId = sourceId;
        _branchId = branchId;
    }

    public async Task<(int Snapshot, int Changed)> CaptureAsync(CancellationToken ct = default)
    {
        var snapshot = await _reader.GetFullSnapshotAsync(ct);
        var states = await _store.GetStatesAsync(ct);

        int news = 0;
        int changed = 0;
        int unchanged = 0;
        var presentKeysInSnapshot = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prod in snapshot)
        {
            var businessKey = ProductCanonicalizer.ComputeBusinessKey(_sourceId, prod.ArticleId);
            var contentHash = ProductCanonicalizer.ComputeContentHash(prod);
            var payload = JsonSerializer.Serialize(prod);
            presentKeysInSnapshot.Add(businessKey);

            if (!states.TryGetValue(businessKey, out var state))
            {
                await _store.UpsertStateAndOutboxAsync(_sourceId, _branchId, prod, businessKey, contentHash, payload, ct);
                news++;
            }
            else if (state.ContentHash != contentHash || !state.IsPresent)
            {
                await _store.UpsertStateAndOutboxAsync(_sourceId, _branchId, prod, businessKey, contentHash, payload, ct);
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
                await _store.MarkMissingAndCreateTombstoneAsync(_sourceId, _branchId, state.BusinessKey, state.ArticleId, ct);
                missing++;
            }
        }

        var outboxCreated = news + changed + missing;
        Console.WriteLine($"[PRODUCTS] snapshot={snapshot.Count} changed={outboxCreated} unchanged={unchanged}");
        
        return (snapshot.Count, outboxCreated);
    }

    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        int totalSent = 0;
        while (true)
        {
            var pending = await _store.GetPendingOutboxAsync(1000, ct);
            if (pending.Count == 0)
            {
                break;
            }

            var dtos = pending.Select(m => new ProductSyncDto(
                BusinessKey: m.BusinessKey,
                ContentHash: m.ContentHash,
                ArticleId: m.ArticleId,
                Payload: m.Payload
            )).ToList();

            var batchId = Guid.NewGuid().ToString("D");
            var req = new ProductSyncRequest(_sourceId, _branchId, batchId, dtos);

            try
            {
                var res = await _client.SendProductsBatchAsync(req, ct);

                if (res != null)
                {
                    if (res.Accepted == pending.Count && (res.Inserted + res.Updated + res.Unchanged) == res.Accepted)
                    {
                        foreach (var msg in pending)
                        {
                            await _store.MarkOutboxSentAsync(msg.Id, ct);
                        }
                        totalSent += res.Accepted;
                        Console.WriteLine($"[PRODUCTS SYNC] accepted={res.Accepted}");
                    }
                    else
                    {
                        throw new Exception("Discrepancia en la respuesta del ACK. No se marcaron como sent.");
                    }
                }
                else
                {
                    throw new Exception("HTTP Sync returned null");
                }
            }
            catch (Exception ex)
            {
                foreach (var msg in pending)
                {
                    await _store.MarkOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                throw;
            }
        }
        return totalSent;
    }
}
