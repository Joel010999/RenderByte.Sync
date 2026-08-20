namespace RenderByte.Sync.Core.Alegon.Models;

/// <summary>
/// Representa el stock actual de un artículo en <c>dbo.artistock</c>.
/// PK: <c>Depo + IdArti + Bulto</c>.
/// </summary>
public sealed record AlegonStock(
    int Depo,         // depo (int)
    int ArticleId,    // idarti (int)
    string Bulto,     // bulto (char(6)) - con Trim()
    decimal? Costo,   // costo numeric(20,5) null
    decimal? Precio,  // precio numeric(20,5) null
    decimal? Saldo,   // saldo numeric(20,3) null
    decimal? Piezas   // piezas numeric(6,1) null
);
