using System.Data;
using System.Data.Common;
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

        // Resolviendo ordinals por nombre
        int ordArticulo = reader.GetOrdinal("articulo");
        int ordMarca = reader.GetOrdinal("marca");
        int ordDescri = reader.GetOrdinal("descri");
        int ordUnimed = reader.GetOrdinal("unimed");
        int ordBulto = reader.GetOrdinal("bulto");
        int ordTimpu = reader.GetOrdinal("timpu");
        int ordClasif = reader.GetOrdinal("clasif");
        int ordProvee = reader.GetOrdinal("provee");
        int ordArtprov = reader.GetOrdinal("artprov");
        int ordCossimp = reader.GetOrdinal("cossimp");
        int ordCossvta = reader.GetOrdinal("cossvta");
        int ordFactu = reader.GetOrdinal("factu");
        int ordStopti = reader.GetOrdinal("stopti");
        int ordPtoped = reader.GetOrdinal("ptoped");
        int ordUbicacion = reader.GetOrdinal("ubicacion");
        int ordHabcpa = reader.GetOrdinal("habcpa");
        int ordHabvta = reader.GetOrdinal("habvta");
        int ordCotiza = reader.GetOrdinal("cotiza");
        int ordCuencpa = reader.GetOrdinal("cuencpa");
        int ordCuenvta = reader.GetOrdinal("cuenvta");
        int ordDctoMax = reader.GetOrdinal("dcto_max");
        int ordIdsBArt = reader.GetOrdinal("idsbart");
        int ordIdProd = reader.GetOrdinal("idprod");
        int ordEstado = reader.GetOrdinal("estado");
        int ordEsqucalc = reader.GetOrdinal("esqucalc");
        int ordBenvase = reader.GetOrdinal("benvase");
        int ordNasocenv = reader.GetOrdinal("nasocenv");
        int ordBpesable = reader.GetOrdinal("bpesable");
        int ordCfoto = reader.GetOrdinal("cfoto");
        int ordComision = reader.GetOrdinal("comision");
        int ordNdiasvct = reader.GetOrdinal("ndiasvct");
        int ordNMinMay = reader.GetOrdinal("nMinMay");
        int ordDVigMayd = reader.GetOrdinal("dVigMayd");
        int ordDVigMayh = reader.GetOrdinal("dVigMayh");

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapRow(reader));
        }

        return results;
    }

    public static AlegonProductMaster MapRow(DbDataReader reader)
    {
        // Resolviendo ordinals por nombre
        int ordArticulo = reader.GetOrdinal("articulo");
        int ordMarca = reader.GetOrdinal("marca");
        int ordDescri = reader.GetOrdinal("descri");
        int ordUnimed = reader.GetOrdinal("unimed");
        int ordBulto = reader.GetOrdinal("bulto");
        int ordTimpu = reader.GetOrdinal("timpu");
        int ordClasif = reader.GetOrdinal("clasif");
        int ordProvee = reader.GetOrdinal("provee");
        int ordArtprov = reader.GetOrdinal("artprov");
        int ordCossimp = reader.GetOrdinal("cossimp");
        int ordCossvta = reader.GetOrdinal("cossvta");
        int ordFactu = reader.GetOrdinal("factu");
        int ordStopti = reader.GetOrdinal("stopti");
        int ordPtoped = reader.GetOrdinal("ptoped");
        int ordUbicacion = reader.GetOrdinal("ubicacion");
        int ordHabcpa = reader.GetOrdinal("habcpa");
        int ordHabvta = reader.GetOrdinal("habvta");
        int ordCotiza = reader.GetOrdinal("cotiza");
        int ordCuencpa = reader.GetOrdinal("cuencpa");
        int ordCuenvta = reader.GetOrdinal("cuenvta");
        int ordDctoMax = reader.GetOrdinal("dcto_max");
        int ordIdsBArt = reader.GetOrdinal("idsbart");
        int ordIdProd = reader.GetOrdinal("idprod");
        int ordEstado = reader.GetOrdinal("estado");
        int ordEsqucalc = reader.GetOrdinal("esqucalc");
        int ordBenvase = reader.GetOrdinal("benvase");
        int ordNasocenv = reader.GetOrdinal("nasocenv");
        int ordBpesable = reader.GetOrdinal("bpesable");
        int ordCfoto = reader.GetOrdinal("cfoto");
        int ordComision = reader.GetOrdinal("comision");
        int ordNdiasvct = reader.GetOrdinal("ndiasvct");
        int ordNMinMay = reader.GetOrdinal("nMinMay");
        int ordDVigMayd = reader.GetOrdinal("dVigMayd");
        int ordDVigMayh = reader.GetOrdinal("dVigMayh");

        try
        {
            return new AlegonProductMaster(
                ArticleId:              reader.GetInt32(ordArticulo),
                Marca:                  ReadStringNullableTrimmed(reader, ordMarca),
                Descripcion:            ReadStringNullableTrimmed(reader, ordDescri),
                UnidadMedida:           ReadStringNullableTrimmed(reader, ordUnimed),
                Bulto:                  ReadStringNullableTrimmed(reader, ordBulto),
                TipoImpuesto:           ReadStringNullableTrimmed(reader, ordTimpu),
                Clasificacion:          ReadStringNullableTrimmed(reader, ordClasif),
                Proveedor:              ReadStringNullableTrimmed(reader, ordProvee),
                ArticuloProveedor:      ReadStringNullableTrimmed(reader, ordArtprov),
                CostoImpositivo:        ReadDecimalNullable(reader, ordCossimp),
                CostoVenta:             ReadDecimalNullable(reader, ordCossvta),
                FechaActualizacion:     ReadDateTimeUnspecifiedNullable(reader, ordFactu),
                StockOptimo:            ReadDecimalNullable(reader, ordStopti),
                PuntoPedido:            ReadDecimalNullable(reader, ordPtoped),
                Ubicacion:              ReadStringNullableTrimmed(reader, ordUbicacion),
                HabilitadoCompra:       ReadBoolNullable(reader, ordHabcpa),
                HabilitadoVenta:        ReadBoolNullable(reader, ordHabvta),
                Cotizacion:             ReadStringNullableTrimmed(reader, ordCotiza),
                CuentaCompra:           ReadInt32Nullable(reader, ordCuencpa),
                CuentaVenta:            ReadInt32Nullable(reader, ordCuenvta),
                DescuentoMaximo:        ReadDecimalNullable(reader, ordDctoMax),
                IdSubRubro:             ReadInt32Nullable(reader, ordIdsBArt),
                IdProducto:             ReadInt32Nullable(reader, ordIdProd),
                Estado:                 ReadByteNullable(reader, ordEstado),
                EsquemaCalculo:         ReadStringNullableTrimmed(reader, ordEsqucalc),
                BultoEnvase:            ReadBoolNullable(reader, ordBenvase),
                NumeroAsocEnvase:       ReadDecimalNullable(reader, ordNasocenv),
                BultoPesable:           ReadBoolNullable(reader, ordBpesable),
                RutaFoto:               ReadStringNullableTrimmed(reader, ordCfoto),
                Comision:               ReadDecimalNullable(reader, ordComision),
                DiasVencimiento:        ReadDecimalNullable(reader, ordNdiasvct),
                MinimoMayorista:        ReadDecimalNullable(reader, ordNMinMay),
                VigenciaMayoristaDesde: ReadDateTimeUnspecifiedNullable(reader, ordDVigMayd),
                VigenciaMayoristaHasta: ReadDateTimeUnspecifiedNullable(reader, ordDVigMayh)
            );
        }
        catch (InvalidCastException ex)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                try
                {
                    if (!reader.IsDBNull(i))
                    {
                        var val = reader.GetValue(i); 
                    }
                }
                catch
                {
                }
            }
            
            throw new InvalidOperationException($"[ERROR] ProductReader mapping mismatch. Revisa el stack o depura localmente.", ex);
        }
    }

    private static string? ReadStringNullableTrimmed(DbDataReader r, int i)
    {
        try { return r.IsDBNull(i) ? null : r.GetString(i).TrimEnd(); }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "String", ex); }
    }

    private static decimal? ReadDecimalNullable(DbDataReader r, int i)
    {
        try { return r.IsDBNull(i) ? null : r.GetDecimal(i); }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "Decimal", ex); }
    }

    private static int? ReadInt32Nullable(DbDataReader r, int i)
    {
        try { return r.IsDBNull(i) ? null : r.GetInt32(i); }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "Int32", ex); }
    }

    private static byte? ReadByteNullable(DbDataReader r, int i)
    {
        try { return r.IsDBNull(i) ? null : r.GetByte(i); }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "Byte", ex); }
    }

    private static bool? ReadBoolNullable(DbDataReader r, int i)
    {
        try
        {
            if (r.IsDBNull(i)) return null;
            return Convert.ToBoolean(r.GetValue(i));
        }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "Boolean", ex); }
    }

    private static DateTime? ReadDateTimeUnspecifiedNullable(DbDataReader r, int i)
    {
        try
        {
            if (r.IsDBNull(i)) return null;
            var dt = r.GetDateTime(i);
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }
        catch (InvalidCastException ex) { throw CreateMappingException(r, i, "DateTime", ex); }
    }

    private static InvalidOperationException CreateMappingException(DbDataReader r, int i, string expectedType, InvalidCastException inner)
    {
        string colName = r.GetName(i);
        string sqlTypeName = r.GetDataTypeName(i);
        string actualType = r.GetFieldType(i)?.Name ?? "Unknown";

        return new InvalidOperationException(
            $"[ERROR] ProductReader mapping mismatch:\ncolumn={colName}\nordinal={i}\nexpected={expectedType}\nactual={actualType} (SQL: {sqlTypeName})", inner);
    }
}
