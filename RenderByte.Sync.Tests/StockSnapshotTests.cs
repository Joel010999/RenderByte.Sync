using RenderByte.Sync.Agent;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using System.Text.Json;
using Xunit;
using System.Net;

namespace RenderByte.Sync.Tests;

public class StockSnapshotTests
{
    private class FakeStockReader : IStockReader
    {
        public List<AlegonStock> StocksToReturn { get; set; } = new();

        public Task<IReadOnlyList<AlegonStock>> GetFullSnapshotAsync(int branchId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AlegonStock>>(StocksToReturn);
        }
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    [Fact]
    public async Task RunAsync_ShouldInsertNewStocksToOutbox()
    {
        var sourceId = "TEST_SRC";
        var reader = new FakeStockReader();
        reader.StocksToReturn.Add(new AlegonStock(1, 10, "UN", 10.5m, 20m, 5m, null));

        var dbPath = Path.GetTempFileName();
        try
        {
            await using var store = new SqliteSyncBatchStore(dbPath);
            await store.InitializeAsync(sourceId, 1, CancellationToken.None);

            var conn = (Microsoft.Data.Sqlite.SqliteConnection)store.GetType().GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at) VALUES (1, 1, '2024-01-01T00:00:00.0000000', 'DUMMY', 1, '2024-01-01T00:00:00.0000000Z')";
            await cmd.ExecuteNonQueryAsync();

            Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
            Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");
            
            var res = await StocksSyncOnceAgent.RunAsync(sourceId, reader, new string[0], CancellationToken.None, store, new MockHttpHandler());
            Assert.Equal(1, res); // 1 because HTTP 500

            var pending = await store.GetPendingStockOutboxAsync(100);
            Assert.Single(pending);
            Assert.Equal(10, pending[0].ArticleId);
            Assert.True(pending[0].IsPresent);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_ShouldTombstoneMissingStocks()
    {
        var sourceId = "TEST_SRC";
        var reader = new FakeStockReader();
        
        var dbPath = Path.GetTempFileName();
        try
        {
            await using var store = new SqliteSyncBatchStore(dbPath);
            await store.InitializeAsync(sourceId, 1, CancellationToken.None);

            var conn = (Microsoft.Data.Sqlite.SqliteConnection)store.GetType().GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at) VALUES (1, 1, '2024-01-01T00:00:00.0000000', 'DUMMY', 1, '2024-01-01T00:00:00.0000000Z')";
            await cmd.ExecuteNonQueryAsync();

            Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_URL", "http://localhost");
            Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_API_KEY", "test");

            var existingStock = new AlegonStock(1, 10, "UN", 10.5m, 20m, 5m, null);
            var bizKey = StockCanonicalizer.ComputeBusinessKey(sourceId, 1, 10, "UN");
            var hash = StockCanonicalizer.ComputeContentHash(existingStock, isPresent: true);
            await store.UpsertStockStateAndOutboxAsync(sourceId, 1, existingStock, bizKey, hash, JsonSerializer.Serialize(existingStock));
            
            var pending1 = await store.GetPendingStockOutboxAsync(100);
            foreach (var p in pending1) await store.MarkStockOutboxSentAsync(p.Id);

            // Now reader returns empty. Run agent.
            var res = await StocksSyncOnceAgent.RunAsync(sourceId, reader, new string[0], CancellationToken.None, store, new MockHttpHandler());
            Assert.Equal(1, res);

            var pending2 = await store.GetPendingStockOutboxAsync(100);
            Assert.Single(pending2);
            Assert.Equal(10, pending2[0].ArticleId);
            Assert.False(pending2[0].IsPresent); // Tombstone
            Assert.Equal("TOMBSTONE", pending2[0].ContentHash);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }
}
