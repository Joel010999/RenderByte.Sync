using System.IO;
using Xunit;

namespace RenderByte.Sync.Tests;

public class ProductSchemaReaderTests
{
    private readonly string _sourceCode;

    public ProductSchemaReaderTests()
    {
        var path = Path.GetFullPath("..\\..\\..\\..\\RenderByte.Sync.Infrastructure\\Alegon\\ProductSchemaReader.cs", AppContext.BaseDirectory);
        if (!File.Exists(path))
        {
            path = "C:\\RenderByte\\RenderByte.Sync\\RenderByte.Sync.Infrastructure\\Alegon\\ProductSchemaReader.cs";
        }
        _sourceCode = File.ReadAllText(path).ToUpperInvariant();
    }

    [Fact]
    public void ProductSchemaReader_UsesSelectOnly()
    {
        Assert.Contains("SELECT ", _sourceCode);
    }

    [Fact]
    public void ProductDiscovery_DoesNotMutateDatabase()
    {
        Assert.DoesNotContain("UPDATE ", _sourceCode);
        Assert.DoesNotContain("DELETE ", _sourceCode);
        Assert.DoesNotContain("INSERT ", _sourceCode);
        Assert.DoesNotContain("TRUNCATE ", _sourceCode);
        Assert.DoesNotContain("DROP ", _sourceCode);
        Assert.DoesNotContain("ALTER ", _sourceCode);
        Assert.DoesNotContain("EXEC ", _sourceCode);
    }

    [Fact]
    public void ProductSchemaReader_IsSqlServer2008Compatible()
    {
        // SQL Server 2008 R2 does not support OFFSET FETCH, it uses TOP
        Assert.DoesNotContain("OFFSET ", _sourceCode);
        Assert.DoesNotContain("FETCH NEXT", _sourceCode);
    }

    [Fact]
    public void ProductSampleReader_LimitsRows()
    {
        Assert.Contains("SELECT TOP ", _sourceCode);
    }
}
