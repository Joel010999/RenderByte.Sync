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
    public void ProductReader_TrimsLeadingAndTrailingPadding()
    {
        var source = File.ReadAllText(SourceFilePath);
        Assert.DoesNotContain("GetString(i).TrimEnd()", source);
        Assert.Contains("GetString(i).Trim()", source);
    }

    [Fact]
    public void ProductReader_NullStringRemainsNull()
    {
        var source = File.ReadAllText(SourceFilePath);
        Assert.Contains("r.IsDBNull(i) ? null : r.GetString(i).Trim()", source);
    }

    [Fact]
    public void ProductReader_MapsAll34ColumnsWithRealSqlTypes()
    {
        // 1. Arrange: Crear un DataTable con exactamente los tipos SQL reportados
        var table = new System.Data.DataTable();
        table.Columns.Add("articulo", typeof(int));
        table.Columns.Add("marca", typeof(string));
        table.Columns.Add("descri", typeof(string));
        table.Columns.Add("unimed", typeof(string));
        table.Columns.Add("bulto", typeof(string));
        table.Columns.Add("timpu", typeof(string));
        table.Columns.Add("clasif", typeof(string));
        table.Columns.Add("provee", typeof(string));
        table.Columns.Add("artprov", typeof(string));
        table.Columns.Add("cossimp", typeof(decimal));
        table.Columns.Add("cossvta", typeof(decimal));
        table.Columns.Add("factu", typeof(DateTime));
        table.Columns.Add("stopti", typeof(decimal));
        table.Columns.Add("ptoped", typeof(decimal));
        table.Columns.Add("ubicacion", typeof(string));
        table.Columns.Add("habcpa", typeof(bool)); // bit
        table.Columns.Add("habvta", typeof(bool)); // bit
        table.Columns.Add("cotiza", typeof(string));
        table.Columns.Add("cuencpa", typeof(int));
        table.Columns.Add("cuenvta", typeof(int));
        table.Columns.Add("dcto_max", typeof(decimal));
        table.Columns.Add("idsbart", typeof(int));
        table.Columns.Add("idprod", typeof(int));
        table.Columns.Add("estado", typeof(byte)); // tinyint
        table.Columns.Add("esqucalc", typeof(string));
        table.Columns.Add("benvase", typeof(bool)); // bit
        table.Columns.Add("nasocenv", typeof(decimal)); // numeric
        table.Columns.Add("bpesable", typeof(bool)); // bit
        table.Columns.Add("cfoto", typeof(string));
        table.Columns.Add("comision", typeof(decimal));
        table.Columns.Add("ndiasvct", typeof(decimal));
        table.Columns.Add("nMinMay", typeof(decimal));
        table.Columns.Add("dVigMayd", typeof(DateTime));
        table.Columns.Add("dVigMayh", typeof(DateTime));

        // Insertar una fila con datos válidos
        table.Rows.Add(
            123, "Marca   ", "Desc ", "UN ", "1 ", "IVA", "A", "Prov", "ArtProv",
            10.5m, 12.5m, new DateTime(2023, 1, 1), 5m, 2m, "P1",
            true, false, "1.0",
            101, 102, 5.0m, 1001, 2002, (byte)1,
            "E1", true, 1.0m, false, "/foto.jpg",
            2.0m, 30m, 100m, new DateTime(2023, 1, 1), new DateTime(2024, 1, 1)
        );

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        // 2. Act: Mapear la fila usando el método interno
        var product = RenderByte.Sync.Infrastructure.Alegon.AlegonProductReader.MapRow(reader);

        // 3. Assert: Verificar que mapeó correctamente sin tirar InvalidCastException
        Assert.NotNull(product);
        Assert.Equal(123, product.ArticleId);
        Assert.Equal("Marca", product.Marca); // Trimeado
        Assert.Equal("Desc", product.Descripcion); // Trimeado
        Assert.Equal(10.5m, product.Cossimp);
        Assert.Equal(101, product.CuentaCompra);
        Assert.Equal(102, product.CuentaVenta);
        Assert.Equal(1001, product.IdsBArt);
        Assert.Equal(2002, product.IdProd);
        Assert.Equal((byte)1, product.Estado);
        Assert.True(product.Benvase);
        Assert.Equal(1.0m, product.Nasocenv);
        Assert.False(product.Bpesable);
        Assert.Equal(DateTimeKind.Unspecified, product.Factu!.Value.Kind);
    }
}
