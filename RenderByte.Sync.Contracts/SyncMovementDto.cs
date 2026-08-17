using System.Text.Json.Serialization;

namespace RenderByte.Sync.Contracts;

public sealed record SyncMovementDto(
    [property: JsonPropertyName("movement_key")] string MovementKey,
    [property: JsonPropertyName("business_key")] string BusinessKey,
    [property: JsonPropertyName("depo")] short Depo,
    [property: JsonPropertyName("tipomov")] string TipoMov,
    [property: JsonPropertyName("fecha")] string Fecha,
    [property: JsonPropertyName("codcom")] string CodCom,
    [property: JsonPropertyName("ptovta")] string PtoVta,
    [property: JsonPropertyName("numero")] string Numero,
    [property: JsonPropertyName("proveedor")] string Proveedor,
    [property: JsonPropertyName("idarti")] string IdArti,
    [property: JsonPropertyName("bulto")] string Bulto,
    [property: JsonPropertyName("local")] short Local,
    [property: JsonPropertyName("item")] short Item,
    [property: JsonPropertyName("fedepo")] string? Fedepo,
    [property: JsonPropertyName("oferta")] int? Oferta,
    [property: JsonPropertyName("cantidad")] string? Cantidad,
    [property: JsonPropertyName("saldo")] string? Saldo,
    [property: JsonPropertyName("costo")] string? Costo,
    [property: JsonPropertyName("precio")] string? Precio,
    [property: JsonPropertyName("clave_u")] string ClaveU,
    [property: JsonPropertyName("piezas")] string? Piezas
);
