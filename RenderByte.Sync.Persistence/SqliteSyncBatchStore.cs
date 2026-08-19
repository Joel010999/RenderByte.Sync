using System.Globalization;
using Microsoft.Data.Sqlite;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Persistence;

/// <summary>
/// Implementación de <see cref="ISyncBatchStore"/> sobre SQLite local.
/// Maneja checkpoint + outbox de forma atómica en una sola transacción SQLite.
/// </summary>
/// <remarks>
/// Evolución del schema por <c>PRAGMA user_version</c>:
/// <list type="bullet">
///   <item>v0 → v1: crea <c>sync_checkpoint</c>.</item>
///   <item>v1 → v2: crea <c>sync_outbox</c>.</item>
///   <item>v2 → v3: crea <c>sync_installation</c> (source_id) + unifica formato de fecha en checkpoint.</item>
///   <item>v3 → v4: crea <c>product_state</c> y <c>product_outbox</c> para M8.1.</item>
/// </list>
/// </remarks>
public sealed class SqliteSyncBatchStore : ISyncBatchStore, IProductStore
{
    // ─── Schema v1 ───────────────────────────────────────────────────────────────

    private const string SchemaSqlV1 = """
        CREATE TABLE IF NOT EXISTS sync_checkpoint (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            branch_id   INTEGER NOT NULL,
            fedepo      TEXT    NOT NULL,
            clave_u     TEXT    NOT NULL,
            item        INTEGER NOT NULL,
            updated_at  TEXT    NOT NULL
        );
        """;

    // ─── Schema v2 ───────────────────────────────────────────────────────────────

    private const string SchemaSqlV2 = """
        CREATE TABLE IF NOT EXISTS sync_outbox (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            source_id           TEXT    NOT NULL,
            branch_id           INTEGER NOT NULL,
            business_key        TEXT    NOT NULL,
            movement_key        TEXT    NOT NULL UNIQUE,
            fedepo              TEXT    NOT NULL,
            clave_u             TEXT    NOT NULL,
            item                INTEGER NOT NULL,
            depo                INTEGER NOT NULL,
            tipomov             TEXT    NOT NULL,
            fecha               TEXT    NOT NULL,
            codcom              TEXT    NOT NULL,
            ptovta              TEXT    NOT NULL,
            numero              TEXT    NOT NULL,
            proveedor           TEXT    NOT NULL,
            idarti              TEXT    NOT NULL,
            bulto               TEXT    NOT NULL,
            local               INTEGER NOT NULL,
            oferta              INTEGER,
            cantidad            TEXT,
            saldo               TEXT,
            costo               TEXT,
            precio              TEXT,
            piezas              TEXT,
            status              TEXT    NOT NULL DEFAULT 'pending',
            retry_count         INTEGER NOT NULL DEFAULT 0,
            created_at          TEXT    NOT NULL,
            sent_at             TEXT,
            last_error          TEXT
        );
        """;

    // ─── Schema v3 ───────────────────────────────────────────────────────────────

    private const string SchemaSqlV3_Installation = """
        CREATE TABLE IF NOT EXISTS sync_installation (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            source_id   TEXT    NOT NULL,
            created_at  TEXT    NOT NULL
        );
        """;

    /// <summary>
    /// Migración del fedepo del checkpoint de "YYYY-MM-DD HH:mm:ss.fff"
    /// a "YYYY-MM-DDTHH:mm:ss.fffffff" (unificación al formato canónico de Alegon).
    /// Idempotente: el WHERE garantiza que no se re-migra si ya tiene 'T'.
    /// </summary>
    private const string MigrateSqlV3_CheckpointDate = """
        UPDATE sync_checkpoint
           SET fedepo = substr(fedepo, 1, 10) || 'T' || substr(fedepo, 12) || '0000'
         WHERE fedepo NOT LIKE '%T%';
        """;

    // ─── Schema v4 (Products M8.1) ────────────────────────────────────────────────

    private const string SchemaSqlV4_Products = """
        CREATE TABLE IF NOT EXISTS product_state (
            source_id       TEXT    NOT NULL,
            article_id      INTEGER NOT NULL,
            business_key    TEXT    NOT NULL,
            content_hash    TEXT    NOT NULL,
            last_seen_at    TEXT    NOT NULL,
            last_changed_at TEXT    NOT NULL,
            is_present      INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (source_id, article_id)
        );

        CREATE TABLE IF NOT EXISTS product_outbox (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            source_id       TEXT    NOT NULL,
            branch_id       INTEGER NOT NULL,
            business_key    TEXT    NOT NULL,
            article_id      INTEGER NOT NULL,
            content_hash    TEXT    NOT NULL,
            payload         TEXT    NOT NULL,
            status          TEXT    NOT NULL DEFAULT 'pending',
            retry_count     INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            sent_at         TEXT,
            last_error      TEXT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uidx_product_outbox_pending
        ON product_outbox (source_id, article_id, content_hash)
        WHERE status = 'pending';
        """;

    // ─── Formatos de fecha ────────────────────────────────────────────────────────

    /// <summary>Formatos aceptados al leer fechas de SQLite (para compatibilidad v1/v2 → v3).</summary>
    private static readonly string[] ReadDateFormats =
    [
        MovementCanonicalizer.AlegonDateFormat,   // nuevo: "yyyy-MM-ddTHH:mm:ss.fffffff"
        MovementCanonicalizer.UtcTimestampFormat, // nuevo UTC: "yyyy-MM-ddTHH:mm:ss.fffffffZ"
        "yyyy-MM-dd HH:mm:ss.fff",               // legado M4/M5 (pre-v3)
    ];

    // ─── Estado interno ───────────────────────────────────────────────────────────

    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private string _sourceId = string.Empty;
    private int _branchId;

    /// <summary>
    /// [Solo tests] Si tiene valor, lanza <see cref="InvalidOperationException"/> después de
    /// <em>N</em> inserts exitosos en el outbox dentro de una transacción, forzando un ROLLBACK.
    /// Permite verificar que la transacción deshace todos los inserts previos del batch.
    /// </summary>
    internal int? FailAfterInsertN { get; set; }

    public SqliteSyncBatchStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    // ─── ISyncBatchStore ──────────────────────────────────────────────────────────

    public async Task InitializeAsync(string sourceId, int branchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        if (_connection is not null)
            throw new InvalidOperationException("InitializeAsync ya fue llamado.");

        SyncDbPath.EnsureDirectory(_dbPath);

        _sourceId  = sourceId;
        _branchId  = branchId;
        _connection = new SqliteConnection($"Data Source={_dbPath}");

        try
        {
            await _connection.OpenAsync(cancellationToken);
            await RunNonQueryAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await RunNonQueryAsync("PRAGMA foreign_keys = ON;", cancellationToken);
            await MigrateSchemaAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"[ERROR_SQLITE] No se pudo abrir o inicializar la base de datos local en '{_dbPath}'. " +
                $"Detalle: {ex.Message}", ex);
        }

        await RegisterOrValidateSourceAsync(sourceId, cancellationToken);
        await ValidateBranchAsync(branchId, cancellationToken);
    }

    public async Task OpenExistingInstallationAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        if (_connection is not null)
            throw new InvalidOperationException("InitializeAsync u OpenExistingInstallationAsync ya fue llamado.");

        if (!System.IO.File.Exists(_dbPath))
        {
            throw new InvalidOperationException(
                $"La base de datos '{_dbPath}' no existe. La instalación nunca fue provisionada. " +
                "Ejecute un ciclo completo (ej. checkpoint-test o outbox-test) antes de iniciar outbox-sync.");
        }

        _sourceId = sourceId;
        _connection = new SqliteConnection($"Data Source={_dbPath}");

        try
        {
            await _connection.OpenAsync(cancellationToken);
            await RunNonQueryAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await RunNonQueryAsync("PRAGMA foreign_keys = ON;", cancellationToken);
            await MigrateSchemaAsync(cancellationToken);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"[ERROR_SQLITE] No se pudo abrir la base de datos local en '{_dbPath}'. " +
                $"Detalle: {ex.Message}", ex);
        }

        using var selectCmd = _connection.CreateCommand();
        selectCmd.CommandText = "SELECT source_id FROM sync_installation WHERE id = 1;";
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);

        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                "La base de datos local existe pero no contiene metadata de instalación (source_id).");
        }

        var storedSource = (string)result;
        if (!string.Equals(storedSource, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[SOURCE MISMATCH] sync.db fue creado para source_id='{storedSource}', " +
                $"pero el agente arrancó con source_id='{sourceId}'. " +
                $"No mezclar instalaciones. Verifique la variable RENDERBYTE_SYNC_SOURCE_ID.");
        }
    }

    public async Task<StoredCheckpointData?> GetCheckpointAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT branch_id, fedepo, clave_u, item, updated_at
            FROM sync_checkpoint
            WHERE id = 1;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new StoredCheckpointData(
            BranchId:  reader.GetInt32(0),
            Fedepo:    ParseAlegonDate(reader.GetString(1)),
            ClaveU:    reader.GetString(2),
            Item:      reader.GetInt32(3),
            UpdatedAt: ParseAlegonDate(reader.GetString(4)));
    }

    public async Task<PersistBatchResult> PersistBatchAndCheckpointAsync(
        int branchId,
        IReadOnlyList<AlegonMovement> movements,
        MovementCheckpoint checkpointAfter,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        // Batch vacío → no-op, checkpoint no avanza
        if (movements.Count == 0)
            return PersistBatchResult.Empty(checkpointAfter);

        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);

        try
        {
            var (inserted, duplicatesSkipped) = await InsertMovementsAsync(
                (SqliteTransaction)transaction, movements, cancellationToken);

            await UpsertCheckpointAsync(
                (SqliteTransaction)transaction, checkpointAfter, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new PersistBatchResult(
                Attempted:         movements.Count,
                Inserted:          inserted,
                DuplicatesSkipped: duplicatesSkipped,
                CheckpointAfter:   checkpointAfter);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_outbox WHERE status = 'pending';";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l ? l : Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT id, source_id, branch_id, business_key, movement_key, fedepo, clave_u, item, depo,
                   tipomov, fecha, codcom, ptovta, numero, proveedor, idarti, bulto,
                   local, oferta, cantidad, saldo, costo, precio, piezas, status,
                   retry_count, created_at, sent_at, last_error
            FROM sync_outbox
            WHERE status = 'pending'
            ORDER BY id ASC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapOutboxMessage(reader));
        }

        return list;
    }

    private static OutboxMessage MapOutboxMessage(SqliteDataReader reader) => new OutboxMessage(
        Id:                reader.GetInt64(0),
        SourceId:          reader.GetString(1),
        BranchId:          reader.GetInt32(2),
        BusinessKey:       reader.GetString(3),
        MovementKey:       reader.GetString(4),
        Fedepo:            reader.GetString(5),
        ClaveU:            reader.GetString(6),
        Item:              reader.GetInt32(7),
        Depo:              reader.GetInt32(8),
        TipoMovimiento:    reader.GetString(9),
        Fecha:             reader.GetString(10),
        CodigoComprobante: reader.GetString(11),
        PuntoVenta:        reader.GetString(12),
        Numero:            reader.GetString(13),
        Proveedor:         reader.GetString(14),
        ArticleId:         reader.GetString(15),
        Bulto:             reader.GetString(16),
        Local:             reader.GetInt32(17),
        Oferta:            reader.IsDBNull(18) ? null : reader.GetInt32(18),
        Cantidad:          reader.IsDBNull(19) ? null : reader.GetString(19),
        Saldo:             reader.IsDBNull(20) ? null : reader.GetString(20),
        Costo:             reader.IsDBNull(21) ? null : reader.GetString(21),
        Precio:            reader.IsDBNull(22) ? null : reader.GetString(22),
        Piezas:            reader.IsDBNull(23) ? null : reader.GetString(23),
        Status:            reader.GetString(24),
        RetryCount:        reader.GetInt32(25),
        CreatedAt:         reader.GetString(26),
        SentAt:            reader.IsDBNull(27) ? null : reader.GetString(27),
        LastError:         reader.IsDBNull(28) ? null : reader.GetString(28));

    public async Task<OutboxMessage?> GetMessageByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM sync_outbox WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapOutboxMessage(reader);
        }

        return null;
    }

    public async Task MarkBatchAsSentAsync(IEnumerable<long> messageIds, string batchId, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        var ids = messageIds.ToList();
        if (ids.Count == 0) return;

        var inClause = string.Join(",", ids);
        var nowUtc = DateTime.UtcNow.ToString("O");

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"""
            UPDATE sync_outbox
            SET status = 'sent',
                sent_at = @now,
                last_error = NULL
            WHERE id IN ({inClause});
            """;
        cmd.Parameters.AddWithValue("@now", nowUtc);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkBatchAsFailedAsync(IEnumerable<long> messageIds, string error, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        var ids = messageIds.ToList();
        if (ids.Count == 0) return;

        var inClause = string.Join(",", ids);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"""
            UPDATE sync_outbox
            SET retry_count = retry_count + 1,
                last_error = @error
            WHERE id IN ({inClause});
            """;
        cmd.Parameters.AddWithValue("@error", error);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    // ─── Implementación interna ────────────────────────────────────────────────────

    private async Task MigrateSchemaAsync(CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

        if (version < 1)
        {
            await RunNonQueryAsync(SchemaSqlV1, ct);
            await RunNonQueryAsync("PRAGMA user_version = 1;", ct);
            version = 1;
        }

        if (version < 2)
        {
            await RunNonQueryAsync(SchemaSqlV2, ct);
            await RunNonQueryAsync("PRAGMA user_version = 2;", ct);
            version = 2;
        }

        if (version < 3)
        {
            // Migración v3 es transaccional: creación de sync_installation + normalización de fecha
            await using var tx = await _connection!.BeginTransactionAsync(ct);
            var sqliteTx = (SqliteTransaction)tx;
            try
            {
                await RunNonQueryInTxAsync(SchemaSqlV3_Installation, sqliteTx, ct);
                await RunNonQueryInTxAsync(MigrateSqlV3_CheckpointDate, sqliteTx, ct);
                await RunNonQueryInTxAsync("PRAGMA user_version = 3;", sqliteTx, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        if (version < 4)
        {
            await RunNonQueryAsync(SchemaSqlV4_Products, ct);
            await RunNonQueryAsync("PRAGMA user_version = 4;", ct);
            version = 4;
        }
    }

    private async Task RegisterOrValidateSourceAsync(string sourceId, CancellationToken ct)
    {
        using var selectCmd = _connection!.CreateCommand();
        selectCmd.CommandText = "SELECT source_id FROM sync_installation WHERE id = 1;";
        var result = await selectCmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull)
        {
            // Primera vez: registrar el source_id para esta instalación
            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO sync_installation (id, source_id, created_at)
                VALUES (1, @sourceId, @createdAt);
                """;
            insertCmd.Parameters.AddWithValue("@sourceId", sourceId);
            insertCmd.Parameters.AddWithValue("@createdAt",
                DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture));
            await insertCmd.ExecuteNonQueryAsync(ct);
            return;
        }

        var storedSource = (string)result;
        if (!string.Equals(storedSource, sourceId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"[SOURCE MISMATCH] sync.db fue creado para source_id='{storedSource}', " +
                $"pero el agente arrancó con source_id='{sourceId}'. " +
                $"No mezclar instalaciones. Verifique la variable RENDERBYTE_SYNC_SOURCE_ID.");
    }

    private async Task ValidateBranchAsync(int branchId, CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT branch_id FROM sync_checkpoint WHERE id = 1;";
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull)
            return; // Primera ejecución, no hay checkpoint que comparar

        var storedBranch = Convert.ToInt32(result);
        if (storedBranch != branchId)
            throw new InvalidOperationException(
                $"[BRANCH MISMATCH] sync.db fue creado para la sucursal {storedBranch}, " +
                $"pero Alegon reporta {branchId}.");
    }

    private async Task<(int inserted, int duplicatesSkipped)> InsertMovementsAsync(
        SqliteTransaction transaction,
        IReadOnlyList<AlegonMovement> movements,
        CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sync_outbox (
                source_id, branch_id, business_key, movement_key, fedepo, clave_u, item, depo,
                tipomov, fecha, codcom, ptovta, numero, proveedor, idarti, bulto,
                local, oferta, cantidad, saldo, costo, precio, piezas, status,
                retry_count, created_at, sent_at, last_error
            )
            VALUES (
                @sourceId, @branchId, @businessKey, @movementKey, @fedepo, @claveU, @item, @depo,
                @tipomov, @fecha, @codcom, @ptovta, @numero, @proveedor, @idarti, @bulto,
                @local, @oferta, @cantidad, @saldo, @costo, @precio, @piezas, @status,
                @retryCount, @createdAt, @sentAt, @lastError
            )
            ON CONFLICT(movement_key) DO NOTHING;
            """;

        var pSourceId    = cmd.Parameters.Add("@sourceId",    SqliteType.Text);
        var pBranchId    = cmd.Parameters.Add("@branchId",    SqliteType.Integer);
        var pBusinessKey = cmd.Parameters.Add("@businessKey", SqliteType.Text);
        var pMovementKey = cmd.Parameters.Add("@movementKey", SqliteType.Text);
        var pFedepo      = cmd.Parameters.Add("@fedepo",      SqliteType.Text);
        var pClaveU      = cmd.Parameters.Add("@claveU",      SqliteType.Text);
        var pItem        = cmd.Parameters.Add("@item",        SqliteType.Integer);
        var pDepo        = cmd.Parameters.Add("@depo",        SqliteType.Integer);
        var pTipomov     = cmd.Parameters.Add("@tipomov",     SqliteType.Text);
        var pFecha       = cmd.Parameters.Add("@fecha",       SqliteType.Text);
        var pCodcom      = cmd.Parameters.Add("@codcom",      SqliteType.Text);
        var pPtovta      = cmd.Parameters.Add("@ptovta",      SqliteType.Text);
        var pNumero      = cmd.Parameters.Add("@numero",      SqliteType.Text);
        var pProveedor   = cmd.Parameters.Add("@proveedor",   SqliteType.Text);
        var pIdarti      = cmd.Parameters.Add("@idarti",      SqliteType.Text);
        var pBulto       = cmd.Parameters.Add("@bulto",       SqliteType.Text);
        var pLocal       = cmd.Parameters.Add("@local",       SqliteType.Integer);
        var pOferta      = cmd.Parameters.Add("@oferta",      SqliteType.Integer);
        var pCantidad    = cmd.Parameters.Add("@cantidad",    SqliteType.Text);
        var pSaldo       = cmd.Parameters.Add("@saldo",       SqliteType.Text);
        var pCosto       = cmd.Parameters.Add("@costo",       SqliteType.Text);
        var pPrecio      = cmd.Parameters.Add("@precio",      SqliteType.Text);
        var pPiezas      = cmd.Parameters.Add("@piezas",      SqliteType.Text);
        var pStatus      = cmd.Parameters.Add("@status",      SqliteType.Text);
        var pRetryCount  = cmd.Parameters.Add("@retryCount",  SqliteType.Integer);
        var pCreatedAt   = cmd.Parameters.Add("@createdAt",   SqliteType.Text);
        var pSentAt      = cmd.Parameters.Add("@sentAt",      SqliteType.Text);
        var pLastError   = cmd.Parameters.Add("@lastError",   SqliteType.Text);

        int inserted          = 0;
        int duplicatesSkipped = 0;
        int insertedSoFar     = 0;

        foreach (var movement in movements)
        {
            // Test failpoint: fuerza ROLLBACK después de N inserts exitosos
            if (FailAfterInsertN.HasValue && insertedSoFar >= FailAfterInsertN.Value)
                throw new InvalidOperationException(
                    $"[TEST_FAILPOINT] Forzado después de {FailAfterInsertN.Value} inserts.");

            var msg = OutboxMessage.CreatePending(_sourceId, _branchId, movement);

            pSourceId.Value    = msg.SourceId;
            pBranchId.Value    = msg.BranchId;
            pBusinessKey.Value = msg.BusinessKey;
            pMovementKey.Value = msg.MovementKey;
            pFedepo.Value      = msg.Fedepo;
            pClaveU.Value      = msg.ClaveU;
            pItem.Value        = msg.Item;
            pDepo.Value        = msg.Depo;
            pTipomov.Value     = msg.TipoMovimiento;
            pFecha.Value       = msg.Fecha;
            pCodcom.Value      = msg.CodigoComprobante;
            pPtovta.Value      = msg.PuntoVenta;
            pNumero.Value      = msg.Numero;
            pProveedor.Value   = msg.Proveedor;
            pIdarti.Value      = msg.ArticleId;
            pBulto.Value       = msg.Bulto;
            pLocal.Value       = msg.Local;
            pOferta.Value      = msg.Oferta ?? (object)DBNull.Value;
            pCantidad.Value    = msg.Cantidad ?? (object)DBNull.Value;
            pSaldo.Value       = msg.Saldo ?? (object)DBNull.Value;
            pCosto.Value       = msg.Costo ?? (object)DBNull.Value;
            pPrecio.Value      = msg.Precio ?? (object)DBNull.Value;
            pPiezas.Value      = msg.Piezas ?? (object)DBNull.Value;
            pStatus.Value      = msg.Status;
            pRetryCount.Value  = msg.RetryCount;
            pCreatedAt.Value   = msg.CreatedAt;
            pSentAt.Value      = msg.SentAt ?? (object)DBNull.Value;
            pLastError.Value   = msg.LastError ?? (object)DBNull.Value;

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected > 0)
            {
                inserted++;
                insertedSoFar++;
            }
            else
            {
                duplicatesSkipped++;
            }
        }

        return (inserted, duplicatesSkipped);
    }

    private async Task UpsertCheckpointAsync(
        SqliteTransaction transaction,
        MovementCheckpoint checkpoint,
        CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at)
            VALUES (1, @branchId, @fedepo, @claveU, @item, @updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                fedepo     = excluded.fedepo,
                clave_u    = excluded.clave_u,
                item       = excluded.item,
                updated_at = excluded.updated_at
            WHERE sync_checkpoint.branch_id = excluded.branch_id;
            """;

        cmd.Parameters.AddWithValue("@branchId",  _branchId);
        cmd.Parameters.AddWithValue("@fedepo",
            DateTime.SpecifyKind(checkpoint.Fedepo, DateTimeKind.Unspecified)
                .ToString(MovementCanonicalizer.AlegonDateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@claveU",    checkpoint.ClaveU);
        cmd.Parameters.AddWithValue("@item",      checkpoint.Item);
        cmd.Parameters.AddWithValue("@updatedAt",
            DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture));

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            throw new InvalidOperationException(
                $"[BUG] UpsertCheckpointAsync: el UPSERT no modificó ninguna fila. " +
                $"Posible branch_id mismatch interno (stored={_branchId}).");
    }

    private async Task RunNonQueryAsync(string sql, CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RunNonQueryInTxAsync(string sql, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void ThrowIfNotInitialized()
    {
        if (_connection is null)
            throw new InvalidOperationException("InitializeAsync debe llamarse primero.");
    }

    private static DateTime ParseAlegonDate(string value) =>
        DateTime.ParseExact(value, ReadDateFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ─── IProductStore ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, ProductState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT business_key, article_id, content_hash, is_present FROM product_state WHERE source_id = @sourceId;";
        cmd.Parameters.AddWithValue("@sourceId", _sourceId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, ProductState>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            result[key] = new ProductState(
                BusinessKey: key,
                ArticleId: reader.GetInt32(1),
                ContentHash: reader.GetString(2),
                IsPresent: reader.GetInt32(3) != 0);
        }

        return result;
    }

    public async Task UpsertStateAndOutboxAsync(
        string sourceId,
        int branchId,
        AlegonProductMaster product,
        string businessKey,
        string contentHash,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);
        var nowStr = DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture);

        try
        {
            // 1. Upsert State
            using var cmdState = _connection.CreateCommand();
            cmdState.Transaction = (SqliteTransaction)transaction;
            cmdState.CommandText = """
                INSERT INTO product_state (source_id, article_id, business_key, content_hash, last_seen_at, last_changed_at, is_present)
                VALUES (@sourceId, @articleId, @businessKey, @contentHash, @now, @now, 1)
                ON CONFLICT(source_id, article_id) DO UPDATE SET
                    content_hash = excluded.content_hash,
                    last_seen_at = excluded.last_seen_at,
                    last_changed_at = excluded.last_changed_at,
                    is_present = 1;
                """;
            cmdState.Parameters.AddWithValue("@sourceId", sourceId);
            cmdState.Parameters.AddWithValue("@articleId", product.ArticleId);
            cmdState.Parameters.AddWithValue("@businessKey", businessKey);
            cmdState.Parameters.AddWithValue("@contentHash", contentHash);
            cmdState.Parameters.AddWithValue("@now", nowStr);
            await cmdState.ExecuteNonQueryAsync(cancellationToken);

            // 2. Insert Outbox
            using var cmdOutbox = _connection.CreateCommand();
            cmdOutbox.Transaction = (SqliteTransaction)transaction;
            cmdOutbox.CommandText = """
                INSERT INTO product_outbox (source_id, branch_id, business_key, article_id, content_hash, payload, status, retry_count, created_at)
                VALUES (@sourceId, @branchId, @businessKey, @articleId, @contentHash, @payload, 'pending', 0, @now)
                ON CONFLICT(source_id, article_id, content_hash) WHERE status = 'pending' DO NOTHING;
                """;
            cmdOutbox.Parameters.AddWithValue("@sourceId", sourceId);
            cmdOutbox.Parameters.AddWithValue("@branchId", branchId);
            cmdOutbox.Parameters.AddWithValue("@businessKey", businessKey);
            cmdOutbox.Parameters.AddWithValue("@articleId", product.ArticleId);
            cmdOutbox.Parameters.AddWithValue("@contentHash", contentHash);
            cmdOutbox.Parameters.AddWithValue("@payload", payloadJson);
            cmdOutbox.Parameters.AddWithValue("@now", nowStr);
            await cmdOutbox.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkMissingAndCreateTombstoneAsync(
        string sourceId,
        int branchId,
        string businessKey,
        int articleId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        await using var transaction = await _connection!.BeginTransactionAsync(cancellationToken);
        var nowStr = DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture);

        try
        {
            // 1. Update State
            using var cmdState = _connection.CreateCommand();
            cmdState.Transaction = (SqliteTransaction)transaction;
            cmdState.CommandText = "UPDATE product_state SET is_present = 0, last_changed_at = @now, last_seen_at = @now WHERE business_key = @businessKey;";
            cmdState.Parameters.AddWithValue("@businessKey", businessKey);
            cmdState.Parameters.AddWithValue("@now", nowStr);
            var affected = await cmdState.ExecuteNonQueryAsync(cancellationToken);

            if (affected > 0)
            {
                // 2. Insert Outbox (Tombstone)
                using var cmdOutbox = _connection.CreateCommand();
                cmdOutbox.Transaction = (SqliteTransaction)transaction;
                cmdOutbox.CommandText = """
                    INSERT INTO product_outbox (source_id, branch_id, business_key, article_id, content_hash, payload, status, retry_count, created_at)
                    VALUES (@sourceId, @branchId, @businessKey, @articleId, 'TOMBSTONE', '{}', 'pending', 0, @now)
                    ON CONFLICT(source_id, article_id, content_hash) WHERE status = 'pending' DO NOTHING;
                    """;
                cmdOutbox.Parameters.AddWithValue("@sourceId", sourceId);
                cmdOutbox.Parameters.AddWithValue("@branchId", branchId);
                cmdOutbox.Parameters.AddWithValue("@businessKey", businessKey);
                cmdOutbox.Parameters.AddWithValue("@articleId", articleId);
                cmdOutbox.Parameters.AddWithValue("@now", nowStr);
                await cmdOutbox.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProductOutboxMessage>> GetPendingOutboxAsync(int limit, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
            SELECT id, business_key, article_id, content_hash, payload, status, retry_count
            FROM product_outbox
            WHERE status = 'pending' AND source_id = @sourceId
            ORDER BY id ASC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@sourceId", _sourceId);
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProductOutboxMessage>();

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProductOutboxMessage(
                Id: reader.GetInt64(0),
                BusinessKey: reader.GetString(1),
                ArticleId: reader.GetInt32(2),
                ContentHash: reader.GetString(3),
                Payload: reader.GetString(4),
                Status: reader.GetString(5),
                RetryCount: reader.GetInt32(6)
            ));
        }

        return result;
    }

    public async Task MarkOutboxSentAsync(long id, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        var nowStr = DateTime.UtcNow.ToString(MovementCanonicalizer.UtcTimestampFormat, CultureInfo.InvariantCulture);

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE product_outbox SET status = 'sent', sent_at = @now, last_error = NULL WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", nowStr);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOutboxErrorAsync(long id, string error, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE product_outbox SET retry_count = retry_count + 1, last_error = @error WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@error", error);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

