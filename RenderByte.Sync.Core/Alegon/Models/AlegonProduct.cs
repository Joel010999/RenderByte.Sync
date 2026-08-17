namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa un artículo del catálogo en la tabla <c>dbo.articulo</c>.
/// PK: <c>articulo</c> (int).
/// Las columnas de tipo CHAR se leen con <c>Trim()</c> para eliminar espacios de relleno.
/// </summary>
public sealed record AlegonProduct(
    int      ArticleId,         // columna: articulo (int)
    string   Marca,             // columna: marca
    string   Descripcion,       // columna: descri
    string   UnidadMedida,      // columna: unimed
    string   Bulto,             // columna: bulto
    string   Clasificacion,     // columna: clasif
    string   Proveedor,         // columna: provee
    string   ArticuloProveedor, // columna: artprov
    string   Ubicacion,         // columna: ubicacion
    bool     HabilitadoCompra,  // columna: habcpa
    bool     HabilitadoVenta,   // columna: habvta
    decimal? DiasVencimiento    // columna: ndiasvct (NUMERIC nullable)
);
