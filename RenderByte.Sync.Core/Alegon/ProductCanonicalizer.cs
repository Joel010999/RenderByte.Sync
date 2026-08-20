using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Genera claves canónicas e identificadores y hashes para productos de Alegon.
/// </summary>
public static class ProductCanonicalizer
{
    private const string NullLiteral = "\0NULL\0";

    public static string ComputeBusinessKey(string sourceId, int articleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        
        ReadOnlySpan<string> fields =
        [
            sourceId,
            articleId.ToString(CultureInfo.InvariantCulture)
        ];

        var canonical = MovementCanonicalizer.BuildCanonicalBytes(fields);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computa el content_hash del producto (SHA-256 lowercase hex) sobre todos los 34 campos.
    /// Distingue entre NULL y string vacío. Preserva la exactitud de los decimales.
    /// </summary>
    public static string ComputeContentHash(AlegonProductMaster product)
    {
        ArgumentNullException.ThrowIfNull(product);

        string FormatDate(DateTime? d) =>
            d.HasValue
                ? DateTime.SpecifyKind(d.Value, DateTimeKind.Unspecified).ToString(MovementCanonicalizer.AlegonDateFormat, CultureInfo.InvariantCulture)
                : NullLiteral;

        string FormatDecimal(decimal? d) =>
            d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : NullLiteral;

        string FormatBool(bool? b) =>
            b.HasValue ? (b.Value ? "1" : "0") : NullLiteral;

        string FormatInt(int? i) =>
            i.HasValue ? i.Value.ToString(CultureInfo.InvariantCulture) : NullLiteral;

        string FormatByte(byte? b) =>
            b.HasValue ? b.Value.ToString(CultureInfo.InvariantCulture) : NullLiteral;

        string FormatString(string? s) => s ?? NullLiteral;

        ReadOnlySpan<string> fields =
        [
            product.ArticleId.ToString(CultureInfo.InvariantCulture),
            FormatString(product.Marca),
            FormatString(product.Descripcion),
            FormatString(product.UnidadMedida),
            FormatString(product.Bulto),
            FormatString(product.Timpu),
            FormatString(product.Clasificacion),
            FormatString(product.Proveedor),
            FormatString(product.ArticuloProveedor),
            FormatDecimal(product.Cossimp),
            FormatDecimal(product.Cossvta),
            FormatDate(product.Factu),
            FormatDecimal(product.Stopti),
            FormatDecimal(product.Ptoped),
            FormatString(product.Ubicacion),
            FormatBool(product.HabilitadoCompra),
            FormatBool(product.HabilitadoVenta),
            FormatString(product.Cotiza),
            FormatInt(product.CuentaCompra),
            FormatInt(product.CuentaVenta),
            FormatDecimal(product.DescuentoMaximo),
            FormatInt(product.IdsBArt),
            FormatInt(product.IdProd),
            FormatByte(product.Estado),
            FormatString(product.Esqucalc),
            FormatBool(product.Benvase),
            FormatDecimal(product.Nasocenv),
            FormatBool(product.Bpesable),
            FormatString(product.RutaFoto),
            FormatDecimal(product.Comision),
            FormatDecimal(product.Ndiasvct),
            FormatDecimal(product.NMinMay),
            FormatDate(product.DVigMayd),
            FormatDate(product.DVigMayh)
        ];

        var canonical = MovementCanonicalizer.BuildCanonicalBytes(fields);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
