using System.Text;
using Microsoft.Data.SqlClient;

namespace RenderByte.Sync.Infrastructure.Alegon;

/// <summary>
/// Implementación de <see cref="RenderByte.Sync.Core.Alegon.IProductSchemaReader"/> para discovery
/// del schema de productos en Alegon (SQL Server 2008 R2).
/// <list type="bullet">
///   <item>SELECT ONLY — nunca modifica la base.</item>
///   <item>Compatible SQL Server 2008 R2: no usa funciones introducidas después de SQL 2008 (sin OFFSET/FETCH).</item>
///   <item>Nunca convierte dbo.artistock.idarti (CHAR/VARCHAR) a integer.
///         La conversión segura va siempre en dirección integer → VARCHAR cuando se necesita comparar
///         contra dbo.articulo.articulo (INT).</item>
/// </list>
/// </summary>
public sealed class ProductSchemaReader : RenderByte.Sync.Core.Alegon.IProductSchemaReader
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

    // ─────────────────────────────────────────────────────────────────────────
    // dbo.articulo — discovery
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<string> GetSchemaInfoAsync(CancellationToken cancellationToken = default)
    {
        // Two result sets: columns then indexes of dbo.articulo.
        const string sql = """
            SELECT
                c.name         AS ColumnName,
                t.name         AS TypeName,
                c.max_length   AS MaxLength,
                c.precision    AS Precision,
                c.scale        AS Scale,
                c.is_nullable  AS IsNullable,
                c.is_identity  AS IsIdentity
            FROM sys.columns c
            INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.articulo')
            ORDER BY c.column_id;

            SELECT
                i.name        AS IndexName,
                i.type_desc   AS IndexType,
                i.is_primary_key AS IsPK,
                i.is_unique   AS IsUnique,
                c.name        AS ColumnName
            FROM sys.indexes i
            JOIN sys.index_columns ic
                ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c
                ON ic.object_id = c.object_id AND ic.column_id = c.column_id
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
        AppendResultSet(sb, reader);

        if (await reader.NextResultAsync(cancellationToken))
        {
            sb.AppendLine("\n--- INDEXES ---");
            AppendResultSet(sb, reader);
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
        // TOP without ORDER BY is intentional for a raw sample (SQL 2008 compatible).
        // Columns listed explicitly so the result is predictable regardless of schema changes.
        var sql = $"""
            SELECT TOP {limit}
                articulo,
                descri,
                marca,
                bulto,
                clasif,
                provee,
                artprov,
                habcpa,
                habvta,
                estado
            FROM dbo.articulo;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public async Task<string> GetDuplicatesInfoAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COUNT(*)              AS TotalRows,
                COUNT(DISTINCT articulo) AS DistinctArticulo,
                COUNT(DISTINCT descri)   AS DistinctDescri
            FROM dbo.articulo;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public async Task<string> GetModificationDateInfoAsync(CancellationToken cancellationToken = default)
    {
        // No modification-date column confirmed yet; return the highest articulo IDs as a proxy.
        const string sql = """
            SELECT TOP 5 articulo, descri FROM dbo.articulo ORDER BY articulo DESC;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    public Task<string> GetCostPriceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("N/A — See sample for cost/price columns (cossimp, cossvta, precio).");

    public Task<string> GetSoftDeleteInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("N/A — See sample for active/baja columns (habcpa, habvta, estado).");

    // ─────────────────────────────────────────────────────────────────────────
    // dbo.artistock — safe discovery (M8.0.1)
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> GetArtistockSchemaAsync(CancellationToken cancellationToken = default)
    {
        // Two result sets: columns then indexes of dbo.artistock.
        const string sql = """
            SELECT
                c.name         AS ColumnName,
                t.name         AS TypeName,
                c.max_length   AS MaxLength,
                c.precision    AS Precision,
                c.scale        AS Scale,
                c.is_nullable  AS IsNullable,
                c.is_identity  AS IsIdentity
            FROM sys.columns c
            INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.artistock')
            ORDER BY c.column_id;

            SELECT
                i.name           AS IndexName,
                i.type_desc      AS IndexType,
                i.is_primary_key AS IsPK,
                i.is_unique      AS IsUnique,
                c.name           AS ColumnName
            FROM sys.indexes i
            JOIN sys.index_columns ic
                ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c
                ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.object_id = OBJECT_ID('dbo.artistock')
            ORDER BY i.index_id, ic.index_column_id;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("--- ARTISTOCK COLUMNS ---");
        AppendResultSet(sb, reader);

        if (await reader.NextResultAsync(cancellationToken))
        {
            sb.AppendLine("\n--- ARTISTOCK INDEXES ---");
            AppendResultSet(sb, reader);
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public async Task<string> GetArtistockSampleIdsAsync(int limit, CancellationToken cancellationToken = default)
    {
        // SAFE: reads idarti and bulto as-is (CHAR/VARCHAR). No type conversion.
        // DISTINCT TOP without ORDER BY supported in SQL Server 2008 R2 (uses TOP in SELECT).
        // We add ORDER BY RTRIM(idarti) to get a varied, sorted sample.
        var sql = $"""
            SELECT TOP {limit}
                RTRIM(idarti)  AS idarti,
                RTRIM(bulto)   AS bulto
            FROM dbo.artistock
            GROUP BY RTRIM(idarti), RTRIM(bulto)
            ORDER BY RTRIM(idarti);
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetArtistockIdProfileAsync(CancellationToken cancellationToken = default)
    {
        // SAFE: all counts via LIKE patterns. No CAST/CONVERT of idarti to INT.
        // LIKE '%[^0-9]%' is SQL Server 2008 R2 compatible.
        // ISNUMERIC is intentionally avoided: ISNUMERIC('FA019376.00') = 1 (false positive).
        const string sql = """
            SELECT
                (SELECT COUNT_BIG(*) FROM dbo.artistock)                                                AS TotalArtistock,
                (SELECT COUNT(DISTINCT RTRIM(idarti)) FROM dbo.artistock)                               AS DistinctIdarti,
                (SELECT COUNT_BIG(*) FROM dbo.artistock WHERE idarti IS NULL)                           AS NullIdarti,
                (SELECT COUNT_BIG(*) FROM dbo.artistock WHERE LTRIM(RTRIM(idarti)) = '')               AS BlankIdarti,
                (SELECT COUNT_BIG(*) FROM dbo.artistock
                 WHERE LTRIM(RTRIM(idarti)) <> ''
                   AND RTRIM(idarti) NOT LIKE '%[^0-9]%')                                              AS OnlyDigits,
                (SELECT COUNT_BIG(*) FROM dbo.artistock
                 WHERE RTRIM(idarti) LIKE '%[^0-9]%')                                                  AS AlphanumericOrSpecial;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> GetArtistockRelationAsync(CancellationToken cancellationToken = default)
    {
        // ── SAFETY CONTRACT ──────────────────────────────────────────────────
        // FORBIDDEN pattern: casting idarti to integer type in any direction.
        //   Reason: SQL Server 2008 R2 optimizer may evaluate the coercion before
        //   a filter predicate, causing SqlException 245 on values like 'FA019376.00'.
        //
        // SAFE direction: CONVERT(VARCHAR(20), articulo.articulo) = RTRIM(idarti)
        //   The explicit CONVERT is on the integer column only. The VARCHAR side stays VARCHAR.
        //   We use NOT EXISTS to avoid joins that might trigger implicit coercion.
        //
        // Candidate A: RTRIM(idarti) = CONVERT(VARCHAR(20), articulo.articulo)
        // Candidate B: RTRIM(idarti) = RTRIM(articulo.artprov)
        // ─────────────────────────────────────────────────────────────────────
        const string sql = """
            SELECT
                (SELECT COUNT_BIG(*) FROM dbo.artistock)                                         AS TotalArtistock,

                -- Candidate A: idarti matches CONVERT(VARCHAR(20), articulo.articulo)
                (SELECT COUNT_BIG(*) FROM dbo.artistock s
                 WHERE EXISTS (
                     SELECT 1 FROM dbo.articulo a
                     WHERE CONVERT(VARCHAR(20), a.articulo) = RTRIM(s.idarti)
                 ))                                                                               AS CandA_Matches,

                (SELECT COUNT_BIG(*) FROM dbo.artistock s
                 WHERE NOT EXISTS (
                     SELECT 1 FROM dbo.articulo a
                     WHERE CONVERT(VARCHAR(20), a.articulo) = RTRIM(s.idarti)
                 ))                                                                               AS CandA_NoMatch,

                -- Candidate B: idarti matches artprov (both VARCHAR — no conversion needed)
                (SELECT COUNT_BIG(*) FROM dbo.artistock s
                 WHERE EXISTS (
                     SELECT 1 FROM dbo.articulo a
                     WHERE RTRIM(a.artprov) = RTRIM(s.idarti)
                 ))                                                                               AS CandB_Matches,

                (SELECT COUNT_BIG(*) FROM dbo.artistock s
                 WHERE NOT EXISTS (
                     SELECT 1 FROM dbo.articulo a
                     WHERE RTRIM(a.artprov) = RTRIM(s.idarti)
                 ))                                                                               AS CandB_NoMatch,

                -- Ambiguity check: how many distinct articulo.articulo values have duplicate CONVERT output
                (SELECT COUNT(*) FROM (
                    SELECT CONVERT(VARCHAR(20), articulo) AS v, COUNT(*) AS n
                    FROM dbo.articulo
                    GROUP BY CONVERT(VARCHAR(20), articulo)
                    HAVING COUNT(*) > 1
                ) AS dup_a)                                                                       AS CandA_ArticuloDuplicates,

                -- Ambiguity check: how many distinct artprov values have multiple articulo rows
                (SELECT COUNT(*) FROM (
                    SELECT RTRIM(artprov) AS v, COUNT(*) AS n
                    FROM dbo.articulo
                    GROUP BY RTRIM(artprov)
                    HAVING COUNT(*) > 1
                ) AS dup_b)                                                                       AS CandB_ArtprovDuplicates;
            """;
        return await ExecuteQueryToStringAsync(sql, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> ExecuteQueryToStringAsync(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sb = new StringBuilder();
        AppendResultSet(sb, reader);
        return sb.ToString();
    }

    /// <summary>
    /// Appends column headers and all rows from the current result set of <paramref name="reader"/>
    /// to <paramref name="sb"/>. Values are trimmed; DBNull is rendered as "NULL".
    /// </summary>
    private static void AppendResultSet(StringBuilder sb, System.Data.Common.DbDataReader reader)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            sb.Append(reader.GetName(i)).Append('\t');
        sb.AppendLine();

        // ReadAsync is not available here because this is a sync helper called after await.
        // We use the synchronous Read() inside what is already an async context.
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.GetValue(i);
                sb.Append(val is System.DBNull ? "NULL" : val?.ToString()?.Trim()).Append('\t');
            }
            sb.AppendLine();
        }
    }
}
