using System.Globalization;
using System.Security.Cryptography;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Genera claves canónicas e identificadores y hashes para stock de Alegon.
/// </summary>
public static class StockCanonicalizer
{
    private const string NullLiteral = "\0NULL\0";

    /// <summary>
    /// Computa el business_key del stock (SHA-256 lowercase hex) sobre:
    /// source_id, depo, idarti, bulto (normalizado).
    /// Utiliza serialización longitud-prefijada para evitar ambigüedad.
    /// </summary>
    public static string ComputeBusinessKey(string sourceId, int depo, int articleId, string bulto)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        
        ReadOnlySpan<string> fields =
        [
            sourceId,
            depo.ToString(CultureInfo.InvariantCulture),
            articleId.ToString(CultureInfo.InvariantCulture),
            bulto.Trim()
        ];

        var canonical = MovementCanonicalizer.BuildCanonicalBytes(fields);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computa el content_hash del stock (SHA-256 lowercase hex).
    /// Distingue entre NULL y 0. Preserva la exactitud de los decimales.
    /// </summary>
    public static string ComputeContentHash(AlegonStock stock, bool isPresent)
    {
        ArgumentNullException.ThrowIfNull(stock);

        string FormatDecimal(decimal? d) =>
            d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : NullLiteral;

        ReadOnlySpan<string> fields =
        [
            stock.Depo.ToString(CultureInfo.InvariantCulture),
            stock.ArticleId.ToString(CultureInfo.InvariantCulture),
            stock.Bulto.Trim(),
            FormatDecimal(stock.Costo),
            FormatDecimal(stock.Precio),
            FormatDecimal(stock.Saldo),
            FormatDecimal(stock.Piezas),
            isPresent ? "1" : "0"
        ];

        var canonical = MovementCanonicalizer.BuildCanonicalBytes(fields);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
