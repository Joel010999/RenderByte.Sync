using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Core.Alegon;
using System.Collections.Generic;
using RenderByte.Sync.Core.Alegon.Models;
using Moq;
using System.Net.Http;
using System.Net;
using RenderByte.Sync.Persistence;
using System.IO;

namespace RenderByte.Sync.Tests;

[Collection("Sequential")]
public class UnifiedRunTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _sourceId;
    private readonly SyncAgentOptions _options;

    public UnifiedRunTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(SyncDbPath.EnvVar, _dbPath);
        _sourceId = Guid.NewGuid().ToString().ToLowerInvariant();
        
        _options = new SyncAgentOptions(
            AlegonConnectionString: "Server=.;Integrated Security=true",
            SourceId: _sourceId,
            ApiUrl: "http://localhost",
            ApiKey: "test-key",
            ReadBatchSize: 10,
            UploadBatchSize: 10,
            PollSeconds: 1
        );

        DateTimeOffset fakeTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        ContinuousRunAgent.DelayTask = async (t, ct) => {
            fakeTime += t;
            await Task.Delay(1, ct); 
        };
        ContinuousRunAgent.GetUtcNow = () => fakeTime; 
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SyncDbPath.EnvVar, null);
        ContinuousRunAgent.DelayTask = Task.Delay;
        ContinuousRunAgent.GetUtcNow = () => DateTimeOffset.UtcNow;
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = 
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    private AlegonMovement MakeMovement(string numero, int minAdd = 0) => new(
        Depo: 1, TipoMovimiento: "P", Fecha: DateTime.Parse("2024-01-01T10:00:00.0000000").AddMinutes(minAdd),
        CodigoComprobante: "A", PuntoVenta: "0001", Numero: numero,
        Proveedor: "123", ArticleId: "ART1", Bulto: "0", Local: 1, Item: 1,
        FechaDeposito: DateTime.Parse("2024-01-01T10:00:00.0000000").AddMinutes(minAdd), Oferta: null, Cantidad: 1m, Saldo: 0m,
        Costo: 100m, Precio: 200m, ClaveU: "X", Piezas: null);

    [Fact]
    public async Task UnifiedRun_StartsAllThreePipelinesImmediately()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        bool movCalled = false, stkCalled = false, prdCalled = false;
        
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { movCalled = true; return new List<AlegonMovement>(); });

        var stockMock = new Mock<IStockReader>();
        stockMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { stkCalled = true; return new List<AlegonStock>(); });

        var productMock = new Mock<IProductReader>();
        productMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { prdCalled = true; return new List<AlegonProductMaster>(); });

        var mockHttp = new MockHttpMessageHandler();
        using var cts = new CancellationTokenSource();
        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productMock.Object, stockMock.Object);
        
        await Task.Delay(200);
        cts.Cancel();
        await runTask;
        
        Assert.True(movCalled);
        Assert.True(stkCalled);
        Assert.True(prdCalled);
    }

    [Fact]
    public void UnifiedRun_IntervalsConfigurable()
    {
        var options = new SyncAgentOptions(
            AlegonConnectionString: "A",
            SourceId: "B",
            ApiUrl: "C",
            ApiKey: "D",
            ReadBatchSize: 10,
            UploadBatchSize: 10,
            PollSeconds: 1
        );
        
        Assert.Equal(60, options.MovementIntervalSeconds);
        Assert.Equal(300, options.StockIntervalSeconds);
        Assert.Equal(3600, options.ProductIntervalSeconds);
    }

    [Fact]
    public async Task UnifiedRun_WhenAllDue_OrderIsMovementsStockProducts()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        var order = new List<string>();
        
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("MOV"); return new List<AlegonMovement>(); });

        var stockMock = new Mock<IStockReader>();
        stockMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("STK"); return new List<AlegonStock>(); });

        var productMock = new Mock<IProductReader>();
        productMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("PRD"); return new List<AlegonProductMaster>(); });

        var mockHttp = new MockHttpMessageHandler();
        using var cts = new CancellationTokenSource();
        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productMock.Object, stockMock.Object);
        
        await Task.Delay(200);
        cts.Cancel();
        await runTask;
        
        Assert.Equal("MOV", order[0]);
        Assert.Equal("STK", order[1]);
        Assert.Equal("PRD", order[2]);
    }

    [Fact]
    public async Task UnifiedRun_ProductFailure_DoesNotBlockMovements()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        int movCount = 0;
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { movCount++; return new List<AlegonMovement>(); });

        var stockMock = new Mock<IStockReader>();
        stockMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<AlegonStock>());

        var productMock = new Mock<IProductReader>();
        productMock.Setup(r => r.GetFullSnapshotAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Product network fail")); // FAILURE!

        var mockHttp = new MockHttpMessageHandler();
        using var cts = new CancellationTokenSource();
        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productMock.Object, stockMock.Object);
        
        await Task.Delay(500);
        cts.Cancel();
        await runTask;
        
        Assert.True(movCount > 1, "Movements should keep polling despite product failures");
    }
}
