using System;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent.Services;

public class MovementSyncService
{
    private readonly IAlegonReader _reader;
    private readonly ISyncBatchStore _store;
    private readonly SyncTransportService _transport;
    private readonly int _branchId;
    private readonly int _readBatchSize;
    private readonly int _uploadBatchSize;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public MovementSyncService(
        IAlegonReader reader, 
        ISyncBatchStore store, 
        SyncTransportService transport, 
        int branchId,
        Microsoft.Extensions.Logging.ILogger logger,
        int readBatchSize = 100,
        int uploadBatchSize = 200)
    {
        _reader = reader;
        _store = store;
        _transport = transport;
        _branchId = branchId;
        _logger = logger;
        _readBatchSize = readBatchSize;
        _uploadBatchSize = uploadBatchSize;
    }

    public async Task<int> CaptureAsync(CancellationToken ct = default)
    {
        var cpRow = await _store.GetCheckpointAsync(ct);
        if (cpRow == null)
        {
            throw new InvalidOperationException("[ERROR] No hay checkpoint persistido. M7 no admite backfill histórico. Ejecute bootstrap primero.");
        }
        var checkpoint = cpRow.ToMovementCheckpoint();

        var movements = await _reader.GetMovementsAfterAsync(_branchId, checkpoint, _readBatchSize, ct);
        if (movements.Count > 0)
        {
            var cpAfter = MovementCheckpoint.From(movements[^1]);
            var res = await _store.PersistBatchAndCheckpointAsync(_branchId, movements, cpAfter, ct);
            
            _logger.LogInformation("[MOVEMENTS CAPTURE] captured={Count} pending={Pending}", movements.Count, res.Inserted);
            return movements.Count;
        }
        return 0;
    }

    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        var (success, sentCount) = await _transport.SendPendingAsync(_uploadBatchSize, ct);
        if (!success)
        {
            throw new Exception("HTTP Sync failed or returned non-success");
        }
        if (sentCount > 0)
        {
            _logger.LogInformation("[MOVEMENTS SYNC] accepted={Count}", sentCount);
        }
        return sentCount;
    }
}
