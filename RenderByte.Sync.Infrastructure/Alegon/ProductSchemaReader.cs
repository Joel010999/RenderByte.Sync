using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using RenderByte.Sync.Core.Alegon;

namespace RenderByte.Sync.Infrastructure.Alegon;

public sealed class ProductSchemaReader : IProductSchemaReader
{
    private readonly string _connectionString;

    public ProductSchemaReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly
        }.ConnectionString;
    }

    private async Task<string> ExecuteQueryToStringAsync(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sb = new StringBuilder();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            sb.Append(reader.GetName(i)).Append('\t');
        }
        sb.AppendLine();

        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.GetValue(i);
                sb.Append(val is DBNull ? "NULL" : val?.ToString()?.Trim()).Append('\t');
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> GetSchemaInfoAsync(CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT 
                c.name AS ColumnName,
                t.name AS TypeName,
                c.max_length AS MaxLength,
                c.precision AS Precision,
                c.scale AS Scale,
                c.is_nullable AS IsNullable,
                c.is_identity AS IsIdentity
            FROM sys.columns c
            INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.articulo')
            ORDER BY c.column_id;

            SELECT 
                i.name AS IndexName, 
                i.type_desc AS IndexType,
                i.is_primary_key AS IsPK,
                i.is_unique AS IsUnique,
                c.name AS ColumnName
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.object_id = OBJECT_ID('dbo.articulo')
            ORDER BY i.index_id, ic.index_column_id;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        var sb = new StringBuilder();
        sb.AppendLine("--- COLUMNS ---");
        for (int i = 0; i < reader.FieldCount; i++) sb.Append(reader.GetName(i)).Append('\t');
        sb.AppendLine();

        while (await reader.ReadAsync(cancellationToken))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.GetValue(i);
                sb.Append(val is DBNull ? "NULL" : val?.ToString()?.Trim()).Append('\t');
            }
            sb.AppendLine();
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            sb.AppendLine("\n--- INDEXES ---");
            for (int i = 0; i < reader.FieldCount; i++) sb.Append(reader.GetName(i)).Append('\t');
            sb.AppendLine();
            
            while (await reader.ReadAsync(cancellationToken))
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.GetValue(i);
                    sb.Append(val is DBNull ? "NULL" : val?.ToString()?.Trim()).Append('\t');
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public async Task<long> GetProductCountAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT_BIG(*) FROM dbo.articulo;";

        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<string> GetSampleProductsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return await ExecuteQueryToStringAsync($"SELECT TOP {limit} * FROM dbo.articulo;", cancellationToken);
    }

    public async Task<string> GetArtistockRelationAsync(CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT 
                (SELECT COUNT_BIG(*) FROM dbo.artistock) AS TotalArtistock,
                (SELECT COUNT_BIG(*) FROM dbo.artistock s LEFT JOIN dbo.articulo a ON s.idarti = CAST(a.articulo AS varchar(10)) WHERE a.articulo IS NULL) AS ArtistockWithoutArticulo,
                (SELECT COUNT_BIG(*) FROM dbo.artistock s LEFT JOIN dbo.articulo a ON s.idarti = a.artprov WHERE a.articulo IS NULL) AS ArtistockWithoutArtprov;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public async Task<string> GetDuplicatesInfoAsync(CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT COUNT(*) AS TotalRows, COUNT(DISTINCT articulo) AS DistinctArticulo, COUNT(DISTINCT descri) AS DistinctDescri FROM dbo.articulo;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public async Task<string> GetModificationDateInfoAsync(CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT TOP 5 articulo, descri FROM dbo.articulo ORDER BY articulo DESC;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public Task<string> GetCostPriceInfoAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("N/A - See sample for columns");
    }

    public Task<string> GetSoftDeleteInfoAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("N/A - See sample for columns");
    }
}
