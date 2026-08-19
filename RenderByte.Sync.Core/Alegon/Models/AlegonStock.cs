namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa el stock actual de un artículo en <c>dbo.artistock</c>.
/// PK: <c>Depo + IdArti + Bulto</c>.
/// <c>Saldo</c> es el stock actual según Alegon.
/// Todas las columnas numéricas son NUMERIC en SQL Server — se mapean a <c>decimal</c>.
/// <c>Bulto</c> es CHAR — se lee con <c>Trim()</c>.
/// <c>IdArti</c> es CHAR/VARCHAR — se lee con <c>Trim()</c>.
///   Puede contener valores alfanuméricos como "FA019376.00".
///   NO asumir que es INT. La relación con dbo.articulo.articulo (INT) no está
///   confirmada y debe descubrirse mediante discovery seguro (M8.0.1).
/// <c>Piezas</c> es NUMERIC NULL en el esquema.
/// </summary>
public sealed record AlegonStock(
    int      Depo,
    string   IdArti,    // columna: idarti CHAR/VARCHAR — con Trim(). Puede ser alfanumérico.
    string   Bulto,     // columna: bulto CHAR — con Trim()
    decimal  Costo,     // columna: costo NUMERIC
    decimal  Precio,    // columna: precio NUMERIC
    decimal  Saldo,     // columna: saldo NUMERIC — stock actual
    decimal? Piezas     // columna: piezas NUMERIC NULL
);
