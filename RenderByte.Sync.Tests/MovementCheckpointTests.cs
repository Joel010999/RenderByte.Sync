using Xunit;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Tests;

public sealed class MovementCheckpointTests
{
    private static readonly DateTime SampleDate = new(2026, 8, 14, 17, 0, 0);

    // ─── Initial ─────────────────────────────────────────────────────────────

    [Fact]
    public void Initial_SetsCorrectFedepo()
    {
        var cp = MovementCheckpoint.Initial(SampleDate);
        Assert.Equal(SampleDate, cp.Fedepo);
    }

    [Fact]
    public void Initial_ClaveU_IsEmptyString()
    {
        var cp = MovementCheckpoint.Initial(SampleDate);
        Assert.Equal(string.Empty, cp.ClaveU);
    }

    [Fact]
    public void Initial_Item_IsShortMinValue()
    {
        var cp = MovementCheckpoint.Initial(SampleDate);
        // short.MinValue = -32768: menor que cualquier item real en Alegon.
        Assert.Equal(short.MinValue, cp.Item);
    }

    // ─── From ─────────────────────────────────────────────────────────────────

    [Fact]
    public void From_ValidMovement_MapsAllFields()
    {
        var movement = MakeMovement(fedepo: SampleDate, claveU: "ABC123", item: 5);
        var cp = MovementCheckpoint.From(movement);

        Assert.Equal(SampleDate,  cp.Fedepo);
        Assert.Equal("ABC123",    cp.ClaveU);
        Assert.Equal(5,           cp.Item);
    }

    [Fact]
    public void From_MovementWithNullFedepo_Throws()
    {
        var movement = MakeMovement(fedepo: null, claveU: "X", item: 1);
        var ex = Assert.Throws<ArgumentException>(() => MovementCheckpoint.From(movement));
        Assert.Contains("fedepo NULL", ex.Message);
    }

    [Fact]
    public void From_NullMovement_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MovementCheckpoint.From(null!));
    }

    // ─── Record equality (valor, no referencia) ───────────────────────────────

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new MovementCheckpoint(SampleDate, "CL001", 3);
        var b = new MovementCheckpoint(SampleDate, "CL001", 3);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentItem_NotEqual()
    {
        var a = new MovementCheckpoint(SampleDate, "CL001", 3);
        var b = new MovementCheckpoint(SampleDate, "CL001", 4);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentClaveU_NotEqual()
    {
        var a = new MovementCheckpoint(SampleDate, "CL001", 3);
        var b = new MovementCheckpoint(SampleDate, "CL002", 3);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentFedepo_NotEqual()
    {
        var a = new MovementCheckpoint(SampleDate, "CL001", 3);
        var b = new MovementCheckpoint(SampleDate.AddSeconds(1), "CL001", 3);
        Assert.NotEqual(a, b);
    }

    // ─── ToString ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ContainsAllComponents()
    {
        var cp  = new MovementCheckpoint(SampleDate, "ABC", 7);
        var str = cp.ToString();
        Assert.Contains("fedepo=",   str);
        Assert.Contains("CLAVEU=ABC", str);
        Assert.Contains("item=7",    str);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AlegonMovement MakeMovement(DateTime? fedepo, string claveU, int item) =>
        new(
            Depo:              2,
            TipoMovimiento:    "VT",
            Fecha:             new DateTime(2026, 8, 14),
            CodigoComprobante: "TEST",
            PuntoVenta:        "0001",
            Numero:            "00000001",
            Proveedor:         "PROV",
            ArticleId:         "ART001",
            Bulto:             "U",
            Local:             2,
            Item:              item,
            FechaDeposito:     fedepo,
            Oferta:            null,
            Cantidad:          1m,
            Saldo:             10m,
            Costo:             100m,
            Precio:            150m,
            ClaveU:            claveU,
            Piezas:            null
        );
}
