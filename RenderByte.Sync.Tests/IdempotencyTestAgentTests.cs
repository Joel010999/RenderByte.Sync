using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Tests;

[Collection("Sequential")]
public class IdempotencyTestAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _sourceId;

    public IdempotencyTestAgentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(SyncDbPath.EnvVar, _dbPath);
        _sourceId = Guid.NewGuid().ToString().ToLowerInvariant();
        
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test-key");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SyncDbPath.EnvVar, null);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private AlegonMovement MakeMovement(string numero) => new(
        Depo: 1, TipoMovimiento: "P", Fecha: DateTime.Parse("2024-01-01T10:00:00.0000000"),
        CodigoComprobante: "A", PuntoVenta: "0001", Numero: numero,
        Proveedor: "123", ArticleId: "ART1", Bulto: "0", Local: 1, Item: 1,
        FechaDeposito: DateTime.Parse("2024-01-01T10:00:00.0000000"), Oferta: null, Cantidad: 1m, Saldo: 0m,
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
    public async Task IdempotencyTest_MissingIdAbortsBeforeHttp()
    {
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = _ => throw new Exception("HTTP no debió ser llamado")
        };

        // No parameters
        var exitCode = await IdempotencyTestAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(1, exitCode);
        
        // Invalid ID
        exitCode = await IdempotencyTestAgent.RunAsync(_sourceId, new[] { "not-an-id" }, CancellationToken.None, mockHttp);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task IdempotencyTest_SentRow_CanBeRead()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            var result = await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
            
            var pending = await store.GetPendingAsync(10);
            var id = pending[0].Id;
            await store.MarkBatchAsSentAsync(new[] { id }, "batch-1"); // Row is now SENT
            
            var readMsg = await store.GetMessageByIdAsync(id);
            Assert.NotNull(readMsg);
            Assert.Equal("sent", readMsg.Status);
        }
    }

    [Fact]
    public async Task IdempotencyTest_ValidAckDuplicatesAccepted()
    {
        long targetId;
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
            var pending = await store.GetPendingAsync(10);
            targetId = pending[0].Id;
            await store.MarkBatchAsSentAsync(new[] { targetId }, "batch-1"); 
        }

        bool httpCalled = false;
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                httpCalled = true;
                // Simula respuesta de idempotencia (duplicates=1)
                var res = new SyncBatchResponse("123", 1, 0, 1, DateTimeOffset.UtcNow);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res), Encoding.UTF8, "application/json")
                };
            }
        };

        var exitCode = await IdempotencyTestAgent.RunAsync(_sourceId, new[] { targetId.ToString() }, CancellationToken.None, mockHttp);
        Assert.Equal(0, exitCode);
        Assert.True(httpCalled);
    }

    [Fact]
    public async Task IdempotencyTest_DoesNotChangeLocalStatus()
    {
        long targetId;
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
            var pending = await store.GetPendingAsync(10);
            targetId = pending[0].Id;
            await store.MarkBatchAsSentAsync(new[] { targetId }, "batch-1"); 
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var res = new SyncBatchResponse("123", 1, 0, 1, DateTimeOffset.UtcNow);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res), Encoding.UTF8, "application/json")
                };
            }
        };

        await IdempotencyTestAgent.RunAsync(_sourceId, new[] { targetId.ToString() }, CancellationToken.None, mockHttp);

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var row = await store.GetMessageByIdAsync(targetId);
            Assert.NotNull(row);
            Assert.Equal("sent", row.Status); // No change
            Assert.Equal(0, row.RetryCount); // No change
        }
    }

    [Fact]
    public async Task IdempotencyTest_DoesNotChangeCheckpoint()
    {
        long targetId;
        MovementCheckpoint originalCheckpoint;

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            var cp = new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, cp);
            
            var cpRow = await store.GetCheckpointAsync();
            originalCheckpoint = cpRow!.ToMovementCheckpoint();

            var pending = await store.GetPendingAsync(10);
            targetId = pending[0].Id;
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var res = new SyncBatchResponse("123", 1, 0, 1, DateTimeOffset.UtcNow);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res), Encoding.UTF8, "application/json")
                };
            }
        };

        await IdempotencyTestAgent.RunAsync(_sourceId, new[] { targetId.ToString() }, CancellationToken.None, mockHttp);

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var cpRow = await store.GetCheckpointAsync();
            Assert.Equal(originalCheckpoint.Fedepo, cpRow!.Fedepo);
            Assert.Equal(originalCheckpoint.ClaveU, cpRow.ClaveU);
            Assert.Equal(originalCheckpoint.Item, cpRow.Item);
        }
    }

    [Fact]
    public async Task IdempotencyTest_HttpFailureLeavesEverythingUntouched()
    {
        long targetId;
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
            var pending = await store.GetPendingAsync(10);
            targetId = pending[0].Id;
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        var exitCode = await IdempotencyTestAgent.RunAsync(_sourceId, new[] { targetId.ToString() }, CancellationToken.None, mockHttp);
        Assert.Equal(1, exitCode);

        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.OpenExistingInstallationAsync(_sourceId);
            var row = await store.GetMessageByIdAsync(targetId);
            Assert.NotNull(row);
            Assert.Equal("pending", row.Status); // Sigue pending, no falló la fila original
            Assert.Equal(0, row.RetryCount);     // No sumó retry count
        }
    }
}
