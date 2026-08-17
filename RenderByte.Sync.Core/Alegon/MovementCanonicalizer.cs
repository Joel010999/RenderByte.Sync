using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Genera claves canónicas para movimientos de Alegon usando codificación longitud-prefijada.
/// <para>
/// El formato canónico elimina toda ambigüedad entre campos:
/// para cada campo se escribe <c>[int32 LE: byte_count][UTF-8 bytes del valor]</c>.
/// Esto garantiza que "A|B" + "C" y "A" + "B|C" producen representaciones canónicas distintas.
/// </para>
/// </summary>
public static class MovementCanonicalizer
{
    /// <summary>
    /// Formato de fechas provenientes de Alegon (Kind=Unspecified, sin sufijo de zona).
    /// Preserva el valor literal sin ninguna conversión a UTC.
    /// </summary>
    public const string AlegonDateFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";

    /// <summary>
    /// Formato de timestamps generados localmente (created_at, updated_at, sent_at).
    /// Siempre UTC, indicado con el sufijo 'Z'.
    /// </summary>
    public const string UtcTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>
    /// Computa el movement_key: SHA-256 lowercase hex sobre la serialización canónica
    /// longitud-prefijada de los 13 campos de la PK física de <c>movistockdt</c> + <c>source_id</c>.
    /// </summary>
    /// <remarks>
    /// Campos incluidos en orden exacto:
    ///   source_id, depo, tipomov, fecha, codcom, ptovta, numero,
    ///   proveedor, idarti, bulto, local, item, claveu
    ///
    /// Output: 64 caracteres hexadecimales en lowercase.
    /// </remarks>
    public static string ComputeMovementKey(string sourceId, AlegonMovement movement)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentNullException.ThrowIfNull(movement);

        // Normalizar Kind de la fecha: preservar valor Alegon literal sin conversión de zona.
        // DateTime.SpecifyKind asegura que el formato "O" no agrega ningún sufijo de offset.
        var fechaStr = DateTime.SpecifyKind(movement.Fecha, DateTimeKind.Unspecified)
            .ToString(AlegonDateFormat, CultureInfo.InvariantCulture);

        // Campos en el orden exacto de la PK física de movistockdt + source_id como prefijo de instalación.
        ReadOnlySpan<string> fields =
        [
            sourceId,
            movement.Depo.ToString(CultureInfo.InvariantCulture),
            movement.TipoMovimiento,
            fechaStr,
            movement.CodigoComprobante,
            movement.PuntoVenta,
            movement.Numero,
            movement.Proveedor,
            movement.ArticleId,
            movement.Bulto,
            movement.Local.ToString(CultureInfo.InvariantCulture),
            movement.Item.ToString(CultureInfo.InvariantCulture),
            movement.ClaveU,
        ];

        var canonical = BuildCanonicalBytes(fields);
        var hash = SHA256.HashData(canonical);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Construye el array de bytes canónico usando codificación longitud-prefijada.
    /// Para cada campo: <c>[4 bytes LE int32 = byte_count][N bytes UTF-8 del valor]</c>.
    /// </summary>
    /// <remarks>
    /// Accesible internamente para tests que validan la canonicalización de forma aislada.
    /// </remarks>
    internal static byte[] BuildCanonicalBytes(ReadOnlySpan<string> fields)
    {
        // Calcular tamaño total del buffer para evitar realocaciones
        var totalSize = 0;
        foreach (var field in fields)
            totalSize += 4 + Encoding.UTF8.GetByteCount(field);

        var buffer = new byte[totalSize];
        var offset = 0;

        foreach (var field in fields)
        {
            var encoded = Encoding.UTF8.GetBytes(field);

            // Prefijo de longitud: int32 en little-endian (explícito para ser independiente del SO)
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), encoded.Length);
            offset += 4;

            encoded.CopyTo(buffer, offset);
            offset += encoded.Length;
        }

        return buffer;
    }
}
