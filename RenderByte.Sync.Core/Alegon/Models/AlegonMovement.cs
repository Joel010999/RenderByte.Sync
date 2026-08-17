namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa una línea de movimiento de stock en <c>dbo.movistockdt</c>.
/// La identidad lógica de una línea es <c>ClaveU + Item</c>.
/// </summary>
/// <remarks>
/// Tipos mapeados del schema real validado de <c>movistockdt</c>:
/// <list type="bullet">
///   <item>Columnas CHAR (tipomov, codcom, ptovta, numero, proveedor, idarti, bulto, CLAVEU): se aplica <c>Trim()</c> al leer.</item>
///   <item><c>Cantidad</c> es NUMERIC NULL en el schema pero en los datos actuales no hay NULL. Aplicar signo según <c>TipoMovimiento</c>.</item>
///   <item><c>Local</c> es tinyint → mapeado a <c>int</c>.</item>
///   <item><c>Oferta</c> es INT NULL.</item>
///   <item><c>idarti</c> en esta tabla es CHAR(10) → se preserva como <c>string</c>. Distinto de <c>artistock.idarti</c> que es INT.</item>
/// </list>
/// </remarks>
public sealed record AlegonMovement(
    int       Depo,                // depo tinyint NOT NULL
    string    TipoMovimiento,      // tipomov char(2) NOT NULL
    DateTime  Fecha,               // fecha datetime NOT NULL
    string    CodigoComprobante,   // codcom char(4) NOT NULL
    string    PuntoVenta,          // ptovta char(4) NOT NULL
    string    Numero,              // numero char(8) NOT NULL
    string    Proveedor,           // proveedor char(13) NOT NULL
    string    ArticleId,           // idarti char(10) NOT NULL — string con Trim()
    string    Bulto,               // bulto char(6) NOT NULL
    int       Local,               // local tinyint NOT NULL
    int       Item,                // item smallint NOT NULL
    DateTime? FechaDeposito,       // fedepo datetime NULL
    int?      Oferta,              // oferta int NULL
    decimal?  Cantidad,            // cantidad numeric NULL (en datos actuales sin NULL; aplicar signo según TipoMovimiento)
    decimal?  Saldo,               // saldo numeric NULL
    decimal?  Costo,               // costo numeric NULL
    decimal?  Precio,              // precio numeric NULL
    string    ClaveU,              // CLAVEU char(10) NOT NULL
    decimal?  Piezas               // piezas numeric NULL
)
{
    /// <summary>
    /// Identidad lógica global: source_id + branch_id + CLAVEU + Item.
    /// Permite identificar el movimiento lógico a nivel de instalación y sucursal.
    /// </summary>
    /// <remarks>
    /// Formato: <c>{sourceId}|{branchId}|{ClaveU}|{Item}</c>
    /// Ejemplo: <c>CLIENTEA-SUC2|2|CL0001234|5</c>
    /// </remarks>
    public string GetBusinessKey(string sourceId, int branchId) =>
        $"{sourceId}|{branchId}|{ClaveU}|{Item}";

    /// <summary>
    /// Identidad física idempotente: SHA-256 lowercase hex de la PK física canónica.
    /// Calcula el hash mediante <see cref="MovementCanonicalizer.ComputeMovementKey"/>,
    /// usando codificación longitud-prefijada para eliminar ambigüedad de campos.
    /// </summary>
    /// <remarks>
    /// Campos incluidos: source_id, depo, tipomov, fecha, codcom, ptovta, numero,
    /// proveedor, idarti, bulto, local, item, claveu.
    /// Output: 64 caracteres hexadecimales en lowercase.
    /// </remarks>
    public string GetMovementKey(string sourceId) =>
        MovementCanonicalizer.ComputeMovementKey(sourceId, this);
}
