namespace RenderByte.Sync.Core.Alegon;

/// <summary>
/// Resultado del health check de conexión a Alegon.
/// </summary>
/// <param name="SqlServerConnected">True si se pudo abrir conexión a SQL Server.</param>
/// <param name="DatabaseFound">True si la base <c>sistema</c> existe en el servidor.</param>
/// <param name="BranchNumber">Número de sucursal (NRO.SUCURS).</param>
/// <param name="BranchName">Nombre del local según <c>dbo.locales</c>, o null si no se encontró.</param>
/// <param name="ProductCount">Cantidad de artículos en <c>dbo.articulo</c>.</param>
/// <param name="LocalStockRecordCount">Registros de stock local en <c>dbo.artistock</c> filtrado por depo = BranchNumber.</param>
/// <param name="LastMovementInsertedAt">MAX(fedepo) en <c>dbo.movistockdt</c> filtrado por depo = BranchNumber.</param>
public sealed record AlegonHealthCheck(
    bool      SqlServerConnected,
    bool      DatabaseFound,
    int       BranchNumber,
    string?   BranchName,
    long      ProductCount,
    long      LocalStockRecordCount,
    DateTime? LastMovementInsertedAt
);
