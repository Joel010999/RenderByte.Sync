using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class OutboxTestAgent
{
    private const string DefaultFallbackDate = "2026-08-14 17:00:00";
    private const int    DefaultBatchSize    = 10;

    public static async Task<int> RunTestAsync(
        IAlegonReader reader,
        string sourceId,
        string[] args,
        CancellationToken ct)
    {
        var fallbackDateStr = args.Length > 0 ? args[0] : DefaultFallbackDate;
        var batchSize = args.Length > 1 && int.TryParse(args[1], out var b) ? b : DefaultBatchSize;

        if (!DateTime.TryParseExact(fallbackDateStr, "yyyy-MM-dd HH:mm:ss",
                null, System.Globalization.DateTimeStyles.None, out var fallbackDate))
        {
            Console.WriteLine($"[ERROR] Fecha inválida. Formato esperado: 'yyyy-MM-dd HH:mm:ss'. Dado: {fallbackDateStr}");
            return 1;
        }

        Console.WriteLine("--- RENDERBYTE SYNC: OUTBOX TEST ---");
        Console.WriteLine($"source_id : {sourceId}");

        var branchId = await reader.GetBranchNumberAsync(ct);
        Console.WriteLine($"branch_id : {branchId}");

        var dbPath = SyncDbPath.Resolve();
        Console.WriteLine($"db        : {dbPath}");

        await using var store = new SqliteSyncBatchStore(dbPath);
        await store.InitializeAsync(sourceId, branchId, ct);

        var checkpoint = await store.GetCheckpointAsync(ct);
        var cursor     = checkpoint?.ToMovementCheckpoint() ?? MovementCheckpoint.Initial(fallbackDate);

        Console.WriteLine($"\nCheckpoint before:\n  {cursor}");

        var batchReader = new MovementBatchReader(reader, branchId, batchSize);
        var batchResult = await batchReader.ReadNextBatchAsync(cursor, ct);

        Console.WriteLine($"\nMovements read: {batchResult.Movements.Count}");

        if (!batchResult.IsEmpty)
        {
            var persistResult = await store.PersistBatchAndCheckpointAsync(
                branchId, batchResult.Movements, batchResult.CheckpointAfter, ct);

            Console.WriteLine($"\nOutbox inserted        : {persistResult.Inserted}");
            Console.WriteLine($"Outbox duplicates      : {persistResult.DuplicatesSkipped}");
            Console.WriteLine($"\nCheckpoint after:\n  {persistResult.CheckpointAfter}");
        }
        else
        {
            Console.WriteLine("\nCheckpoint after:");
            Console.WriteLine($"  {batchResult.CheckpointAfter} (sin cambios)");
        }

        var pendingTotal = await store.GetPendingCountAsync(ct);
        Console.WriteLine($"\nPending total: {pendingTotal}");
        Console.WriteLine("\n[OK] Transaction and fetch completed.");
        return 0;
    }

    public static async Task<int> RunShowAsync(string[] args, CancellationToken ct)
    {
        var limit = args.Length > 0 && int.TryParse(args[0], out var l) ? l : 20;

        Console.WriteLine("--- RENDERBYTE SYNC: OUTBOX SHOW ---");

        var dbPath = SyncDbPath.Resolve();

        // Lectura directa sin inicializar el store (no valida source_id ni branch)
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(ct);

        var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM sync_outbox WHERE status = 'pending';";
        long totalPending = 0L;
        try
        {
            totalPending = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine("La tabla outbox no existe aún. Ejecute outbox-test primero.");
            return 0;
        }

        var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, movement_key, fedepo, clave_u, item, tipomov, codcom, idarti, cantidad, status, source_id
            FROM sync_outbox
            WHERE status = 'pending'
            ORDER BY id ASC
            LIMIT {limit};
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);

        Console.WriteLine(
            $"{"ID",-5} | {"MOVEMENT_KEY",-15} | {"FEDEPO",-27} | {"CLAVEU",-10} | {"ITEM",-5} | {"TMOV",-4} | {"CCOM",-4} | {"IDARTI",-10} | {"CANT",-10} | {"STATUS",-8}");
        Console.WriteLine(new string('-', 115));

        int rows = 0;
        while (await reader.ReadAsync(ct))
        {
            rows++;
            var id       = reader.GetInt64(0);
            var mkey     = reader.GetString(1);
            var mkeyShort = mkey.Length > 15 ? mkey[..12] + "..." : mkey;
            var fedepo   = reader.GetString(2);
            var claveu   = reader.GetString(3);
            var item     = reader.GetInt32(4);
            var tmov     = reader.GetString(5);
            var codcom   = reader.GetString(6);
            var idarti   = reader.GetString(7);
            var cantidad = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var status   = reader.GetString(9);

            Console.WriteLine(
                $"{id,-5} | {mkeyShort,-15} | {fedepo,-27} | {claveu,-10} | {item,-5} | {tmov,-4} | {codcom,-4} | {idarti,-10} | {cantidad,-10} | {status,-8}");
        }

        Console.WriteLine(new string('-', 115));
        Console.WriteLine($"Mostrando {rows} registros. Pending total en DB: {totalPending}");

        return 0;
    }
}
