namespace RenderByte.Sync.Tests;

using Xunit;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Agent.Services;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Persistence;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using System.IO;

[Collection("EnvVars")]
public class M121Tests : IDisposable
{
    private readonly string _dbPath;
    private readonly ResolvedSyncOptions _options;
    
    public M121Tests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.sqlite");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", _dbPath);
        _options = new ResolvedSyncOptions(
            "Server=.;Integrated Security=true", 
            "SRC-01", 
            "http://localhost", 
            "key",
            30, 60, 120);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", null);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task ServiceMode_AlegonOfflineAtStartup_RemainsRunning()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_options.SourceId, 1);
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at) VALUES (1, 1, '2024-01-01T10:00:00.0000000', 'X', 1, '2024-01-01T10:00:00.0000000');";
            await cmd.ExecuteNonQueryAsync();
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("Alegon is offline")); 

        var statusWriterMock = new Mock<ISyncStatusWriter>();
        
        var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger>();
        
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();
        
        using var cts = new CancellationTokenSource();
        var exitCodeTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, loggerMock.Object, statusWriterMock.Object, null, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(500);
        cts.Cancel();
        
        var exitCode = await exitCodeTask;
        if (exitCode != 0)
        {
            var invocations = loggerMock.Invocations;
            var sb = new System.Text.StringBuilder();
            foreach (var inv in invocations)
            {
                sb.AppendLine(inv.ToString());
                if (inv.Arguments.Count > 2)
                {
                    sb.AppendLine("Msg: " + inv.Arguments[2]?.ToString() + " Ex: " + inv.Arguments[3]?.ToString());
                }
            }
            throw new Exception("Test failed with exit code: " + exitCode + "\nLogs: " + sb.ToString());
        }
        Assert.Equal(0, exitCode);
    }
}
