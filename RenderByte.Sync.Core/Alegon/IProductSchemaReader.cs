namespace RenderByte.Sync.Core.Alegon;

public interface IProductSchemaReader
{
    Task<string> GetSchemaInfoAsync(CancellationToken cancellationToken = default);
    Task<long> GetProductCountAsync(CancellationToken cancellationToken = default);
    Task<string> GetSampleProductsAsync(int limit, CancellationToken cancellationToken = default);
    Task<string> GetArtistockRelationAsync(CancellationToken cancellationToken = default);
    Task<string> GetDuplicatesInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetModificationDateInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetCostPriceInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetSoftDeleteInfoAsync(CancellationToken cancellationToken = default);
}
