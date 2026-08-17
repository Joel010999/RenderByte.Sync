using System.Globalization;

namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa un mensaje pendiente (o enviado) en el Outbox local de RenderByte Sync.
/// Conserva exactamente todos los datos del movimiento original leído de Alegon.
/// </summary>
/// <remarks>
/// Todos los campos de fecha y decimal se almacenan como TEXT para preservar el valor exacto:
/// <list type="bullet">
///   <item><c>fedepo</c>, <c>fecha</c>: formato <see cref="MovementCanonicalizer.AlegonDateFormat"/> (sin zona, valor literal de Alegon).</item>
///   <item><c>created_at</c>, <c>sent_at</c>: formato <see cref="MovementCanonicalizer.UtcTimestampFormat"/> (UTC real).</item>
///   <item>Decimales (cantidad, saldo, costo, precio, piezas): <c>InvariantCulture</c>, nunca pasan por float/double.</item>
/// </list>
/// </remarks>
public sealed record OutboxMessage(
    long Id,
    string SourceId,
    int BranchId,
    string BusinessKey,
    string MovementKey,
    string Fedepo,
    string ClaveU,
    int Item,
    int Depo,
    string TipoMovimiento,
    string Fecha,
    string CodigoComprobante,
    string PuntoVenta,
    string Numero,
    string Proveedor,
    string ArticleId,
    string Bulto,
    int Local,
    int? Oferta,
    string? Cantidad,
    string? Saldo,
    string? Costo,
    string? Precio,
    string? Piezas,
    string Status,
    int RetryCount,
    string CreatedAt,
    string? SentAt,
    string? LastError)
{
    public const string StatusPending = "pending";
    public const string StatusSent    = "sent";
    public const string StatusError   = "error";

    /// <summary>
    /// Crea un nuevo OutboxMessage pendiente a partir de un AlegonMovement.
    /// </summary>
    /// <param name="sourceId">Identificador de la instalación local (de <c>RENDERBYTE_SYNC_SOURCE_ID</c>).</param>
    /// <param name="branchId">Número de sucursal reportado por Alegon.</param>
    /// <param name="movement">El movimiento leído de Alegon.</param>
    public static OutboxMessage CreatePending(string sourceId, int branchId, AlegonMovement movement)
    {
        var businessKey  = movement.GetBusinessKey(sourceId, branchId);
        var movementKey  = movement.GetMovementKey(sourceId);
        var nowUtc       = DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture);
        var alegonFormat = MovementCanonicalizer.AlegonDateFormat;

        return new OutboxMessage(
            Id:                0, // auto-incremental SQLite
            SourceId:          sourceId,
            BranchId:          branchId,
            BusinessKey:       businessKey,
            MovementKey:       movementKey,
            Fedepo:            movement.FechaDeposito?.ToString(alegonFormat, CultureInfo.InvariantCulture) ?? string.Empty,
            ClaveU:            movement.ClaveU,
            Item:              movement.Item,
            Depo:              movement.Depo,
            TipoMovimiento:    movement.TipoMovimiento,
            Fecha:             DateTime.SpecifyKind(movement.Fecha, DateTimeKind.Unspecified)
                                   .ToString(alegonFormat, CultureInfo.InvariantCulture),
            CodigoComprobante: movement.CodigoComprobante,
            PuntoVenta:        movement.PuntoVenta,
            Numero:            movement.Numero,
            Proveedor:         movement.Proveedor,
            ArticleId:         movement.ArticleId,
            Bulto:             movement.Bulto,
            Local:             movement.Local,
            Oferta:            movement.Oferta,
            Cantidad:          movement.Cantidad?.ToString(CultureInfo.InvariantCulture),
            Saldo:             movement.Saldo?.ToString(CultureInfo.InvariantCulture),
            Costo:             movement.Costo?.ToString(CultureInfo.InvariantCulture),
            Precio:            movement.Precio?.ToString(CultureInfo.InvariantCulture),
            Piezas:            movement.Piezas?.ToString(CultureInfo.InvariantCulture),
            Status:            StatusPending,
            RetryCount:        0,
            CreatedAt:         nowUtc,
            SentAt:            null,
            LastError:         null
        );
    }
}
