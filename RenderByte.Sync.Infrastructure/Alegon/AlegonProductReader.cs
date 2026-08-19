using System.Data;
using Microsoft.Data.SqlClient;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Infrastructure.Alegon;

/// <summary>
/// Implementación de <see cref="IProductReader"/> para SQL Server (Alegon).
/// M8.1: Full Snapshot con SELECT ONLY.
/// </summary>
public sealed class AlegonProductReader : IProductReader
{
    private readonly string _connectionString;

    // Campos a sincronizar M8.1. Columnas explicitas.
    private const string SqlProductsSnapshot = """
        SELECT
            articulo,
            marca,
            descri,
            unimed,
            bulto,
            timpu,
            clasif,
            provee,
            artprov,
            cossimp,
            cossvta,
            factu,
            stopti,
            ptoped,
            ubicacion,
            habcpa,
            habvta,
            cotiza,
            cuencpa,
            cuenvta,
            dcto_max,
            idsbart,
            idprod,
            estado,
            esqucalc,
            benvase,
            nasocenv,
            bpesable,
            cfoto,
            comision,
            ndiasvct,
            nMinMay,
            dVigMayd,
            dVigMayh
        FROM dbo.articulo;
        """;

    public AlegonProductReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "sistema",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "RenderByte Sync Products"
        };
        _connectionString = builder.ConnectionString;
    }

    public async Task<IReadOnlyList<AlegonProductMaster>> GetFullSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(SqlProductsSnapshot, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = 120 // Un poco más alto para el snapshot por si acaso
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AlegonProductMaster>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AlegonProductMaster(
                ArticleId:              reader.GetInt32(0),
                Marca:                  ReadString(reader, 1),
                Descripcion:            ReadString(reader, 2),
                UnidadMedida:           ReadString(reader, 3),
                Bulto:                  ReadString(reader, 4),
                TipoImpuesto:           ReadString(reader, 5),
                Clasificacion:          ReadString(reader, 6),
                Proveedor:              ReadString(reader, 7),
                ArticuloProveedor:      ReadString(reader, 8),
                CostoImpositivo:        ReadDecimal(reader, 9),
                CostoVenta:             ReadDecimal(reader, 10),
                FechaActualizacion:     ReadDateTime(reader, 11),
                StockOptimo:            ReadDecimal(reader, 12),
                PuntoPedido:            ReadDecimal(reader, 13),
                Ubicacion:              ReadString(reader, 14),
                HabilitadoCompra:       ReadBool(reader, 15),
                HabilitadoVenta:        ReadBool(reader, 16),
                Cotizacion:             ReadString(reader, 17),
                CuentaCompra:           ReadString(reader, 18),
                CuentaVenta:            ReadString(reader, 19),
                DescuentoMaximo:        ReadDecimal(reader, 20),
                IdSubRubro:             ReadInt32(reader, 21),
                IdProducto:             ReadInt32(reader, 22),
                Estado:                 ReadBool(reader, 23),
                EsquemaCalculo:         ReadString(reader, 24),
                BultoEnvase:            ReadString(reader, 25),
                NumeroAsocEnvase:       ReadInt32(reader, 26),
                BultoPesable:           ReadString(reader, 27),
                RutaFoto:               ReadString(reader, 28),
                Comision:               ReadDecimal(reader, 29),
                DiasVencimiento:        ReadDecimal(reader, 30),
                MinimoMayorista:        ReadDecimal(reader, 31),
                VigenciaMayoristaDesde: ReadDateTime(reader, 32),
                VigenciaMayoristaHasta: ReadDateTime(reader, 33)
            ));
        }

        return results;
    }

    private static string? ReadString(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetString(i).TrimEnd();

    private static decimal? ReadDecimal(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetDecimal(i);

    private static int? ReadInt32(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetInt32(i);

    private static bool? ReadBool(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        // Puede venir como bit o int/numeric dependiendo de cómo se defina en SQL Server
        return Convert.ToBoolean(r.GetValue(i));
    }

    private static DateTime? ReadDateTime(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        // Fechas de Alegon deben ser Unspecified
        var dt = r.GetDateTime(i);
        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
    }
}
