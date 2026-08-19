using System.Data;
using Microsoft.Data.SqlClient;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Infrastructure.Alegon;

/// <summary>
/// Implementación de <see cref="IAlegonReader"/> sobre SQL Server (Alegon).
/// Todas las operaciones son de solo lectura (SELECT). No existe ningún mecanismo
/// de ejecución SQL genérica o arbitraria: cada consulta es una constante privada.
/// </summary>
/// <remarks>
/// SEGURIDAD: <c>ApplicationIntent=ReadOnly</c> es una pista semántica de enrutamiento
/// para grupos de disponibilidad Always On. NO es una barrera de seguridad en instancias
/// standalone. La garantía real de solo lectura la provee el usuario SQL configurado en la
/// cadena de conexión, que debe tener únicamente permiso <c>db_datareader</c> sobre la
/// base <c>sistema</c>.
/// </remarks>
public sealed class AlegonReader : IAlegonReader
{
    // La base de negocio de Alegon. Nunca se opera fuera de esta base
    // (excepto 'master', exclusivamente para verificar su existencia).
    private const string AlegonDatabase = "sistema";

    // ─── Consultas SQL ────────────────────────────────────────────────────────
    // Todas las queries son constantes de compilación. No hay interpolación
    // ni concatenación de strings externos. Los parámetros se pasan siempre
    // con SqlParameter.

    private const string SqlDatabaseExists =
        "SELECT COUNT_BIG(*) FROM sys.databases WHERE name = N'sistema';";

    private const string SqlBranchNumber =
        "SELECT CONVERT(INT, cont) FROM dbo.sisparam WHERE codi = 'NRO.SUCURS';";

    // Columnas confirmadas de dbo.locales: local, nombre, mgclied, mgclieh.
    // RTRIM en SQL porque nombre es CHAR con posibles espacios de relleno.
    private const string SqlBranchName =
        "SELECT RTRIM(nombre) FROM dbo.locales WHERE local = @NroSucursal;";

    private const string SqlProductCount =
        "SELECT COUNT_BIG(*) FROM dbo.articulo;";

    private const string SqlLocalStockCount =
        "SELECT COUNT_BIG(*) FROM dbo.artistock WHERE depo = @branchNumber;";

    private const string SqlLatestMovementDate =
        "SELECT MAX(fedepo) FROM dbo.movistockdt WHERE depo = @branchNumber;";

    // Columnas confirmadas de dbo.articulo.
    // articulo es int. Las columnas de tipo CHAR se leen con Trim() en C#.
    // habcpa y habvta se convierten con Convert.ToBoolean() para tolerar bit y numeric.
    // ndiasvct es NUMERIC nullable.
    private const string SqlProducts =
        """
        SELECT
            articulo,
            marca,
            descri,
            unimed,
            bulto,
            clasif,
            provee,
            artprov,
            ubicacion,
            habcpa,
            habvta,
            ndiasvct
        FROM dbo.articulo;
        """;

    // Columnas confirmadas de dbo.artistock.
    // PK: depo + idarti + bulto.
    // idarti es CHAR — se lee con Trim() en C#.
    // costo, precio, saldo, piezas son NUMERIC en SQL Server → decimal en C#.
    private const string SqlCurrentStock =
        """
        SELECT
            depo,
            idarti,
            bulto,
            costo,
            precio,
            saldo,
            piezas
        FROM dbo.artistock
        WHERE depo = @branchNumber;
        """;

    // ─── Query de lectura incremental con cursor compuesto ────────────────────
    // Compatibilidad: SQL Server 2008 R2 — TOP (@limit) sin OFFSET/FETCH.
    //
    // Cursor: (fedepo, CLAVEU, item).
    // CLAVEU + item es la identidad lógica confirmada por Claudio para cada renglón
    // dentro del depósito. Por eso se usa como cursor incremental: el ORDER BY sobre
    // esta terna es determinístico dentro del depósito sin necesidad de exponer la PK física.
    // La unicidad física completa está representada por la PK real de SQL Server.
    //
    // Sentinel inicial: ClaveU="" (string.Empty), item=short.MinValue.
    // SQL Server compara CHAR(10) vacío como 10 espacios, menor que cualquier CLAVEU real.
    //
    // Los parámetros del cursor usan SqlDbType explícitos (ver CreateCursorCommand).
    // Nunca acepta SQL externo: es una constante privada de compilación.
    private const string SqlMovementsAfterCheckpoint =
        """
        SELECT TOP (@limit)
            depo,
            tipomov,
            fecha,
            codcom,
            ptovta,
            numero,
            proveedor,
            idarti,
            bulto,
            local,
            item,
            fedepo,
            oferta,
            cantidad,
            saldo,
            costo,
            precio,
            CLAVEU,
            piezas
        FROM dbo.movistockdt
        WHERE depo = @branchNumber
          AND (
                fedepo > @lastFedepo
                OR (
                    fedepo = @lastFedepo
                    AND (
                        CLAVEU > @lastClaveU
                        OR (
                            CLAVEU = @lastClaveU
                            AND item > @lastItem
                        )
                    )
                )
              )
        ORDER BY
            fedepo ASC,
            CLAVEU ASC,
            item    ASC;
        """;

    // ─── Estado ───────────────────────────────────────────────────────────────

    private readonly string _connectionString;

    public AlegonReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    // ─── Métodos públicos ─────────────────────────────────────────────────────

    public async Task<AlegonHealthCheck> GetHealthCheckAsync(CancellationToken cancellationToken = default)
    {
        // 1. Verificar conectividad y existencia de la base (contra master)
        await using var masterConnection = new SqlConnection(BuildConnectionString("master"));
        await masterConnection.OpenAsync(cancellationToken);

        var databaseFound = await ExecuteScalarInt64Async(masterConnection, SqlDatabaseExists, cancellationToken) == 1;
        if (!databaseFound)
            return new AlegonHealthCheck(true, false, 0, null, 0, 0, null);

        // 2. Todas las consultas de negocio contra sistema
        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);

        var branchNumber = await QueryBranchNumberAsync(connection, cancellationToken);
        var branchName   = await QueryBranchNameAsync(connection, branchNumber, cancellationToken);
        var productCount = await ExecuteScalarInt64Async(connection, SqlProductCount, cancellationToken);
        var stockCount   = await ExecuteScalarInt64Async(connection, SqlLocalStockCount, cancellationToken,
                               ("@branchNumber", branchNumber));
        var lastMovement = await QueryLatestMovementDateAsync(connection, branchNumber, cancellationToken);

        return new AlegonHealthCheck(true, true, branchNumber, branchName, productCount, stockCount, lastMovement);
    }

    public async Task<int> GetBranchNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);
        return await QueryBranchNumberAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<AlegonProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateCommand(connection, SqlProducts);
        await using var reader  = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AlegonProduct>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AlegonProduct(
                ArticleId:         reader.GetInt32(0),                                                  // articulo int
                Marca:             reader.IsDBNull(1)  ? string.Empty : reader.GetString(1).Trim(),     // marca CHAR
                Descripcion:       reader.IsDBNull(2)  ? string.Empty : reader.GetString(2).Trim(),     // descri CHAR
                UnidadMedida:      reader.IsDBNull(3)  ? string.Empty : reader.GetString(3).Trim(),     // unimed CHAR
                Bulto:             reader.IsDBNull(4)  ? string.Empty : reader.GetString(4).Trim(),     // bulto CHAR
                Clasificacion:     reader.IsDBNull(5)  ? string.Empty : reader.GetString(5).Trim(),     // clasif CHAR
                Proveedor:         reader.IsDBNull(6)  ? string.Empty : reader.GetString(6).Trim(),     // provee CHAR
                ArticuloProveedor: reader.IsDBNull(7)  ? string.Empty : reader.GetString(7).Trim(),     // artprov CHAR
                Ubicacion:         reader.IsDBNull(8)  ? string.Empty : reader.GetString(8).Trim(),     // ubicacion CHAR
                HabilitadoCompra:  !reader.IsDBNull(9)  && Convert.ToBoolean(reader.GetValue(9)),       // habcpa
                HabilitadoVenta:   !reader.IsDBNull(10) && Convert.ToBoolean(reader.GetValue(10)),      // habvta
                DiasVencimiento:   reader.IsDBNull(11) ? null : (decimal?)reader.GetDecimal(11)         // ndiasvct NUMERIC?
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<AlegonStock>> GetCurrentStockAsync(
        int branchNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateCommand(connection, SqlCurrentStock,
            ("@branchNumber", branchNumber));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AlegonStock>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AlegonStock(
                Depo:   reader.GetInt32(0),                                                          // depo int
                IdArti: reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),              // idarti CHAR/VARCHAR — alfanumérico
                Bulto:  reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim(),              // bulto CHAR
                Costo:  reader.GetDecimal(3),                                                        // costo NUMERIC
                Precio: reader.GetDecimal(4),                                                        // precio NUMERIC
                Saldo:  reader.GetDecimal(5),                                                        // saldo NUMERIC
                Piezas: reader.IsDBNull(6) ? null : (decimal?)reader.GetDecimal(6)                  // piezas NUMERIC NULL
            ));
        }
        return results;
    }

    public async Task<DateTime?> GetLatestMovementInsertionDateAsync(
        int branchNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);
        return await QueryLatestMovementDateAsync(connection, branchNumber, cancellationToken);
    }

    public async Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(
        int                branchNumber,
        MovementCheckpoint checkpoint,
        int                limit,
        CancellationToken  cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "El límite debe ser mayor a cero.");

        await using var connection = new SqlConnection(BuildConnectionString(AlegonDatabase));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateCursorCommand(connection, checkpoint, branchNumber, limit);
        await using var reader  = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AlegonMovement>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(MapMovement(reader));

        return results;
    }

    // ─── Mapeo de filas de movistockdt ────────────────────────────────────────

    /// <summary>
    /// Mapea una fila de SqlDataReader al record <see cref="AlegonMovement"/>.
    /// Los índices de columna corresponden al SELECT de <see cref="SqlMovementsAfterCheckpoint"/>.
    /// Las columnas CHAR se leen con Trim() para eliminar relleno de espacios.
    /// </summary>
    private static AlegonMovement MapMovement(System.Data.Common.DbDataReader r) =>
        new(
            Depo:                 r.GetByte(0),                                                   // depo tinyint
            TipoMovimiento:       r.IsDBNull(1)  ? string.Empty : r.GetString(1).Trim(),          // tipomov char(2)
            Fecha:                r.GetDateTime(2),                                                // fecha datetime
            CodigoComprobante:    r.IsDBNull(3)  ? string.Empty : r.GetString(3).Trim(),          // codcom char(4)
            PuntoVenta:           r.IsDBNull(4)  ? string.Empty : r.GetString(4).Trim(),          // ptovta char(4)
            Numero:               r.IsDBNull(5)  ? string.Empty : r.GetString(5).Trim(),          // numero char(8)
            Proveedor:            r.IsDBNull(6)  ? string.Empty : r.GetString(6).Trim(),          // proveedor char(13)
            ArticleId:            r.IsDBNull(7)  ? string.Empty : r.GetString(7).Trim(),          // idarti char(10)
            Bulto:                r.IsDBNull(8)  ? string.Empty : r.GetString(8).Trim(),          // bulto char(6)
            Local:                r.GetByte(9),                                                    // local tinyint
            Item:                 r.GetInt16(10),                                                  // item smallint
            FechaDeposito:        r.IsDBNull(11) ? null : (DateTime?)r.GetDateTime(11),           // fedepo datetime NULL
            Oferta:               r.IsDBNull(12) ? null : (int?)r.GetInt32(12),                   // oferta int NULL
            Cantidad:             r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),             // cantidad numeric NULL
            Saldo:                r.IsDBNull(14) ? null : (decimal?)r.GetDecimal(14),             // saldo numeric NULL
            Costo:                r.IsDBNull(15) ? null : (decimal?)r.GetDecimal(15),             // costo numeric NULL
            Precio:               r.IsDBNull(16) ? null : (decimal?)r.GetDecimal(16),             // precio numeric NULL
            ClaveU:               r.IsDBNull(17) ? string.Empty : r.GetString(17).Trim(),         // CLAVEU char(10)
            Piezas:               r.IsDBNull(18) ? null : (decimal?)r.GetDecimal(18)              // piezas numeric NULL
        );

    // ─── Helpers privados ─────────────────────────────────────────────────────

    /// <summary>
    /// Crea el <see cref="SqlCommand"/> para la query del cursor compuesto,
    /// usando <see cref="SqlParameter"/> con tipos explícitos coherentes con el schema real
    /// de <c>dbo.movistockdt</c>. Evita conversiones implícitas de <c>AddWithValue</c>.
    /// </summary>
    /// <remarks>
    /// Tipos utilizados:
    /// <list type="bullet">
    ///   <item><c>@limit</c>        → <see cref="SqlDbType.Int"/>      (parámetro de control)</item>
    ///   <item><c>@branchNumber</c> → <see cref="SqlDbType.TinyInt"/>  (depo tinyint NOT NULL)</item>
    ///   <item><c>@lastFedepo</c>   → <see cref="SqlDbType.DateTime"/> (fedepo datetime NULL)</item>
    ///   <item><c>@lastClaveU</c>   → <see cref="SqlDbType.Char"/> 10  (CLAVEU char(10) NOT NULL)</item>
    ///   <item><c>@lastItem</c>     → <see cref="SqlDbType.SmallInt"/> (item smallint NOT NULL)</item>
    /// </list>
    /// El sentinel <c>ClaveU=""</c> con <c>SqlDbType.Char,10</c> se compara en SQL Server como
    /// 10 espacios (mínimo de CHAR(10)), garantizando que el cursor inicial no omite ninguna fila.
    /// </remarks>
    private static SqlCommand CreateCursorCommand(
        SqlConnection      connection,
        MovementCheckpoint checkpoint,
        int                branchNumber,
        int                limit)
    {
        var cmd = new SqlCommand(SqlMovementsAfterCheckpoint, connection)
        {
            CommandType    = System.Data.CommandType.Text,
            CommandTimeout = 30
        };

        cmd.Parameters.Add("@limit",        SqlDbType.Int).Value       = limit;
        cmd.Parameters.Add("@branchNumber", SqlDbType.TinyInt).Value   = (byte)branchNumber;  // depo tinyint
        cmd.Parameters.Add("@lastFedepo",   SqlDbType.DateTime).Value  = checkpoint.Fedepo;   // fedepo datetime
        // CLAVEU char(10): Char con size=10 preserva la semántica de padding CHAR en comparaciones >.
        cmd.Parameters.Add("@lastClaveU",   SqlDbType.Char, 10).Value  = checkpoint.ClaveU;
        cmd.Parameters.Add("@lastItem",     SqlDbType.SmallInt).Value  = (short)checkpoint.Item; // item smallint

        return cmd;
    }

    /// <summary>
    /// Construye la cadena de conexión forzando siempre la base indicada.
    /// El servidor, usuario y contraseña provienen de la cadena externa del operador.
    /// <c>ApplicationIntent=ReadOnly</c> se agrega como señal semántica, NO como barrera de seguridad.
    /// </summary>
    private string BuildConnectionString(string database)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog    = database,                         // siempre forzado: "master" o "sistema"
            ApplicationIntent = ApplicationIntent.ReadOnly,       // semántico, no de seguridad (ver XML summary)
            ApplicationName   = "RenderByte Sync"
        };
        return builder.ConnectionString;
    }

    private static async Task<int> QueryBranchNumberAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, SqlBranchNumber);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException("No se encontró el parámetro NRO.SUCURS en dbo.sisparam.");
        return Convert.ToInt32(result);
    }

    private static async Task<string?> QueryBranchNameAsync(
        SqlConnection connection,
        int branchNumber,
        CancellationToken cancellationToken)
    {
        // Parámetro @NroSucursal coincide con la convención de sisparam y locales.
        await using var command = CreateCommand(connection, SqlBranchName,
            ("@NroSucursal", branchNumber));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
        // RTRIM ya aplicado en SQL; no se hace Trim() adicional en C#.
    }

    private static async Task<DateTime?> QueryLatestMovementDateAsync(
        SqlConnection connection,
        int branchNumber,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(connection, SqlLatestMovementDate, cancellationToken,
            ("@branchNumber", branchNumber));
        return result is null or DBNull ? null : (DateTime?)Convert.ToDateTime(result);
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var result = await ExecuteScalarAsync(connection, sql, cancellationToken, parameters);
        return Convert.ToInt64(result);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Crea un <see cref="SqlCommand"/> con <c>CommandType.Text</c> y timeout de 30 s.
    /// Solo se llama desde métodos internos con queries que son constantes privadas de esta clase.
    /// No existe ningún punto de entrada público que permita pasar SQL externo.
    /// </summary>
    private static SqlCommand CreateCommand(
        SqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection)
        {
            CommandType    = System.Data.CommandType.Text,
            CommandTimeout = 30
        };

        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        return command;
    }
}
