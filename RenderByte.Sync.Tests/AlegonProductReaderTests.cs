using System.IO;
using Xunit;

namespace RenderByte.Sync.Tests;

public class AlegonProductReaderTests
{
    private static readonly string SourceFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "..", "..", "..", "..",
        "RenderByte.Sync.Infrastructure", "Alegon", "AlegonProductReader.cs");

    [Fact]
    public void ProductReader_UsesExplicitColumns()
    {
        var source = File.ReadAllText(SourceFilePath).ToUpperInvariant();

        Assert.DoesNotContain("SELECT *", source);
        Assert.Contains("SELECT", source);
        Assert.Contains("ARTICULO,", source);
        Assert.Contains("MARCA,", source);
        Assert.Contains("DVIGMAYH", source); // última columna
        Assert.Contains("FROM DBO.ARTICULO", source);
    }

    [Fact]
    public void ProductReader_IsSelectOnly()
    {
        var source = File.ReadAllText(SourceFilePath).ToUpperInvariant();

        Assert.DoesNotContain("UPDATE ", source);
        Assert.DoesNotContain("INSERT ", source);
        Assert.DoesNotContain("DELETE ", source);
        Assert.DoesNotContain("EXEC ", source);
    }

    [Fact]
    public void ProductReader_TrimsCharPadding()
    {
        var source = File.ReadAllText(SourceFilePath);
        Assert.Contains("GetString(i).TrimEnd()", source);
    }
}
