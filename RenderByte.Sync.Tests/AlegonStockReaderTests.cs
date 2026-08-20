
using RenderByte.Sync.Infrastructure.Alegon;
using Xunit;

namespace RenderByte.Sync.Tests;

public class AlegonStockReaderTests
{
    // These tests require a real connection string for an Alegon database, so they will be skipped in CI if not provided.
    // They are integration tests for the reader logic.
    [Fact(Skip = "Integration test requires real DB")]
    public async Task GetFullSnapshotAsync_ShouldReturnStock()
    {
        var connectionString = "Server=localhost;Database=ALEGON_TEST;Trusted_Connection=True;";
        var reader = new AlegonStockReader(connectionString);

        var snapshot = await reader.GetFullSnapshotAsync(1, CancellationToken.None);

        Assert.NotNull(snapshot);
    }
}
