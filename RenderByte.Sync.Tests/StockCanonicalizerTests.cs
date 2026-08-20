
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace RenderByte.Sync.Tests;

public class StockCanonicalizerTests
{
    [Fact]
    public void ComputeBusinessKey_ShouldBeDeterministic()
    {
        var key1 = StockCanonicalizer.ComputeBusinessKey("SRC-1", 10, 500, "CAJA");
        var key2 = StockCanonicalizer.ComputeBusinessKey("SRC-1", 10, 500, "CAJA");
        
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ComputeContentHash_ShouldBeDeterministicAndIgnoreNullsVsZeros()
    {
        var s1 = new AlegonStock(10, 500, "CAJA", 10.5m, 20.0m, 100m, null);

        var hash1 = StockCanonicalizer.ComputeContentHash(s1, true);
        var hash2 = StockCanonicalizer.ComputeContentHash(s1, true);

        Assert.Equal(hash1, hash2);

        var s2 = new AlegonStock(10, 500, "CAJA", 10.5m, 20.0m, 100m, 0m);

        var hash3 = StockCanonicalizer.ComputeContentHash(s2, true);

        Assert.NotEqual(hash1, hash3); // null is different from 0
    }

    [Fact]
    public void StockTombstone_UsesIsPresentFlag()
    {
        var s1 = new AlegonStock(10, 500, "CAJA", 10.5m, 20.0m, 100m, null);
        var hashPresent = StockCanonicalizer.ComputeContentHash(s1, true);
        var hashTombstone = StockCanonicalizer.ComputeContentHash(s1, false);

        Assert.NotEqual(hashPresent, hashTombstone);
    }

    [Fact]
    public void StockTombstone_ContentHashRemainsSha256()
    {
        var sTombstone = new AlegonStock(10, 500, "CAJA", null, null, null, null);
        var hashTombstone = StockCanonicalizer.ComputeContentHash(sTombstone, false);

        Assert.Matches("^[a-f0-9]{64}$", hashTombstone);
    }
}
