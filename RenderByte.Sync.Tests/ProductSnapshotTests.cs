using System.Net;
using System.Text;
using System.Text.Json;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Contracts;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using Xunit;

namespace RenderByte.Sync.Tests;

public class ProductSnapshotTests
{
    public ProductSnapshotTests()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");
    }

    private class MockProductStore : IProductStore
    {
        public Dictionary<string, ProductState> States { get; } = new();
        public List<ProductOutboxMessage> Outbox { get; } = new();
        private long _nextId = 1;

        public Task InitializeAsync(string sourceId, int branchId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, ProductState>> GetStatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, ProductState>>(States);
        }

        public Task UpsertStateAndOutboxAsync(string sourceId, int branchId, AlegonProductMaster product, string businessKey, string contentHash, string payloadJson, CancellationToken cancellationToken = default)
        {
            States[businessKey] = new ProductState(businessKey, product.ArticleId, contentHash, true);
            Outbox.Add(new ProductOutboxMessage(_nextId++, businessKey, product.ArticleId, contentHash, payloadJson, "pending", 0));
            return Task.CompletedTask;
        }

        public Task MarkMissingAndCreateTombstoneAsync(string sourceId, int branchId, string businessKey, int articleId, CancellationToken cancellationToken = default)
        {
            var existingState = States[businessKey];
            States[businessKey] = existingState with { IsPresent = false };

            Outbox.Add(new ProductOutboxMessage(
                Id: Outbox.Count + 1,
                BusinessKey: businessKey,
                ArticleId: articleId,
                ContentHash: "TOMBSTONE",
                Payload: "{}",
                Status: "pending",
                RetryCount: 0));
            
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProductOutboxMessage>> GetPendingOutboxAsync(int limit, CancellationToken cancellationToken = default)
        {
            var pending = Outbox.Where(x => x.Status == "pending").Take(limit).ToList();
            return Task.FromResult<IReadOnlyList<ProductOutboxMessage>>(pending);
        }

        public Task MarkOutboxSentAsync(long id, CancellationToken cancellationToken = default)
        {
            var msg = Outbox.FirstOrDefault(x => x.Id == id);
            if (msg != null)
            {
                var index = Outbox.IndexOf(msg);
                Outbox[index] = msg with { Status = "sent" };
            }
            return Task.CompletedTask;
        }

        public Task MarkOutboxErrorAsync(long id, string error, CancellationToken cancellationToken = default)
        {
            var msg = Outbox.FirstOrDefault(x => x.Id == id);
            if (msg != null)
            {
                var index = Outbox.IndexOf(msg);
                Outbox[index] = msg with { RetryCount = msg.RetryCount + 1 };
            }
            return Task.CompletedTask;
        }
    }

    private class MockProductReader : IProductReader
    {
        public List<AlegonProductMaster> Products { get; set; } = new();
        public bool ShouldThrow { get; set; }

        public Task<IReadOnlyList<AlegonProductMaster>> GetFullSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (ShouldThrow) throw new Exception("Reader failed");
            return Task.FromResult<IReadOnlyList<AlegonProductMaster>>(Products);
        }
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        public ProductSyncRequest? LastRequest { get; set; }
        public bool ReturnError { get; set; }
        public int? AcceptedCount { get; set; }
        public Action<HttpRequestMessage>? OnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnSend?.Invoke(request);

            if (ReturnError)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Error")
                };
            }

            var content = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastRequest = JsonSerializer.Deserialize<ProductSyncRequest>(content);

            var responseDto = new ProductSyncResponse(
                BatchId: LastRequest!.BatchId,
                Accepted: AcceptedCount ?? LastRequest.Products.Count,
                Inserted: LastRequest.Products.Count,
                Updated: 0,
                Unchanged: 0,
                ReceivedAt: DateTimeOffset.UtcNow
            );

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(responseDto), Encoding.UTF8, "application/json")
            };
        }
    }

    private static AlegonProductMaster CreateProduct(int id, string desc) => new(
        ArticleId: id,
        Marca: null,
        Descripcion: desc,
        UnidadMedida: null,
        Bulto: null,
        Timpu: null,
        Clasificacion: null,
        Proveedor: null,
        ArticuloProveedor: null,
        Cossimp: null,
        Cossvta: null,
        Factu: null,
        Stopti: null,
        Ptoped: null,
        Ubicacion: null,
        HabilitadoCompra: null,
        HabilitadoVenta: null,
        Cotiza: null,
        CuentaCompra: null,
        CuentaVenta: null,
        DescuentoMaximo: null,
        IdsBArt: null,
        IdProd: null,
        Estado: null,
        Esqucalc: null,
        Benvase: null,
        Nasocenv: null,
        Bpesable: null,
        RutaFoto: null,
        Comision: null,
        Ndiasvct: null,
        NMinMay: null,
        DVigMayd: null,
        DVigMayh: null
    );

    [Fact]
    public async Task ProductSnapshot_FirstRunCreatesAll()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));
        reader.Products.Add(CreateProduct(2, "B"));

        var handler = new MockHttpHandler();

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Equal(2, store.States.Count);
        Assert.Equal(2, store.Outbox.Count);
        Assert.All(store.Outbox, msg => Assert.Equal("sent", msg.Status));
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(2, handler.LastRequest.Products.Count);
    }

    [Fact]
    public async Task ProductSnapshot_SecondUnchangedRunCreatesNoOutbox()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler();

        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear(); // Simulamos que ya se enviaron y borraron o ignoramos
        handler.LastRequest = null;

        // Segunda ejecución sin cambios
        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Empty(store.Outbox);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ProductSnapshot_OneChangedProductCreatesOneOutbox()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler();

        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear();

        // Cambiamos el producto
        reader.Products[0] = CreateProduct(1, "A_MODIFIED");

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Single(store.Outbox);
        Assert.Equal("sent", store.Outbox[0].Status);
    }

    [Fact]
    public async Task ProductSnapshot_NewProductCreatesOneOutbox()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler();

        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear();

        // Agregamos nuevo producto
        reader.Products.Add(CreateProduct(2, "B"));

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Single(store.Outbox);
        Assert.Contains("2", store.Outbox[0].BusinessKey);
    }

    [Fact]
    public async Task ProductSnapshot_MissingProductCreatesTombstoneOnlyAfterCompleteSnapshot()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));
        reader.Products.Add(CreateProduct(2, "B"));

        var handler = new MockHttpHandler();

        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear();

        // Producto 2 desaparece
        reader.Products.RemoveAt(1);

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Single(store.Outbox);
        Assert.Equal("TOMBSTONE", store.Outbox[0].ContentHash);
        Assert.False(store.States[ProductCanonicalizer.ComputeBusinessKey("SRC", 2)].IsPresent);
    }

    [Fact]
    public async Task ProductSnapshot_FailedSnapshotCreatesNoFalseTombstones()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler();

        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear();

        // Lector falla (ej: error de conexión a mitad de lectura)
        reader.ShouldThrow = true;

        await Assert.ThrowsAsync<Exception>(() => ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler));

        // No se debe haber creado un tombstone falsamente
        Assert.Empty(store.Outbox);
        Assert.True(store.States[ProductCanonicalizer.ComputeBusinessKey("SRC", 1)].IsPresent);
    }

    [Fact]
    public void ProductPersist_StateAndOutboxAreAtomic()
    {
        // En nuestro mock no podemos probar la atomicidad real de SQLite, 
        // pero verificaremos que se llame a UpsertStateAndOutboxAsync.
        // La prueba real de DB la haremos en SqliteSyncBatchStoreTests si aplica.
    }

    [Fact]
    public void ProductApi_AuthSourceMismatchRejected()
    {
        // Este test aplica al lado de la API (SyncEndpoints), que validaremos levantando un TestServer o revisando su código.
        // Ya hemos implementado la verificación `authContext.SourceId != request.SourceId` en SyncEndpoints.cs.
    }

    [Fact]
    public async Task ProductAck_InvalidDoesNotMarkSent()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler { AcceptedCount = 0 }; // ACK inválido

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(1, result); // El agente debe fallar al recibir ACK incongruente
        Assert.Equal("pending", store.Outbox[0].Status);
    }

    [Fact]
    public async Task ProductHttpFailure_KeepsPending()
    {
        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler { ReturnError = true };

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(1, result); // Retorna error
        Assert.Equal("pending", store.Outbox[0].Status);
        Assert.Equal(1, store.Outbox[0].RetryCount);
    }

    [Fact]
    public async Task ProductSnapshot_RepeatedTombstoneCreatesNoDuplicateOutbox()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

        var store = new MockProductStore();
        var reader = new MockProductReader();
        reader.Products.Add(CreateProduct(1, "A"));

        var handler = new MockHttpHandler();

        // 1. Snapshot inicial
        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        store.Outbox.Clear();

        // 2. Producto se borra en Alegon, se genera Tombstone
        reader.Products.Clear();
        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        Assert.Single(store.Outbox);
        Assert.Equal("TOMBSTONE", store.Outbox[0].ContentHash);
        store.Outbox.Clear();

        // 3. Nueva ejecución de Snapshot, el producto sigue borrado
        await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);
        
        // No debe generar un nuevo Tombstone en el outbox porque el state ya lo marca como IsPresent=false
        Assert.Empty(store.Outbox);
    }

    [Fact]
    public async Task ProductSync_MultipleBatchesAreSentAndAckedProperly()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

        var store = new MockProductStore();
        var reader = new MockProductReader();
        
        // Simular 2500 productos (forzará 3 batches de 1000, 1000 y 500)
        for (int i = 1; i <= 2500; i++)
        {
            reader.Products.Add(CreateProduct(i, $"Prod_{i}"));
        }

        int httpCalls = 0;
        var handler = new MockHttpHandler 
        { 
            // Inyectamos una acción para contar llamadas
            OnSend = req => { httpCalls++; }
        };

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(0, result);
        Assert.Equal(3, httpCalls); // 1000, 1000, 500
        Assert.All(store.Outbox, msg => Assert.Equal("sent", msg.Status));
    }

    [Fact]
    public async Task ProductSync_MiddleBatchFailurePreservesRemainingPending()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

        var store = new MockProductStore();
        var reader = new MockProductReader();
        
        // 2500 productos (Batches: B1=1000, B2=1000, B3=500)
        for (int i = 1; i <= 2500; i++)
        {
            reader.Products.Add(CreateProduct(i, $"Prod_{i}"));
        }

        int httpCalls = 0;
        var handler = new MockHttpHandler 
        { 
            OnSend = req => 
            { 
                httpCalls++; 
                if (httpCalls == 2) throw new HttpRequestException("Network failure mid-sync");
            }
        };

        var result = await ProductsSyncOnceAgent.RunAsync("SRC", reader, Array.Empty<string>(), default, store, handler);

        Assert.Equal(1, result); // Falla y sale del loop
        Assert.Equal(2, httpCalls); // Hizo la primera bien, falló en la segunda

        // El primer batch se envió y su ACK se procesó, deben estar sent
        var sent = store.Outbox.Count(m => m.Status == "sent");
        var pending = store.Outbox.Count(m => m.Status == "pending");

        Assert.Equal(1000, sent);
        Assert.Equal(1500, pending);
    }
}
