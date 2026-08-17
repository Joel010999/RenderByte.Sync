using Microsoft.Data.Sqlite;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;
using Xunit;

namespace RenderByte.Sync.Tests;

/// <summary>
/// Tests de <see cref="SqliteSyncBatchStore"/> usando bases de datos SQLite temporales aisladas.
/// Cada test recibe una DB nueva en %TEMP% y la elimina al finalizar.
/// </summary>
public sealed class SqliteSyncBatchStoreTests : IDisposable
{
    private const string SrcId = "TEST-SRC-1";

    private readonly string _dbPath;

    public SqliteSyncBatchStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); }          catch { }
            try { File.Delete(_dbPath + "-wal"); } catch { }
            try { File.Delete(_dbPath + "-shm"); } catch { }
        }
    }

    // ─── 1. Schema ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_NewDb_CreatesAllThreeTables()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type='table'
              AND name IN ('sync_checkpoint', 'sync_outbox', 'sync_installation');
            """;
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Contains("sync_checkpoint",  tables);
        Assert.Contains("sync_outbox",      tables);
        Assert.Contains("sync_installation", tables);
    }

    // ─── 2. Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_BatchCorrecto_AvanzaCheckpointYGuardaOutbox_EnTransaccion()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var fedepo1 = new DateTime(2026, 8, 14, 10, 0, 0, 123);
        var mov1    = MakeMovement(fedepo1, "A", 1, "PROD1", 10.5m);

        var checkpointAfter = MovementCheckpoint.From(mov1);
        var result = await store.PersistBatchAndCheckpointAsync(2, [mov1], checkpointAfter);

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.DuplicatesSkipped);

        var data = await store.GetCheckpointAsync();
        Assert.NotNull(data);
        Assert.Equal(fedepo1, data.Fedepo);
        Assert.Equal("A", data.ClaveU);

        var pending = await store.GetPendingAsync(10);
        Assert.Single(pending);
        Assert.Equal("A", pending[0].ClaveU);
        Assert.Equal(1, pending[0].Item);
        Assert.Equal("10.5", pending[0].Cantidad);
        Assert.Equal(SrcId, pending[0].SourceId);
    }

    // ─── 3. Rollback: si falla la transacción, checkpoint y outbox no cambian ───

    [Fact]
    public async Task Save_FalloDuranteTransaccion_CheckpointNoCambia_OutboxVacio()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        // Setup checkpoint inicial manualmente
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var setupCmd = conn.CreateCommand();
        setupCmd.CommandText = """
            INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at)
            VALUES (1, 2, '2026-08-14T00:00:00.0000000', 'INI', 0, '2026-08-14T00:00:00.0000000Z');
            """;
        await setupCmd.ExecuteNonQueryAsync();

        // Forzar error dropeando sync_checkpoint (el segundo paso de la tx)
        await using var conn2 = new SqliteConnection($"Data Source={_dbPath}");
        await conn2.OpenAsync();
        using var dropCmd = conn2.CreateCommand();
        dropCmd.CommandText = "DROP TABLE sync_checkpoint;";
        await dropCmd.ExecuteNonQueryAsync();

        var mov1    = MakeMovement(DateTime.Now, "A", 1, "P", 1);
        var cpAfter = MovementCheckpoint.From(mov1);

        // La transacción debe lanzar SqliteException y hacer ROLLBACK completo
        await Assert.ThrowsAsync<SqliteException>(
            () => store.PersistBatchAndCheckpointAsync(2, [mov1], cpAfter));

        // Restaurar tabla para poder verificar el outbox
        dropCmd.CommandText = """
            CREATE TABLE sync_checkpoint (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                branch_id INTEGER NOT NULL,
                fedepo TEXT NOT NULL,
                clave_u TEXT NOT NULL,
                item INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO sync_checkpoint VALUES (1, 2, '2026-08-14T00:00:00.0000000', 'INI', 0, '2026-08-14T00:00:00.0000000Z');
            """;
        await dropCmd.ExecuteNonQueryAsync();

        // El outbox debe estar vacío (el insert de mov1 también hizo rollback)
        var cpData = await store.GetCheckpointAsync();
        Assert.NotNull(cpData);
        Assert.Equal("INI", cpData.ClaveU);

        var pending = await store.GetPendingAsync(10);
        Assert.Empty(pending);
    }

    // ─── 4. Idempotencia: mismo movement_key → una sola fila ─────────────────

    [Fact]
    public async Task MismaMovementKey_DosVeces_UnaSolaFila()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var mov1   = MakeMovement(DateTime.Now, "A", 1, "PROD1", 10.5m);
        var cpAfter = MovementCheckpoint.From(mov1);

        var r1 = await store.PersistBatchAndCheckpointAsync(2, [mov1], cpAfter);
        Assert.Equal(1, r1.Inserted);
        Assert.Equal(0, r1.DuplicatesSkipped);

        var mov1Copia = mov1 with { }; // mismo hash
        var r2 = await store.PersistBatchAndCheckpointAsync(2, [mov1Copia], cpAfter);
        Assert.Equal(0, r2.Inserted);
        Assert.Equal(1, r2.DuplicatesSkipped);

        var pending = await store.GetPendingAsync(10);
        Assert.Single(pending);
    }

    // ─── 5. Orden: GetPending retorna id ASC ────────────────────────────────────

    [Fact]
    public async Task GetPending_RespetaOrdenIdAsc()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var m1 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 1, "P1", 1);
        var m2 = MakeMovement(new DateTime(2026, 8, 14, 10, 5, 0), "A", 2, "P2", 2);
        var m3 = MakeMovement(new DateTime(2026, 8, 14, 10, 10, 0), "B", 1, "P3", 3);

        await store.PersistBatchAndCheckpointAsync(2, [m1], MovementCheckpoint.From(m1));
        await store.PersistBatchAndCheckpointAsync(2, [m2, m3], MovementCheckpoint.From(m3));

        var pending = await store.GetPendingAsync(10);
        Assert.Equal(3, pending.Count);
        Assert.Equal(1, pending[0].Id);
        Assert.Equal(2, pending[1].Id);
        Assert.Equal(3, pending[2].Id);
    }

    // ─── 6. Precisión decimal y fecha ────────────────────────────────────────────

    [Fact]
    public async Task DecimalesYFechas_PreservanValorExacto_SinPerdida()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var exactDate = new DateTime(2026, 8, 14, 17, 30, 45, 999);
        var mov       = MakeMovement(exactDate, "A", 1, "P1", 12345.6789m);
        var movExact  = mov with { Precio = 9876543.21m };

        await store.PersistBatchAndCheckpointAsync(2, [movExact], MovementCheckpoint.From(movExact));

        var pending = await store.GetPendingAsync(10);
        var p = pending[0];

        Assert.Equal("2026-08-14T17:30:45.9990000", p.Fedepo);
        Assert.Equal("12345.6789", p.Cantidad);
        Assert.Equal("9876543.21", p.Precio);
    }

    // ─── 7. Reapertura: datos persisten entre instancias del store ───────────────

    [Fact]
    public async Task Reopen_ExistingDb_KeepsOutboxAndCheckpoint()
    {
        var fedepo = new DateTime(2026, 8, 14, 17, 5, 49, 523);
        var mov    = MakeMovement(fedepo, "CLAVE001", 10, "P1", 5);
        var cp     = MovementCheckpoint.From(mov);

        var store1 = new SqliteSyncBatchStore(_dbPath);
        await store1.InitializeAsync(SrcId, branchId: 2);
        await store1.PersistBatchAndCheckpointAsync(2, [mov], cp);
        await store1.DisposeAsync();

        var store2 = new SqliteSyncBatchStore(_dbPath);
        await store2.InitializeAsync(SrcId, branchId: 2);

        var data = await store2.GetCheckpointAsync();
        Assert.NotNull(data);
        Assert.Equal(fedepo, data.Fedepo);
        Assert.Equal("CLAVE001", data.ClaveU);

        var pending = await store2.GetPendingAsync(10);
        Assert.Single(pending);
    }

    // ─── 8. Branch mismatch ──────────────────────────────────────────────────────

    [Fact]
    public async Task BranchMismatch_ThrowsException()
    {
        var store1 = new SqliteSyncBatchStore(_dbPath);
        await store1.InitializeAsync(SrcId, branchId: 2);
        var mov = MakeMovement(DateTime.Now, "A", 1, "P", 1);
        await store1.PersistBatchAndCheckpointAsync(2, [mov], MovementCheckpoint.From(mov));
        await store1.DisposeAsync();

        var store2 = new SqliteSyncBatchStore(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store2.InitializeAsync(SrcId, branchId: 3));
        Assert.Contains("[BRANCH MISMATCH]", ex.Message);
    }

    // ─── 9. Batch vacío: no-op ────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyBatch_NoCambiaCheckpoint_NiGuardaOutbox()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var mov = MakeMovement(DateTime.Now, "A", 1, "P", 1);
        await store.PersistBatchAndCheckpointAsync(2, [mov], MovementCheckpoint.From(mov));

        var cpAnterior = await store.GetCheckpointAsync();

        var emptyResult = await store.PersistBatchAndCheckpointAsync(
            2, Array.Empty<AlegonMovement>(),
            new MovementCheckpoint(DateTime.Now, "Z", 999));

        Assert.Equal(0, emptyResult.Attempted);
        Assert.Equal(0, emptyResult.Inserted);

        var cpActual = await store.GetCheckpointAsync();
        Assert.Equal(cpAnterior!.Fedepo, cpActual!.Fedepo);

        var count = await store.GetPendingCountAsync();
        Assert.Equal(1, count);
    }

    // ─── 10. MovementKey cambia si cambia PK física ────────────────────────────

    [Fact]
    public void MovementKey_Cambia_SiCambiaPkFisica()
    {
        var mov1 = MakeMovement(DateTime.Now, "A", 1, "P", 1);
        var mov2 = mov1 with { Numero = "OTRONUM" };

        var k1 = mov1.GetMovementKey(SrcId);
        var k2 = mov2.GetMovementKey(SrcId);

        Assert.NotEqual(k1, k2);
    }

    // ─── 11. Migración v1→v2→v3: preserva checkpoint existente ─────────────────

    [Fact]
    public async Task SchemaMigration_V1ToV3_Works_PreservesCheckpoint()
    {
        // Simular DB en v1 (solo sync_checkpoint, formato legado de fecha)
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = """
            CREATE TABLE sync_checkpoint (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                branch_id INTEGER NOT NULL,
                fedepo TEXT NOT NULL,
                clave_u TEXT NOT NULL,
                item INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO sync_checkpoint (id, branch_id, fedepo, clave_u, item, updated_at)
            VALUES (1, 2, '2026-08-14 10:30:00.500', 'INI', 0, '2026-08-14 00:00:00.000');
            PRAGMA user_version = 1;
            """;
        await cmd1.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        // El store debe aplicar v2 y v3 automáticamente
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        // Checkpoint debe seguir presente y fedepo migrada al nuevo formato
        var cp = await store.GetCheckpointAsync();
        Assert.NotNull(cp);
        Assert.Equal("INI", cp.ClaveU);
        Assert.Equal(new DateTime(2026, 8, 14, 10, 30, 0, 500), cp.Fedepo);

        // Outbox debe estar disponible
        var mov = MakeMovement(DateTime.Now, "B", 1, "P", 1);
        var result = await store.PersistBatchAndCheckpointAsync(2, [mov], MovementCheckpoint.From(mov));
        Assert.Equal(1, result.Inserted);
    }

    // ─── [D] Source mismatch → aborta ────────────────────────────────────────────

    [Fact]
    public async Task SourceMismatch_Aborts()
    {
        var storeA = new SqliteSyncBatchStore(_dbPath);
        await storeA.InitializeAsync("SOURCE-A", branchId: 2);
        var mov = MakeMovement(DateTime.Now, "A", 1, "P", 1);
        await storeA.PersistBatchAndCheckpointAsync(2, [mov], MovementCheckpoint.From(mov));
        await storeA.DisposeAsync();

        var storeB = new SqliteSyncBatchStore(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storeB.InitializeAsync("SOURCE-B", branchId: 2));

        Assert.Contains("[SOURCE MISMATCH]", ex.Message);
        Assert.Contains("SOURCE-A", ex.Message);
        Assert.Contains("SOURCE-B", ex.Message);
    }

    // ─── [E] Failpoint: inserts previos hacen rollback ───────────────────────────

    [Fact]
    public async Task RollbackOnInsertFailure_AllInsertsRollback_CheckpointUnchanged()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        // Checkpoint inicial
        var m0 = MakeMovement(new DateTime(2026, 8, 14, 9, 0, 0), "Z", 0, "P0", 1);
        await store.PersistBatchAndCheckpointAsync(2, [m0], MovementCheckpoint.From(m0));

        var countBefore = await store.GetPendingCountAsync();
        var cpBefore    = await store.GetCheckpointAsync();

        // Activar failpoint: lanzar excepción después del 1er insert exitoso
        store.FailAfterInsertN = 1;

        var m1 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 1, "PA", 5);
        var m2 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 2, "PB", 5);
        var m3 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 3, "PC", 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PersistBatchAndCheckpointAsync(2, [m1, m2, m3], MovementCheckpoint.From(m3)));

        // ROLLBACK: m1 también debe haberse revertido
        var countAfter = await store.GetPendingCountAsync();
        var cpAfter    = await store.GetCheckpointAsync();

        Assert.Equal(countBefore, countAfter);
        Assert.Equal(cpBefore!.ClaveU, cpAfter!.ClaveU);
        Assert.Equal(cpBefore.Fedepo,  cpAfter.Fedepo);
    }

    // ─── [F] PersistResult reporta inserted y duplicates correctamente ───────────

    [Fact]
    public async Task PersistResult_ReportsInsertedAndDuplicatesCorrectly()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var m1 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 1, "P1", 1);
        var m2 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 2, "P2", 2);

        // Primer commit: 2 insertados
        var r1 = await store.PersistBatchAndCheckpointAsync(2, [m1, m2], MovementCheckpoint.From(m2));
        Assert.Equal(2, r1.Attempted);
        Assert.Equal(2, r1.Inserted);
        Assert.Equal(0, r1.DuplicatesSkipped);

        // Segundo commit: m1 repetido + m3 nuevo
        var m3 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 3, "P3", 3);
        var r2 = await store.PersistBatchAndCheckpointAsync(2, [m1, m3], MovementCheckpoint.From(m3));
        Assert.Equal(2, r2.Attempted);
        Assert.Equal(1, r2.Inserted);
        Assert.Equal(1, r2.DuplicatesSkipped);
    }

    // ─── [G] GetPendingCountAsync no materializa filas ────────────────────────

    [Fact]
    public async Task PendingCount_DoesNotMaterializeRows()
    {
        var store = new SqliteSyncBatchStore(_dbPath);
        await store.InitializeAsync(SrcId, branchId: 2);

        var m1 = MakeMovement(new DateTime(2026, 8, 14, 10, 0, 0), "A", 1, "P1", 1);
        var m2 = MakeMovement(new DateTime(2026, 8, 14, 10, 5, 0), "A", 2, "P2", 2);
        await store.PersistBatchAndCheckpointAsync(2, [m1, m2], MovementCheckpoint.From(m2));

        // GetPendingCountAsync devuelve el COUNT correcto sin materializar filas
        var count = await store.GetPendingCountAsync();
        Assert.Equal(2L, count);

        // Verificar que DB vacía también retorna 0
        var freshDb   = Path.Combine(Path.GetTempPath(), $"sync_count_{Guid.NewGuid()}.db");
        var store2    = new SqliteSyncBatchStore(freshDb);
        await store2.InitializeAsync(SrcId, branchId: 2);
        var zeroCount = await store2.GetPendingCountAsync();
        await store2.DisposeAsync();
        try { File.Delete(freshDb); } catch { }

        Assert.Equal(0L, zeroCount);
    }

    // ─── Factory ─────────────────────────────────────────────────────────────────

    private static AlegonMovement MakeMovement(
        DateTime fedepo, string claveU, int item, string articleId = "P", decimal qty = 1) =>
        new(
            Depo:              2,
            TipoMovimiento:    "VT",
            Fecha:             fedepo.Date,
            CodigoComprobante: "TEST",
            PuntoVenta:        "0001",
            Numero:            "00000001",
            Proveedor:         "PROV",
            ArticleId:         articleId,
            Bulto:             "U",
            Local:             2,
            Item:              item,
            FechaDeposito:     fedepo,
            Oferta:            null,
            Cantidad:          qty,
            Saldo:             0m,
            Costo:             0m,
            Precio:            0m,
            ClaveU:            claveU,
            Piezas:            null
        );
}
