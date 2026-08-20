using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent.Services;

public class StockSyncService
{
    private readonly IStockReader _reader;
    private readonly IStockStore _store;
    private readonly HttpSyncClient _client;
    private readonly string _sourceId;
    private readonly int _branchId;

    public StockSyncService(
        IStockReader reader,
        IStockStore store,
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
        var snapshot = await _reader.GetFullSnapshotAsync(_branchId, ct);
        var states = await _store.GetStockStatesAsync(ct);

        int news = 0;
        int changed = 0;
        int unchanged = 0;
        var presentKeysInSnapshot = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stock in snapshot)
        {
            var businessKey = StockCanonicalizer.ComputeBusinessKey(_sourceId, stock.Depo, stock.ArticleId, stock.Bulto);
            var contentHash = StockCanonicalizer.ComputeContentHash(stock, isPresent: true);
            var payload = JsonSerializer.Serialize(stock);
            presentKeysInSnapshot.Add(businessKey);

            if (!states.TryGetValue(businessKey, out var state))
            {
                await _store.UpsertStockStateAndOutboxAsync(_sourceId, _branchId, stock, businessKey, contentHash, payload, ct);
                news++;
            }
            else if (state.ContentHash != contentHash || !state.IsPresent)
            {
                await _store.UpsertStockStateAndOutboxAsync(_sourceId, _branchId, stock, businessKey, contentHash, payload, ct);
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

                await _store.MarkStockMissingAndCreateTombstoneAsync(_sourceId, _branchId, state.BusinessKey, state.Depo, state.ArticleId, state.Bulto, tombstoneHash, ct);
                missing++;
            }
        }

        var outboxCreated = news + changed + missing;
        Console.WriteLine($"[STOCK] snapshot={snapshot.Count} changed={outboxCreated} unchanged={unchanged}");
        
        return (snapshot.Count, outboxCreated);
    }

    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        int totalSent = 0;
        while (true)
        {
            var pending = await _store.GetPendingStockOutboxAsync(1000, ct);
            if (pending.Count == 0)
            {
                break;
            }

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
                SourceId = _sourceId, 
                BranchId = _branchId, 
                BatchId = batchId, 
                Stocks = dtos 
            };

            try
            {
                var res = await _client.SendStocksBatchAsync(req, ct);

                if (res != null)
                {
                    if (res.Accepted == pending.Count && (res.Inserted + res.Updated + res.Unchanged) == res.Accepted)
                    {
                        foreach (var msg in pending)
                        {
                            await _store.MarkStockOutboxSentAsync(msg.Id, ct);
                        }
                        totalSent += res.Accepted;
                        Console.WriteLine($"[STOCK SYNC] accepted={res.Accepted}");
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
                    await _store.MarkStockOutboxErrorAsync(msg.Id, ex.Message, ct);
                }
                throw;
            }
        }
        return totalSent;
    }
}
