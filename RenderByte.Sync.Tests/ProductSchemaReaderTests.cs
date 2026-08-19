using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Core.Alegon;
using Xunit;

namespace RenderByte.Sync.Tests;

public class ProductSchemaReaderTests
{
    private readonly string _readerSourceCode;
    private readonly string _agentSourceCode;

    public ProductSchemaReaderTests()
    {
        string ResolveFile(string relative)
        {
            var path = Path.GetFullPath(relative, AppContext.BaseDirectory);
            if (!File.Exists(path))
                path = Path.Combine("C:\\RenderByte\\RenderByte.Sync", relative.Replace("..\\..\\..\\..\\", ""));
            return File.ReadAllText(path).ToUpperInvariant();
        }

        _readerSourceCode = ResolveFile(
            "..\\..\\..\\..\\RenderByte.Sync.Infrastructure\\Alegon\\ProductSchemaReader.cs");

        _agentSourceCode = ResolveFile(
            "..\\..\\..\\..\\RenderByte.Sync.Agent\\ProductSchemaTestAgent.cs");
    }

    // ── Existing tests (M8.0 — preserved) ────────────────────────────────────

    [Fact]
    public void ProductSchemaReader_UsesSelectOnly()
    {
        Assert.Contains("SELECT ", _readerSourceCode);
    }

    [Fact]
    public void ProductDiscovery_DoesNotMutateDatabase()
    {
        Assert.DoesNotContain("UPDATE ", _readerSourceCode);
        Assert.DoesNotContain("DELETE ", _readerSourceCode);
        Assert.DoesNotContain("INSERT ", _readerSourceCode);
        Assert.DoesNotContain("TRUNCATE ", _readerSourceCode);
        Assert.DoesNotContain("DROP ", _readerSourceCode);
        Assert.DoesNotContain("ALTER ", _readerSourceCode);
        Assert.DoesNotContain("EXEC ", _readerSourceCode);
    }

    [Fact]
    public void ProductSchemaReader_IsSqlServer2008Compatible()
    {
        // SQL Server 2008 R2 does not support OFFSET FETCH or TRY_CONVERT.
        Assert.DoesNotContain("OFFSET ", _readerSourceCode);
        Assert.DoesNotContain("FETCH NEXT", _readerSourceCode);
        Assert.DoesNotContain("TRY_CONVERT", _readerSourceCode);
    }

    [Fact]
    public void ProductSampleReader_LimitsRows()
    {
        Assert.Contains("SELECT TOP ", _readerSourceCode);
    }

    // ── M8.0.1 — Safe relation discovery tests ────────────────────────────────

    /// <summary>
    /// Ninguna query de discovery debe intentar convertir idarti (VARCHAR/CHAR) a INT.
    /// La causa del SqlException 245 original fue que el optimizador SQL 2008 invirtió
    /// la conversión incluso con CAST(articulo AS varchar) en el otro lado del JOIN.
    /// </summary>
    [Fact]
    public void ProductRelationDiscovery_DoesNotConvertArtistockIdToInt()
    {
        // These patterns represent the forbidden conversions that caused SqlException 245.
        Assert.DoesNotContain("CAST(IDARTI AS INT)", _readerSourceCode);
        Assert.DoesNotContain("CAST(S.IDARTI AS INT)", _readerSourceCode);
        Assert.DoesNotContain("CONVERT(INT, IDARTI)", _readerSourceCode);
        Assert.DoesNotContain("CONVERT(INT, S.IDARTI)", _readerSourceCode);
        // Also verify we don't accidentally use ISNUMERIC (returns 1 for "FA019376.00").
        Assert.DoesNotContain("ISNUMERIC(IDARTI)", _readerSourceCode);
    }

    /// <summary>
    /// Las comparaciones con idarti deben usar RTRIM() para manejar relleno de espacios,
    /// y deben estar del lado VARCHAR de la comparación — nunca convertidas a INT.
    /// Valores como "FA019376.00" deben poder procesarse sin excepción.
    /// </summary>
    [Fact]
    public void ProductRelationDiscovery_HandlesAlphanumericIdarti()
    {
        // The safe pattern is CONVERT(VARCHAR(20), a.articulo) = RTRIM(s.idarti)
        // — converting INT to VARCHAR, never the other way around.
        Assert.Contains("RTRIM(S.IDARTI)", _readerSourceCode);
        Assert.Contains("CONVERT(VARCHAR(20), A.ARTICULO)", _readerSourceCode);
        // The relation is evaluated with NOT EXISTS / EXISTS, not a direct JOIN column cast.
        Assert.Contains("NOT EXISTS", _readerSourceCode);
    }

    /// <summary>
    /// Si una sección del ProductSchemaTestAgent lanza una excepción, el comando
    /// no debe propagarla: debe capturarla, mostrar [WARN] y continuar.
    /// </summary>
    [Fact]
    public async Task ProductDiscovery_SectionFailure_DoesNotCrashWholeCommand()
    {
        // Arrange: a reader that throws on every method.
        var failingReader = new AlwaysFailingProductSchemaReader();

        // Act: RunAsync should complete without throwing.
        var exitCode = await ProductSchemaTestAgent.RunAsync(failingReader, CancellationToken.None);

        // Assert: command returns normally (exit code 0), no exception propagated.
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Los métodos de artistock deben usar SELECT ONLY y no mutar la base de datos.
    /// </summary>
    [Fact]
    public void ProductArtistockSchemaReader_UsesSelectOnly()
    {
        // Verify artistock-specific methods exist (by checking key SQL tokens).
        Assert.Contains("OBJECT_ID('DBO.ARTISTOCK')", _readerSourceCode);
        Assert.Contains("RTRIM(IDARTI)", _readerSourceCode);

        // Verify no mutation queries exist anywhere in the reader.
        Assert.DoesNotContain("UPDATE ", _readerSourceCode);
        Assert.DoesNotContain("INSERT ", _readerSourceCode);
        Assert.DoesNotContain("DELETE ", _readerSourceCode);
    }

    // ── Test double ───────────────────────────────────────────────────────────

    /// <summary>
    /// Test double that throws <see cref="InvalidOperationException"/> from every
    /// method, simulating a fully broken database connection for
    /// <see cref="ProductDiscovery_SectionFailure_DoesNotCrashWholeCommand"/>.
    /// </summary>
    private sealed class AlwaysFailingProductSchemaReader : IProductSchemaReader
    {
        private static Task<T> Fail<T>() =>
            Task.FromException<T>(new InvalidOperationException("Simulated section failure for test."));

        public Task<string> GetSchemaInfoAsync(CancellationToken _ = default)          => Fail<string>();
        public Task<long>   GetProductCountAsync(CancellationToken _ = default)        => Fail<long>();
        public Task<string> GetSampleProductsAsync(int limit, CancellationToken _ = default) => Fail<string>();
        public Task<string> GetDuplicatesInfoAsync(CancellationToken _ = default)      => Fail<string>();
        public Task<string> GetModificationDateInfoAsync(CancellationToken _ = default) => Fail<string>();
        public Task<string> GetCostPriceInfoAsync(CancellationToken _ = default)       => Fail<string>();
        public Task<string> GetSoftDeleteInfoAsync(CancellationToken _ = default)      => Fail<string>();
        public Task<string> GetArtistockSchemaAsync(CancellationToken _ = default)     => Fail<string>();
        public Task<string> GetArtistockSampleIdsAsync(int limit, CancellationToken _ = default) => Fail<string>();
        public Task<string> GetArtistockIdProfileAsync(CancellationToken _ = default)  => Fail<string>();
        public Task<string> GetArtistockRelationAsync(CancellationToken _ = default)   => Fail<string>();
    }
}
