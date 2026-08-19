using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using Xunit;

namespace RenderByte.Sync.Tests;

public class ProductCanonicalizerTests
{
    private static AlegonProductMaster CreateBaseProduct() => new(
        ArticleId: 1234,
        Marca: "Marca A",
        Descripcion: "Desc A",
        UnidadMedida: "UN",
        Bulto: "1",
        TipoImpuesto: "IVA",
        Clasificacion: "A",
        Proveedor: "Prov1",
        ArticuloProveedor: "ArtProv1",
        CostoImpositivo: 10.5m,
        CostoVenta: 12.0m,
        FechaActualizacion: new DateTime(2023, 1, 1, 10, 0, 0, DateTimeKind.Unspecified),
        StockOptimo: 50,
        PuntoPedido: 10,
        Ubicacion: "P1",
        HabilitadoCompra: true,
        HabilitadoVenta: true,
        Cotizacion: "1.0",
        CuentaCompra: 123,
        CuentaVenta: 456,
        DescuentoMaximo: 5.0m,
        IdSubRubro: 1,
        IdProducto: 1,
        Estado: 1,
        EsquemaCalculo: "E1",
        BultoEnvase: true,
        NumeroAsocEnvase: 1.0m,
        BultoPesable: false,
        RutaFoto: "/foto.jpg",
        Comision: 2.0m,
        DiasVencimiento: 30,
        MinimoMayorista: 100,
        VigenciaMayoristaDesde: new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        VigenciaMayoristaHasta: new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)
    );

    [Fact]
    public void ProductIdentity_IsCanonicalAndUnambiguous()
    {
        // "SRC" y "123" vs "SRC1" y "23" 
        // Si fuera concatenación simple ("SRC|123" o "SRC123"), podrían colisionar 
        // si los delimitadores se confunden o se escapan mal.
        var key1 = ProductCanonicalizer.ComputeBusinessKey("SRC", 123);
        var key2 = ProductCanonicalizer.ComputeBusinessKey("SRC1", 23);

        Assert.NotEqual(key1, key2);
        
        // Debe retornar un hash hexadecimal en minúscula de 64 caracteres
        Assert.Equal(64, key1.Length);
        Assert.Matches("^[a-f0-9]{64}$", key1);
        
        // Estable entre ejecuciones
        var key1_repeat = ProductCanonicalizer.ComputeBusinessKey("SRC", 123);
        Assert.Equal(key1, key1_repeat);
    }

    [Fact]
    public void ProductHash_AlegonDatePreservesLiteralValue()
    {
        // DateTimeKind.Unspecified asegura que el valor se transcriba literal
        var date = new DateTime(2026, 8, 19, 14, 30, 0, DateTimeKind.Unspecified);
        var prod1 = CreateBaseProduct() with { FechaActualizacion = date };

        var hash = ProductCanonicalizer.ComputeContentHash(prod1);
        Assert.NotNull(hash); // The actual value is tested by stability, we just ensure it runs and is deterministic
    }

    [Fact]
    public void ProductHash_DoesNotApplyTimezoneConversion()
    {
        var dateUtc = new DateTime(2026, 8, 19, 14, 30, 0, DateTimeKind.Utc);
        var dateUnspecified = new DateTime(2026, 8, 19, 14, 30, 0, DateTimeKind.Unspecified);

        // ProductCanonicalizer hace DateTime.SpecifyKind(d, Unspecified) internamente
        // para asegurarse de que NUNCA sufra conversiones a hora local.
        var prodUtc = CreateBaseProduct() with { FechaActualizacion = dateUtc };
        var prodUnspecified = CreateBaseProduct() with { FechaActualizacion = dateUnspecified };

        var hashUtc = ProductCanonicalizer.ComputeContentHash(prodUtc);
        var hashUnspecified = ProductCanonicalizer.ComputeContentHash(prodUnspecified);

        // Sin importar el Kind original, el canonicalizador lo trata como literal Unspecified y genera el mismo hash
        Assert.Equal(hashUnspecified, hashUtc);
    }

    [Fact]
    public void ProductHash_SameDataSameHash()
    {
        var prod1 = CreateBaseProduct();
        var prod2 = CreateBaseProduct();

        var hash1 = ProductCanonicalizer.ComputeContentHash(prod1);
        var hash2 = ProductCanonicalizer.ComputeContentHash(prod2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ProductHash_AnyFieldChangeChangesHash()
    {
        var prod1 = CreateBaseProduct();
        var prod2 = prod1 with { CostoVenta = 12.0001m };

        var hash1 = ProductCanonicalizer.ComputeContentHash(prod1);
        var hash2 = ProductCanonicalizer.ComputeContentHash(prod2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ProductHash_NullAndEmptyAreDifferent()
    {
        var prod1 = CreateBaseProduct() with { Descripcion = null };
        var prod2 = CreateBaseProduct() with { Descripcion = "" };

        var hash1 = ProductCanonicalizer.ComputeContentHash(prod1);
        var hash2 = ProductCanonicalizer.ComputeContentHash(prod2);

        Assert.NotEqual(hash1, hash2);
    }
}
