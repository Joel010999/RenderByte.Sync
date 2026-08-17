using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using Xunit;

namespace RenderByte.Sync.Tests;

/// <summary>
/// Tests para <see cref="MovementCanonicalizer"/>.
/// Validan el algoritmo de canonicalización longitud-prefijada y la generación de movement_key.
/// </summary>
public sealed class MovementCanonicalizerTests
{
    // ─── A. Mismo movimiento en ejecuciones distintas → mismo hash ───────────────

    [Fact]
    public void MovementKey_SameMovement_AcrossExecutions_IsIdentical()
    {
        var mov = MakeMovement(new DateTime(2026, 8, 14, 17, 5, 49, 523), "CL0001", 3);

        var key1 = mov.GetMovementKey("STORE-A");
        var key2 = mov.GetMovementKey("STORE-A");

        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length); // SHA-256 → 64 hex chars
    }

    // ─── B. Ambigüedad de delimitador: imposible con longitud-prefijada ─────────

    [Fact]
    public void Canonicalization_DelimiterLikeCharacters_CannotCreateAmbiguity()
    {
        // "A|B" como primer campo + "C" como segundo campo
        var canonical1 = MovementCanonicalizer.BuildCanonicalBytes(["A|B", "C"]);

        // "A" como primer campo + "B|C" como segundo campo
        var canonical2 = MovementCanonicalizer.BuildCanonicalBytes(["A", "B|C"]);

        // Con codificación longitud-prefijada, los byte arrays son distintos
        Assert.False(canonical1.SequenceEqual(canonical2),
            "Campos con valores que contienen el delimitador no deben producir el mismo canonical.");
    }

    [Fact]
    public void Canonicalization_EmptyFieldVsNoField_AreDistinct()
    {
        // Campo vacío en pos 0 + "AB" en pos 1
        var c1 = MovementCanonicalizer.BuildCanonicalBytes(["", "AB"]);

        // "AB" en pos 0 (sin segundo campo)
        var c2 = MovementCanonicalizer.BuildCanonicalBytes(["AB"]);

        Assert.False(c1.SequenceEqual(c2));
    }

    // ─── C. source_id cambia el movement_key ────────────────────────────────────

    [Fact]
    public void SourceId_ChangesMovementKey()
    {
        var mov = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "CL0001", 1);

        var keyA = mov.GetMovementKey("CLIENTE-A");
        var keyB = mov.GetMovementKey("CLIENTE-B");

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(64, keyA.Length);
        Assert.Equal(64, keyB.Length);
    }

    // ─── Validar formato lowercase ────────────────────────────────────────────────

    [Fact]
    public void MovementKey_IsLowercaseHex()
    {
        var mov = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "CL0001", 1);
        var key = mov.GetMovementKey("SRC");

        // Debe ser 64 chars hexadecimales en lowercase
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    // ─── Normalización de fecha: SpecifyKind(Unspecified) es idempotente en el hash ─

    [Fact]
    public void MovementKey_FechaKind_DoesNotChangeHash()
    {
        // Dos movimientos con el MISMO Fecha value pero Kind diferente.
        // El canonicalizador normaliza ambos a Unspecified antes de serializar,
        // por lo que el hash debe ser idéntico.
        var baseDate = new DateTime(2026, 8, 14, 10, 0, 0);

        var movBase = MakeMovement(baseDate, "CL0001", 1);
        // Forzar Fecha con Kind=Local y Kind=Utc (mismo valor de ticks)
        var movLocal  = movBase with { Fecha = DateTime.SpecifyKind(baseDate, DateTimeKind.Local) };
        var movUtc    = movBase with { Fecha = DateTime.SpecifyKind(baseDate, DateTimeKind.Utc) };

        // El mov base tiene Fecha = baseDate.Date (midnight) por MakeMovement.
        // Reemplazamos Fecha en todos para que sea el mismo valor absoluto:
        var movRef       = movBase with { Fecha = DateTime.SpecifyKind(baseDate, DateTimeKind.Unspecified) };
        var movLocalSame = movBase with { Fecha = DateTime.SpecifyKind(baseDate, DateTimeKind.Local) };

        var k1 = movRef.GetMovementKey("SRC");
        var k2 = movLocalSame.GetMovementKey("SRC");

        // Mismos ticks, diferente Kind → canonicalizador normaliza a Unspecified → mismo hash
        Assert.Equal(k1, k2);
    }

    // ─── Formato canónico de fecha ────────────────────────────────────────────────

    [Fact]
    public void AlegonDateFormat_SerializesWithSevenDecimalPlaces()
    {
        var date    = new DateTime(2026, 8, 14, 17, 30, 45, 999, DateTimeKind.Unspecified);
        var dateStr = date.ToString(MovementCanonicalizer.AlegonDateFormat,
            System.Globalization.CultureInfo.InvariantCulture);

        // 7 decimales, sin sufijo de zona
        Assert.Equal("2026-08-14T17:30:45.9990000", dateStr);
    }

    // ─── Factory ─────────────────────────────────────────────────────────────────

    private static AlegonMovement MakeMovement(DateTime fedepo, string claveU, int item) =>
        new(
            Depo:              2,
            TipoMovimiento:    "VT",
            Fecha:             fedepo.Date,
            CodigoComprobante: "TEST",
            PuntoVenta:        "0001",
            Numero:            "00000001",
            Proveedor:         "PROV",
            ArticleId:         "ARTX",
            Bulto:             "U",
            Local:             2,
            Item:              item,
            FechaDeposito:     fedepo,
            Oferta:            null,
            Cantidad:          1m,
            Saldo:             0m,
            Costo:             0m,
            Precio:            0m,
            ClaveU:            claveU,
            Piezas:            null
        );
}
