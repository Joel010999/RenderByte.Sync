using System.Globalization;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class CheckpointTestAgent
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    public static async Task<int> RunTestAsync(
        IAlegonReader reader,
        string sourceId,
        string[] args,
        CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Uso: RenderByte.Sync.Agent.exe checkpoint-test \"YYYY-MM-DD HH:mm:ss\" <batch-size>");
            return 1;
        }

        if (!DateTime.TryParseExact(args[0], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var initialDate))
        {
            Console.Error.WriteLine($"[ERROR] Fecha inválida. Formato esperado: {DateFormat}");
            return 1;
        }

        if (!int.TryParse(args[1], out var batchSize) || batchSize <= 0)
        {
            Console.Error.WriteLine("[ERROR] batch-size debe ser un entero > 0.");
            return 1;
        }

        Console.WriteLine("[checkpoint-test] Conectando a Alegon para detectar sucursal...");
        var branchId = await reader.GetBranchNumberAsync(ct);
        Console.WriteLine($"[checkpoint-test] Sucursal detectada: {branchId}");

        var dbPath = SyncDbPath.Resolve();
        Console.WriteLine($"[checkpoint-test] Ruta DB local: {dbPath}");

        await using var store = new SqliteSyncBatchStore(dbPath);

        try
        {
            await store.InitializeAsync(sourceId, branchId, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Fallo al inicializar store: {ex.Message}");
            return 1;
        }

        var storedData = await store.GetCheckpointAsync(ct);
        MovementCheckpoint checkpoint;

        if (storedData is null)
        {
            Console.WriteLine($"[checkpoint-test] CHECKPOINT CREADO desde {initialDate:yyyy-MM-dd HH:mm:ss}");
            checkpoint = MovementCheckpoint.Initial(initialDate);
        }
        else
        {
            Console.WriteLine("[checkpoint-test] CHECKPOINT CARGADO previamente:");
            Console.WriteLine($"                  {storedData.ToMovementCheckpoint()}");
            checkpoint = storedData.ToMovementCheckpoint();
        }

        Console.WriteLine($"[checkpoint-test] Leyendo siguiente batch (tamaño máximo: {batchSize})...");

        var batchReader = new MovementBatchReader(reader, branchId, batchSize);
        var result = await batchReader.ReadNextBatchAsync(checkpoint, ct);

        if (result.IsEmpty)
        {
            Console.WriteLine("[checkpoint-test] No hay movimientos nuevos. El checkpoint no avanza.");
            return 0;
        }

        var first = result.Movements[0];
        var last  = result.Movements[^1];

        Console.WriteLine($"│  leídos             : {result.Count}");
        Console.WriteLine($"│  primero            : fedepo={first.FechaDeposito:yyyy-MM-dd HH:mm:ss.fff}  CLAVEU={first.ClaveU}  item={first.Item}");
        Console.WriteLine($"│  último             : fedepo={last.FechaDeposito:yyyy-MM-dd HH:mm:ss.fff}  CLAVEU={last.ClaveU}  item={last.Item}");
        Console.WriteLine($"│  checkpoint salida  : {result.CheckpointAfter}");
        Console.WriteLine($"└{new string('─', 60)}");

        Console.WriteLine("[checkpoint-test] Persistiendo batch + checkpoint...");
        var persistResult = await store.PersistBatchAndCheckpointAsync(branchId, result.Movements, result.CheckpointAfter, ct);

        Console.WriteLine($"[checkpoint-test] Insertados: {persistResult.Inserted}  Duplicados: {persistResult.DuplicatesSkipped}");
        Console.WriteLine("[checkpoint-test] OK.");
        return 0;
    }

    public static async Task<int> RunShowAsync(CancellationToken ct)
    {
        var dbPath = SyncDbPath.Resolve();
        Console.WriteLine($"[checkpoint-show] Ruta DB local: {dbPath}");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine("[checkpoint-show] No existe base de datos en esta ruta.");
            return 0;
        }

        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT c.branch_id, c.fedepo, c.clave_u, c.item, c.updated_at,
                       COALESCE(i.source_id, '(no registrado)') AS source_id
                FROM sync_checkpoint c
                LEFT JOIN sync_installation i ON i.id = 1
                WHERE c.id = 1;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                Console.WriteLine("[checkpoint-show] La DB existe pero no hay ningún checkpoint guardado.");
                return 0;
            }

            Console.WriteLine($"│  source_id          : {reader.GetString(5)}");
            Console.WriteLine($"│  branch_id          : {reader.GetInt32(0)}");
            Console.WriteLine($"│  fedepo             : {reader.GetString(1)}");
            Console.WriteLine($"│  CLAVEU             : {reader.GetString(2)}");
            Console.WriteLine($"│  item               : {reader.GetInt32(3)}");
            Console.WriteLine($"│  updated_at (UTC)   : {reader.GetString(4)}");
            Console.WriteLine($"└{new string('─', 60)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] No se pudo leer el checkpoint: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
