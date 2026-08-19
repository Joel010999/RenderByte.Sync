using RenderByte.Sync.Agent;
using RenderByte.Sync.Infrastructure.Alegon;

// ─── Punto de entrada único de RenderByte Sync ────────────────────────────────
//
// Variables de entorno requeridas:
//   RENDERBYTE_ALEGON_CONNECTION_STRING  → cadena de conexión a Alegon SQL Server
//   RENDERBYTE_SYNC_SOURCE_ID            → identificador único de esta instalación
//
// Comandos disponibles:
//   (sin argumento)                           → health-check de conexión
//   movements-test [fecha]                    → lee 10 movimientos desde checkpoint (M2)
//   batch-test <fecha> <batch-size> <batches> → batching incremental con cursor compuesto (M3)
//   checkpoint-test <fecha> <batch-size>      → checkpoint incremental (M4)
//   checkpoint-show                           → muestra checkpoint actual (lectura)
//   outbox-test <fecha> [batch-size]          → persiste un batch en outbox (M5)
//   outbox-show [limit]                       → muestra outbox pendiente (lectura)
//
// Comandos mutantes (outbox-test, checkpoint-test, batch-test) requieren Single Instance Guard.
// Comandos de solo lectura (outbox-show, checkpoint-show, movements-test) no requieren mutex.
// ─────────────────────────────────────────────────────────────────────────────

// ── Alegon connection string ──────────────────────────────────────────────────
const string alegonEnvVar = "RENDERBYTE_ALEGON_CONNECTION_STRING";
var connectionString = Environment.GetEnvironmentVariable(alegonEnvVar);

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"[ERROR] Falta la variable de entorno {alegonEnvVar}.");
    Console.Error.WriteLine($"        Ejemplo: set {alegonEnvVar}=Server=.;Integrated Security=true");
    return 2;
}

// ── Source ID ─────────────────────────────────────────────────────────────────
const string sourceIdEnvVar = "RENDERBYTE_SYNC_SOURCE_ID";
var sourceId = Environment.GetEnvironmentVariable(sourceIdEnvVar);

if (string.IsNullOrWhiteSpace(sourceId))
{
    Console.Error.WriteLine($"[ERROR] Falta la variable de entorno {sourceIdEnvVar}.");
    Console.Error.WriteLine($"        Este identificador debe ser único por instalación de RenderByte Sync.");
    Console.Error.WriteLine($"        No se genera automáticamente para evitar identidades inconsistentes.");
    Console.Error.WriteLine($"        Ejemplo: set {sourceIdEnvVar}=CLIENTEA-SUCURSAL-2");
    return 2;
}

// ── CancellationToken ─────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var reader  = new AlegonReader(connectionString);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

// ── Comandos mutantes: adquirir Single Instance Guard ─────────────────────────
var mutatingCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "outbox-test", "checkpoint-test", "batch-test", "run", "products-sync-once" };

SyncInstanceGuard? guard = null;
if (mutatingCommands.Contains(command))
{
    try
    {
        guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
    }
    catch (SyncAlreadyRunningException ex)
    {
        Console.Error.WriteLine($"[ERROR] {ex.Message}");
        return 3; // exit code específico: instancia duplicada
    }
}

try
{
    return command switch
    {
        "movements-test" =>
            await MovementsTestAgent.RunAsync(reader, args.Length > 1 ? args[1] : null, cts.Token),

        "batch-test" =>
            await BatchTestAgent.RunAsync(reader, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "checkpoint-test" =>
            await CheckpointTestAgent.RunTestAsync(reader, sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "checkpoint-show" =>
            await CheckpointTestAgent.RunShowAsync(cts.Token),

        "outbox-test" =>
            await OutboxTestAgent.RunTestAsync(reader, sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "outbox-show" =>
            await OutboxTestAgent.RunShowAsync(args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "run" =>
            await ContinuousRunAgent.RunAsync(SyncAgentOptions.FromEnvironment(), reader, cts.Token),

        "outbox-sync" =>
            await OutboxSyncAgent.RunAsync(sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "idempotency-test" =>
            await IdempotencyTestAgent.RunAsync(sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "product-schema-test" =>
            await ProductSchemaTestAgent.RunAsync(new ProductSchemaReader(connectionString), cts.Token),

        "products-sync-once" =>
            await ProductsSyncOnceAgent.RunAsync(sourceId, new AlegonProductReader(connectionString), args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        // Sin argumento o cualquier otro → health-check (comportamiento original intacto)
        _ =>
            await HealthCheckAgent.RunAsync(reader, cts.Token),
    };
}
finally
{
    guard?.Dispose();
}
