namespace RenderByte.Sync.Agent.Services;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class SyncStatusWriter : ISyncStatusWriter
{
    private readonly string _statusFilePath;
    private readonly object _lock = new();

    public SyncStatusWriter(string statusFilePath)
    {
        _statusFilePath = statusFilePath;
    }

    public Task WriteStatusAsync(SyncStatus status, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var tempFile = _statusFilePath + ".tmp";
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _statusFilePath, overwrite: true);
        }
        return Task.CompletedTask;
    }
}

public record SyncStatus(
    string ServiceVersion,
    string SourceId,
    int? BranchId,
    DateTime? StartedAtUtc,
    DateTime? LastMovementSuccessUtc,
    DateTime? LastStockSuccessUtc,
    DateTime? LastProductSuccessUtc,
    int? MovementPending,
    int? StockPending,
    int? ProductPending,
    string? LastError
);
