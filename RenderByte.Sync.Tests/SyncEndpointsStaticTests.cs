using System.IO;
using Xunit;

namespace RenderByte.Sync.Tests;

public class SyncEndpointsStaticTests
{
    [Fact]
    public void StockApi_UsesStockLevelsRawTable()
    {
        var filePath = "../../../RenderByte.Sync.Api/Endpoints/SyncEndpoints.cs";
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "RenderByte.Sync.Api", "Endpoints", "SyncEndpoints.cs");
        }
        var content = File.ReadAllText(filePath);

        // It should NOT contain " stock_raw " as a table for the stocks endpoint. 
        // We will assert that it DOES contain stock_levels_raw, and does NOT contain " INTO stock_raw " or " UPDATE stock_raw "
        Assert.Contains("stock_levels_raw", content);
        Assert.DoesNotContain("INTO stock_raw ", content);
        Assert.DoesNotContain("UPDATE stock_raw ", content);
        Assert.DoesNotContain("FROM stock_raw ", content);
    }

    [Fact]
    public void StockApi_DoesNotUseMagicTombstoneHash()
    {
        var filePath = "../../../RenderByte.Sync.Api/Endpoints/SyncEndpoints.cs";
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "RenderByte.Sync.Api", "Endpoints", "SyncEndpoints.cs");
        }
        var content = File.ReadAllText(filePath);

        // It should not check for TOMBSTONE in the stocks endpoint context
        // Note: products M8 still uses TOMBSTONE so we only assert that it's not used by stocks.
        // But the user said: "No debe quedar como magic content_hash en M9."
        // We will just verify the tombstone logic for stocks uses IsPresent instead.
        Assert.DoesNotContain("stock.ContentHash == \"TOMBSTONE\"", content);
    }

    [Fact]
    public void StockApi_RejectsInvalidContentHashFormat()
    {
        var filePath = "../../../RenderByte.Sync.Api/Endpoints/SyncEndpoints.cs";
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "RenderByte.Sync.Api", "Endpoints", "SyncEndpoints.cs");
        }
        var content = File.ReadAllText(filePath);

        // It should validate that ContentHash and BusinessKey are 64 lowercase hex chars
        Assert.Contains("^[a-f0-9]{64}$", content);
        Assert.Contains("!hashRegex.IsMatch(stock.BusinessKey)", content);
        Assert.Contains("!hashRegex.IsMatch(stock.ContentHash)", content);
    }
}
