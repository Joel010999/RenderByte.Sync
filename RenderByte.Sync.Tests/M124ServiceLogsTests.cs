using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using Xunit;

namespace RenderByte.Sync.Tests;

[Collection("EnvVars")]
public class M124ServiceLogsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ResolvedSyncOptions _options;
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<ISyncStatusWriter> _statusWriterMock;

    public M124ServiceLogsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.sqlite");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", _dbPath);
        _options = new ResolvedSyncOptions(
            "Server=.;Integrated Security=true;Password=SuperSecretPass", 
            "SRC-01", 
            "http://localhost", 
            "key",
            1, 1, 1);
        
        _loggerMock = new Mock<ILogger>();
        _loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _statusWriterMock = new Mock<ISyncStatusWriter>();
        
        ContinuousRunAgent.DelayTask = (delay, ct) => Task.CompletedTask;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", null);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private void VerifyLogContains(string text)
    {
        _loggerMock.Verify(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v != null && v.ToString() != null && v.ToString()!.Contains(text)),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), 
            Times.AtLeastOnce);
    }
    
    private void VerifyLogDoesNotContain(string text)
    {
        _loggerMock.Verify(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v != null && v.ToString() != null && v.ToString()!.Contains(text)),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), 
            Times.Never);
    }

    private async Task SetupDatabaseAsync()
    {
        await using var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(_options.SourceId, 1);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at) VALUES (1, 1, '2024-01-01T10:00:00.0000000', 'X', 1, '2024-01-01T10:00:00.0000000');";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Mock<IAlegonReader>, Mock<IStockReader>, Mock<IProductReader>, Mock<HttpMessageHandler>)> SetupMocksAsync()
    {
        await SetupDatabaseAsync();

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        readerMock.Setup(r => r.GetMovementsAfterAsync(1, It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonMovement>());

        var stockReaderMock = new Mock<IStockReader>();
        stockReaderMock.Setup(r => r.GetFullSnapshotAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonStock>());

        var productReaderMock = new Mock<IProductReader>();
        productReaderMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonProductMaster>());

        var httpHandlerMock = new Mock<HttpMessageHandler>();
        httpHandlerMock.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"accepted\": 1, \"inserted\": 1}") });
            
        return (readerMock, stockReaderMock, productReaderMock, httpHandlerMock);
    }

    [Fact]
    public async Task ServiceMode_LogsMovementCaptureSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        readerMock.Setup(r => r.GetMovementsAfterAsync(1, It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonMovement> {
                new AlegonMovement(1, "A", DateTime.UtcNow, "A", "A", "A", "A", "A", "A", 1, 1, DateTime.UtcNow, null, 1m, 1m, 1m, 1m, "A", 1m)
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[MOVEMENTS CAPTURE] captured=1");
    }

    [Fact]
    public async Task ServiceMode_LogsMovementTransportSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        readerMock.Setup(r => r.GetMovementsAfterAsync(1, It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonMovement> {
                new AlegonMovement(1, "A", DateTime.UtcNow, "A", "A", "A", "A", "A", "A", 1, 1, DateTime.UtcNow, null, 1m, 1m, 1m, 1m, "A", 1m)
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[MOVEMENTS SYNC] accepted=1");
    }

    [Fact]
    public async Task ServiceMode_LogsStockCaptureSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        stockReaderMock.Setup(r => r.GetFullSnapshotAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonStock> { new AlegonStock(1, 1, "A", 1m, 1m, 1m, 1m) });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[STOCK CAPTURE] snapshot=1");
    }

    [Fact]
    public async Task ServiceMode_LogsStockTransportSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        stockReaderMock.Setup(r => r.GetFullSnapshotAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonStock> { new AlegonStock(1, 1, "A", 1m, 1m, 1m, 1m) });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[STOCK SYNC] accepted=1");
    }
    
    [Fact]
    public async Task ServiceMode_LogsProductCaptureSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        productReaderMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonProductMaster>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[PRODUCTS CAPTURE] snapshot=0");
    }

    [Fact]
    public async Task ServiceMode_LogsProductTransportSuccess()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        
        // Product transport requires valid objects that do not throw during canonicalization
#pragma warning disable SYSLIB0050
        var prod = (AlegonProductMaster)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AlegonProductMaster));
#pragma warning restore SYSLIB0050
        // Reflection to set ArticleId to avoid null ref or other issues
        typeof(AlegonProductMaster).GetProperty("ArticleId")!.SetValue(prod, 1);
        // Not really needed if canonicalizer handles 0 safely, but we do need to avoid null Marca/Descripcion if Trim() is called.
        // Wait, Canonicalizer doesn't trim, the reader trims. Canonicalizer just does JSON serialization.
        
        productReaderMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonProductMaster> { prod });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[PRODUCTS SYNC] accepted=1");
    }

    [Fact]
    public async Task ServiceMode_LogsPipelineFailuresAndBackoff()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        readerMock.Setup(r => r.GetMovementsAfterAsync(1, It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Mocked database error"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogContains("[WARN] MOVEMENTS CAPTURE failure: Mocked database error");
        VerifyLogContains("[SCHEDULER] MOVEMENTS CAPTURE backoff");
    }

    [Fact]
    public async Task ServiceMode_DoesNotLogSecrets()
    {
        var (readerMock, stockReaderMock, productReaderMock, httpHandlerMock) = await SetupMocksAsync();
        readerMock.Setup(r => r.GetMovementsAfterAsync(1, It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Error to trigger log"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        
        try { await ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, _loggerMock.Object, _statusWriterMock.Object, httpHandlerMock.Object, productReaderMock.Object, stockReaderMock.Object); }
        catch (OperationCanceledException) { }

        VerifyLogDoesNotContain("SuperSecretPass");
    }
}
