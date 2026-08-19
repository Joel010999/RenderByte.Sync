namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa el maestro completo de un artículo en la tabla <c>dbo.articulo</c>.
/// Contiene 34 campos requeridos por M8.1.
/// PK: <c>articulo</c> (int).
/// Las columnas de tipo CHAR se leen con <c>TrimEnd()</c> o <c>Trim()</c>.
/// </summary>
public sealed record AlegonProductMaster(
    int      ArticleId,         // articulo (int)
    string?  Marca,             // marca
    string?  Descripcion,       // descri
    string?  UnidadMedida,      // unimed
    string?  Bulto,             // bulto
    string?  TipoImpuesto,      // timpu
    string?  Clasificacion,     // clasif
    string?  Proveedor,         // provee
    string?  ArticuloProveedor, // artprov
    decimal? CostoImpositivo,   // cossimp
    decimal? CostoVenta,        // cossvta
    DateTime? FechaActualizacion, // factu (DateTime Unspecified)
    decimal? StockOptimo,       // stopti
    decimal? PuntoPedido,       // ptoped
    string?  Ubicacion,         // ubicacion
    bool?    HabilitadoCompra,  // habcpa
    bool?    HabilitadoVenta,   // habvta
    string?  Cotizacion,        // cotiza
    string?  CuentaCompra,      // cuencpa
    string?  CuentaVenta,       // cuenvta
    decimal? DescuentoMaximo,   // dcto_max
    int?     IdSubRubro,        // idsbart
    int?     IdProducto,        // idprod
    bool?    Estado,            // estado
    string?  EsquemaCalculo,    // esqucalc
    string?  BultoEnvase,       // benvase
    int?     NumeroAsocEnvase,  // nasocenv
    string?  BultoPesable,      // bpesable
    string?  RutaFoto,          // cfoto
    decimal? Comision,          // comision
    decimal? DiasVencimiento,   // ndiasvct
    decimal? MinimoMayorista,   // nMinMay
    DateTime? VigenciaMayoristaDesde, // dVigMayd
    DateTime? VigenciaMayoristaHasta  // dVigMayh
);
