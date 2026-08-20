using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Infrastructure.Alegon;

/// <summary>
/// Implementación de lectura del stock actual directamente de SQL Server.
/// </summary>
public sealed class AlegonStockReader : IStockReader
{
    private readonly string _connectionString;

    public AlegonStockReader(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<IReadOnlyList<AlegonStock>> GetFullSnapshotAsync(int branchId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                depo,
                idarti,
                bulto,
                costo,
                precio,
                saldo,
                piezas
            FROM dbo.artistock
            WHERE depo = @branchId
            ORDER BY idarti, bulto;";



        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@branchId", branchId);

        var list = new List<AlegonStock>();

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stock = new AlegonStock(
                Depo: reader.GetInt32(0),
                ArticleId: reader.GetInt32(1),
                Bulto: reader.GetString(2).Trim(),
                Costo: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                Precio: reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                Saldo: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                Piezas: reader.IsDBNull(6) ? null : reader.GetDecimal(6)
            );
            list.Add(stock);
        }



        return list;
    }
}
