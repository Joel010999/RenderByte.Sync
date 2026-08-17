namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa el stock actual de un artículo en <c>dbo.artistock</c>.
/// PK: <c>Depo + ArticleId + Bulto</c>.
/// <c>Saldo</c> es el stock actual según Alegon.
/// Todas las columnas numéricas son NUMERIC en SQL Server — se mapean a <c>decimal</c>.
/// <c>Bulto</c> es CHAR(6) — se lee con <c>Trim()</c>.
/// <c>Piezas</c> es NUMERIC NULL en el esquema.
/// </summary>
public sealed record AlegonStock(
    int      Depo,
    int      ArticleId,  // columna: idarti (INT en artistock)
    string   Bulto,      // columna: bulto CHAR(6) — con Trim()
    decimal  Costo,      // columna: costo NUMERIC
    decimal  Precio,     // columna: precio NUMERIC
    decimal  Saldo,      // columna: saldo NUMERIC — stock actual
    decimal? Piezas      // columna: piezas NUMERIC NULL
);
