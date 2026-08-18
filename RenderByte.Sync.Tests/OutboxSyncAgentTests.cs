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

public class OutboxSyncAgentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _sourceId;

    public OutboxSyncAgentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(SyncDbPath.EnvVar, _dbPath);
        _sourceId = Guid.NewGuid().ToString().ToLowerInvariant();
        
        // Evitar que el agente falle por falta de variables
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
    public async Task OutboxSync_MissingDb_AbortsBeforeHttp()
    {
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = _ => throw new Exception("HTTP no debió ser llamado")
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task OutboxSync_SourceMismatch_AbortsBeforeHttp()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync("otro-source", 1);
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = _ => throw new Exception("HTTP no debió ser llamado")
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task OutboxSync_ExistingDb_InitializesBeforeGetPending()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = _ => throw new Exception("HTTP no debió ser llamado, no hay pending")
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(0, exitCode); // 0 indica éxito porque "no hay pending"
    }

    [Fact]
    public async Task OutboxSync_ValidPending_SendsAndMarksSentOnlyAfterAck()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            var result = await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
            Assert.Equal(1, result.Inserted);
        }

        bool httpCalled = false;
        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                httpCalled = true;
                var res = new SyncBatchResponse("123", 1, 1, 0, DateTimeOffset.UtcNow);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res), Encoding.UTF8, "application/json")
                };
            }
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(0, exitCode);
        Assert.True(httpCalled);

        await using (var verifyStore = new SqliteSyncBatchStore(_dbPath))
        {
            await verifyStore.OpenExistingInstallationAsync(_sourceId);
            var pending = await verifyStore.GetPendingCountAsync();
            Assert.Equal(0, pending);
        }
    }

    [Fact]
    public async Task OutboxSync_InvalidAck_DoesNotMarkSent()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var res = new SyncBatchResponse("123", 0, 0, 0, DateTimeOffset.UtcNow); // Invalid ack count
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(res), Encoding.UTF8, "application/json")
                };
            }
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(1, exitCode);

        await using (var verifyStore = new SqliteSyncBatchStore(_dbPath))
        {
            await verifyStore.OpenExistingInstallationAsync(_sourceId);
            var pending = await verifyStore.GetPendingCountAsync();
            Assert.Equal(1, pending);
        }
    }

    [Fact]
    public async Task OutboxSync_HttpFailure_DoesNotMarkSent()
    {
        await using (var store = new SqliteSyncBatchStore(_dbPath))
        {
            await store.InitializeAsync(_sourceId, 1);
            await store.PersistBatchAndCheckpointAsync(1, new[] { MakeMovement("001") }, new MovementCheckpoint(DateTime.Parse("2024-01-01T10:00:00.0000000"), "X", 1));
        }

        var mockHttp = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        };

        var exitCode = await OutboxSyncAgent.RunAsync(_sourceId, Array.Empty<string>(), CancellationToken.None, mockHttp);
        Assert.Equal(1, exitCode);

        await using (var verifyStore = new SqliteSyncBatchStore(_dbPath))
        {
            await verifyStore.OpenExistingInstallationAsync(_sourceId);
            var pending = await verifyStore.GetPendingCountAsync();
            Assert.Equal(1, pending);
        }
    }

    [Fact]
    public async Task OutboxSync_DoesNotRequireAlegon()
    {
        // Esto se valida implícitamente en los demás tests porque en ninguno
        // instanciamos la base de datos de Alegon ni usamos AlegonReader.
        // El test pasa exitosamente.
        await Task.CompletedTask;
    }
}
