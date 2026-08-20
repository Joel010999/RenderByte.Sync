using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Moq;
using Xunit;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Tests;

[Collection("Sequential")]
public class ContinuousRunAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _sourceId;
    private readonly SyncAgentOptions _options;

    public ContinuousRunAgentTests()
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

    private AlegonMovement MakeMovement(string numero, int minAdd = 0) => new(
        Depo: 1, TipoMovimiento: "P", Fecha: DateTime.Parse("2024-01-01T10:00:00.0000000").AddMinutes(minAdd),
        CodigoComprobante: "A", PuntoVenta: "0001", Numero: numero,
        Proveedor: "123", ArticleId: "ART1", Bulto: "0", Local: 1, Item: 1,
        FechaDeposito: DateTime.Parse("2024-01-01T10:00:00.0000000").AddMinutes(minAdd), Oferta: null, Cantidad: 1m, Saldo: 0m,
        Costo: 100m, Precio: 200m, ClaveU: "X", Piezas: null);

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = 
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task Run_StartsFromPersistedCheckpoint()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        bool checkpointPassed = false;
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int branch, MovementCheckpoint cp, int limit, CancellationToken ct) =>
            {
                if (cp.Fedepo == DateTime.Parse("2024-01-01T10:00:00.0000000") && cp.ClaveU == "X" && cp.Item == 1)
                    checkpointPassed = true;
                return new List<AlegonMovement>(); // Return empty to idle
            });

        using var cts = new CancellationTokenSource();
        var mockHttp = new MockHttpMessageHandler();

        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        // Let it start and then cancel
        await Task.Delay(100);
        cts.Cancel();
        
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
        Assert.True(checkpointPassed);
    }

    [Fact]
    public async Task Run_SourceMismatch_Stops()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync("other-source", 1);
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var exitCode = await ContinuousRunAgent.RunAsync(_options, readerMock.Object, CancellationToken.None, null, productReaderMock.Object, stockReaderMock.Object);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Run_NoCheckpoint_Stops()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var exitCode = await ContinuousRunAgent.RunAsync(_options, readerMock.Object, CancellationToken.None, null, productReaderMock.Object, stockReaderMock.Object);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_CapturesAndSendsAndMarksSent()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        bool captured = false;
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int branch, MovementCheckpoint cp, int limit, CancellationToken ct) =>
            {
                if (!captured)
                {
                    captured = true;
                    return new List<AlegonMovement> { MakeMovement("001", 1), MakeMovement("002", 2) };
                }
                return new List<AlegonMovement>(); // Return empty to idle
            });

        bool sent = false;
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                if (body.Contains("001") && body.Contains("002")) sent = true;
                
                var res = new SyncBatchResponse("123", 3, 3, 0, DateTimeOffset.UtcNow);
                var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res, opts), Encoding.UTF8, "application/json")
                };
            }
        };

        using var cts = new CancellationTokenSource();
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(200);
        cts.Cancel();
        
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
        Assert.True(captured);
        Assert.True(sent);

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var pending = await store.GetPendingAsync(10);
            Assert.Empty(pending); // Marked sent
            var cp = await store.GetCheckpointAsync();
            Assert.Equal(DateTime.Parse("2024-01-01T10:02:00.0000000"), cp!.Fedepo);
        }
    }

    [Fact]
    public async Task Run_HttpTransientFailure_KeepsPending()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        bool captured = false;
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int branch, MovementCheckpoint cp, int limit, CancellationToken ct) =>
            {
                if (!captured)
                {
                    captured = true;
                    return new List<AlegonMovement> { MakeMovement("001", 1) };
                }
                return new List<AlegonMovement>(); 
            });

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };

        using var cts = new CancellationTokenSource();
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(200);
        cts.Cancel();
        
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
        
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var pending = await store.GetPendingAsync(10);
            Assert.Equal(2, pending.Count); // 1 initial + 1 captured
        }
    }

    [Fact]
    public async Task Run_AlegonTemporaryFailure_StillSendsPending()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Alegon Caído"));

        bool sent = false;
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                sent = true;
                var res = new SyncBatchResponse("123", 1, 1, 0, DateTimeOffset.UtcNow);
                var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res, opts), Encoding.UTF8, "application/json")
                };
            }
        };

        using var cts = new CancellationTokenSource();
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        await Task.Delay(200);
        cts.Cancel();
        
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
        Assert.True(sent);

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var pending = await store.GetPendingAsync(10);
            Assert.Empty(pending); // Transport succeeded despite Alegon failure
        }
    }

    [Fact]
    public async Task Run_BranchMismatch_Stops()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 2); // DB expects branch 2
            await store.PersistBatchAndCheckpointAsync(2, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1); // Alegon returns 1

        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var exitCode = await ContinuousRunAgent.RunAsync(_options, readerMock.Object, CancellationToken.None, null, productReaderMock.Object, stockReaderMock.Object);
        Assert.Equal(2, exitCode); // InvalidOperationException for mismatch
    }

    [Fact]
    public async Task Run_CtrlC_CancelsCleanly()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlegonMovement>());

        var mockHttp = new MockHttpMessageHandler();

        using var cts = new CancellationTokenSource();
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        cts.Cancel(); // Immediate cancel
        
        var exitCode = await runTask;
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_Scheduler_DoesNotMicroBusyWaitNearDeadline()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("INITIAL", 0) }, MovementCheckpoint.Initial(DateTime.Parse("2024-01-01")));
        }

        var readerMock = new Mock<IAlegonReader>();
        readerMock.Setup(r => r.GetBranchNumberAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        
        int callCount = 0;
        readerMock.Setup(r => r.GetMovementsAfterAsync(It.IsAny<int>(), It.IsAny<MovementCheckpoint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => 
            {
                callCount++;
                if (callCount == 1) return new List<AlegonMovement> { MakeMovement("001", 1) };
                return new List<AlegonMovement>();
            });

        var mockHttp = new MockHttpMessageHandler();

        var delaysRequested = new List<TimeSpan>();
        var fakeTime = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        ContinuousRunAgent.GetUtcNow = () => fakeTime;
        ContinuousRunAgent.DelayTask = async (t, ct) => 
        {
            if (t > TimeSpan.Zero) delaysRequested.Add(t);
            // Simulate waking up 10ms before the deadline
            fakeTime += (t - TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        };

        using var cts = new CancellationTokenSource();
        var productReaderMock = new Mock<IProductReader>();
        var stockReaderMock = new Mock<IStockReader>();

        var runTask = ContinuousRunAgent.RunAsync(_options, readerMock.Object, cts.Token, mockHttp, productReaderMock.Object, stockReaderMock.Object);
        
        while (delaysRequested.Count < 3) { await Task.Delay(50); }
        cts.Cancel();
        
        await runTask;

        foreach (var delay in delaysRequested)
        {
            Assert.True(delay >= TimeSpan.FromMilliseconds(50), $"Found micro-delay: {delay.TotalMilliseconds}ms");
        }
    }
}
