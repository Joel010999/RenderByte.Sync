namespace RenderByte.Sync.Core.Alegon;

public interface IProductSchemaReader
{
    // ── dbo.articulo ────────────────────────────────────────────────────────
    Task<string> GetSchemaInfoAsync(CancellationToken cancellationToken = default);
    Task<long>   GetProductCountAsync(CancellationToken cancellationToken = default);
    Task<string> GetSampleProductsAsync(int limit, CancellationToken cancellationToken = default);
    Task<string> GetDuplicatesInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetModificationDateInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetCostPriceInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetSoftDeleteInfoAsync(CancellationToken cancellationToken = default);

    // ── dbo.artistock — discovery seguro (M8.0.1) ───────────────────────────
    /// <summary>
    /// Devuelve el schema completo de dbo.artistock: columnas, tipos, PK e índices.
    /// SELECT ONLY. Compatible SQL Server 2008 R2.
    /// </summary>
    Task<string> GetArtistockSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve hasta <paramref name="limit"/> valores distintos de idarti y bulto
    /// para inspeccionar el formato real (numérico, alfanumérico, con puntos, etc.).
    /// No hace ninguna conversión de tipo. SELECT ONLY.
    /// </summary>
    Task<string> GetArtistockSampleIdsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve un perfil estadístico de dbo.artistock.idarti:
    /// total, distinct, NULL, blank, sólo-dígitos, alfanuméricos.
    /// No castea idarti a INT. Compatible SQL Server 2008 R2.
    /// </summary>
    Task<string> GetArtistockIdProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evalúa candidatos de relación entre dbo.artistock y dbo.articulo
    /// de forma segura (sin CAST de idarti a INT).
    /// Candidato A: RTRIM(idarti) = CONVERT(VARCHAR(20), articulo.articulo)
    /// Candidato B: RTRIM(idarti) = RTRIM(articulo.artprov)
    /// </summary>
    Task<string> GetArtistockRelationAsync(CancellationToken cancellationToken = default);
}
