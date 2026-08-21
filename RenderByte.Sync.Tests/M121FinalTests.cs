using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Agent.Services;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Persistence;
using Xunit;

namespace RenderByte.Sync.Tests;

[Collection("EnvVars")]
public class M121FinalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ResolvedSyncOptions _options;

    public M121FinalTests()
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
    public async Task ServiceMode_RailwayOfflineAtStartup_RemainsRunning()
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
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        var loggerMock = new Mock<ILogger>();
        var statusWriterMock = new Mock<ISyncStatusWriter>();
        
        // Simulate Railway offline by injecting a throwing HttpMessageHandler
        var mockHttp = new Mock<System.Net.Http.HttpMessageHandler>();
        mockHttp.Protected()
            .Setup<Task<System.Net.Http.HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<System.Net.Http.HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new System.Net.Http.HttpRequestException("Railway is offline"));

        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();
        
        using var cts = new CancellationTokenSource();
        var exitCodeTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, loggerMock.Object, statusWriterMock.Object, mockHttp.Object, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(500);
        cts.Cancel();
        
        var exitCode = await exitCodeTask;
        Assert.Equal(0, exitCode); // Must not crash!
    }

    [Fact]
    public async Task StatusWriter_IsActuallyInvoked()
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
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        var loggerMock = new Mock<ILogger>();
        var statusWriterMock = new Mock<ISyncStatusWriter>();
        
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();
        
        using var cts = new CancellationTokenSource();
        var exitCodeTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, loggerMock.Object, statusWriterMock.Object, null, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(500); // Give it time to run the loop at least once
        cts.Cancel();
        await exitCodeTask;

        // Verify WriteStatusAsync was called at least twice (once at start, once in loop)
        statusWriterMock.Verify(s => s.WriteStatusAsync(It.IsAny<SyncStatus>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task StatusWriter_WritesAtomicStatus()
    {
        var tempStatusFile = Path.Combine(Path.GetTempPath(), $"status_{Guid.NewGuid():N}.json");
        try
        {
            var writer = new SyncStatusWriter(tempStatusFile);
            var status = new SyncStatus("1.0", "SRC-01", 1, DateTime.UtcNow, null, null, null, 0, 0, 0, null);
            
            await writer.WriteStatusAsync(status);
            
            Assert.True(File.Exists(tempStatusFile));
            var contents = File.ReadAllText(tempStatusFile);
            Assert.Contains("SRC-01", contents);
            Assert.False(File.Exists(tempStatusFile + ".tmp"));
        }
        finally
        {
            if (File.Exists(tempStatusFile)) File.Delete(tempStatusFile);
        }
    }

    [Fact]
    public async Task ServiceInstall_FailsOnSqliteSourceMismatch()
    {
        // 1. Initialize DB with SRC-99
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync("SRC-99", 1);
        }

        // 2. Mock configuration to return SRC-01
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE", "1");
        
        // This test simulates the logic inside ServiceInstallCommandAgent
        // because we can't easily mock the internal hardcoded config path, we just test the sqlite verification logic.
        
        var store2 = new SqliteSyncBatchStore(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store2.OpenExistingInstallationAsync("SRC-01"));
        Assert.Contains("[SOURCE MISMATCH]", ex.Message);
    }
}
