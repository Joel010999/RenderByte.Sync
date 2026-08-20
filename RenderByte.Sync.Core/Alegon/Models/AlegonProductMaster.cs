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
    string?  Timpu,             // timpu (char)
    string?  Clasificacion,     // clasif (char)
    string?  Proveedor,         // provee (char)
    string?  ArticuloProveedor, // artprov (char)
    decimal? Cossimp,           // cossimp (numeric)
    decimal? Cossvta,           // cossvta (numeric)
    DateTime? Factu,            // factu (DateTime Unspecified)
    decimal? Stopti,            // stopti (numeric)
    decimal? Ptoped,            // ptoped (numeric)
    string?  Ubicacion,         // ubicacion (char)
    bool?    HabilitadoCompra,  // habcpa (bit)
    bool?    HabilitadoVenta,   // habvta (bit)
    string?  Cotiza,            // cotiza (char)
    int?     CuentaCompra,      // cuencpa (int)
    int?     CuentaVenta,       // cuenvta (int)
    decimal? DescuentoMaximo,   // dcto_max (numeric)
    int?     IdsBArt,           // idsbart (int)
    int?     IdProd,            // idprod (int)
    byte?    Estado,            // estado (tinyint)
    string?  Esqucalc,          // esqucalc (char)
    bool?    Benvase,           // benvase (bit)
    decimal? Nasocenv,          // nasocenv (numeric)
    bool?    Bpesable,          // bpesable (bit)
    string?  RutaFoto,          // cfoto (varchar)
    decimal? Comision,          // comision (numeric)
    decimal? Ndiasvct,          // ndiasvct (numeric)
    decimal? NMinMay,           // nMinMay (numeric)
    DateTime? DVigMayd,         // dVigMayd (datetime)
    DateTime? DVigMayh          // dVigMayh (datetime)
);
