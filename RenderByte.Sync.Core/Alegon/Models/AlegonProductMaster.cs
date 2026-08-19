namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa el maestro completo de un artículo en la tabla <c>dbo.articulo</c>.
/// Contiene 34 campos requeridos por M8.1.
/// PK: <c>articulo</c> (int).
/// Las columnas de tipo CHAR se leen con <c>TrimEnd()</c> o <c>Trim()</c>.
/// </summary>
public sealed record AlegonProductMaster(
    int      ArticleId,         // articulo (int)
    string?  Marca,             // marca (char)
    string?  Descripcion,       // descri (char)
    string?  UnidadMedida,      // unimed (char)
    string?  Bulto,             // bulto (char)
    string?  TipoImpuesto,      // timpu (char)
    string?  Clasificacion,     // clasif (char)
    string?  Proveedor,         // provee (char)
    string?  ArticuloProveedor, // artprov (char)
    decimal? CostoImpositivo,   // cossimp (numeric)
    decimal? CostoVenta,        // cossvta (numeric)
    DateTime? FechaActualizacion, // factu (DateTime Unspecified)
    decimal? StockOptimo,       // stopti (numeric)
    decimal? PuntoPedido,       // ptoped (numeric)
    string?  Ubicacion,         // ubicacion (char)
    bool?    HabilitadoCompra,  // habcpa (bit)
    bool?    HabilitadoVenta,   // habvta (bit)
    string?  Cotizacion,        // cotiza (char)
    int?     CuentaCompra,      // cuencpa (int)
    int?     CuentaVenta,       // cuenvta (int)
    decimal? DescuentoMaximo,   // dcto_max (numeric)
    int?     IdSubRubro,        // idsbart (int)
    int?     IdProducto,        // idprod (int)
    byte?    Estado,            // estado (tinyint)
    string?  EsquemaCalculo,    // esqucalc (char)
    bool?    BultoEnvase,       // benvase (bit)
    decimal? NumeroAsocEnvase,  // nasocenv (numeric)
    bool?    BultoPesable,      // bpesable (bit)
    string?  RutaFoto,          // cfoto (varchar)
    decimal? Comision,          // comision (numeric)
    decimal? DiasVencimiento,   // ndiasvct (numeric)
    decimal? MinimoMayorista,   // nMinMay (numeric)
    DateTime? VigenciaMayoristaDesde, // dVigMayd (datetime)
    DateTime? VigenciaMayoristaHasta  // dVigMayh (datetime)
);
