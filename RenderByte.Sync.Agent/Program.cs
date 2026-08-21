using RenderByte.Sync.Agent;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Infrastructure.Alegon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RenderByte.Sync.Agent.Logging;
using RenderByte.Sync.Agent.Services;
// ─── Punto de entrada único de RenderByte Sync ────────────────────────────────
//
// Comandos disponibles:
//   (sin argumento)                           → health-check de conexión
//   configure                                 → configura interactivamente
//   config-check                              → valida configuración actual
//   run                                       → inicia sincronización continua
//   movements-test [fecha]                    → lee 10 movimientos desde checkpoint (M2)
//   batch-test <fecha> <batch-size> <batches> → batching incremental con cursor compuesto (M3)
//   checkpoint-test <fecha> <batch-size>      → checkpoint incremental (M4)
//   checkpoint-show                           → muestra checkpoint actual (lectura)
//   outbox-test <fecha> [batch-size]          → persiste un batch en outbox (M5)
//   outbox-show [limit]                       → muestra outbox pendiente (lectura)
//   products-sync-once                        → sync one-shot de productos
//   stocks-sync-once                          → sync one-shot de stocks
//
// Comandos mutantes (outbox-test, checkpoint-test, batch-test) requieren Single Instance Guard.
// Comandos de solo lectura (outbox-show, checkpoint-show, movements-test) no requieren mutex.
// ─────────────────────────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var command = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

if (command == "--version" || command == "-v")
{
    Console.WriteLine("RenderByte Sync 0.12.0");
    return 0;
}

if (command == "service")
{
    // Mode for SCM
    var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "RenderByteSync";
        })
        .ConfigureLogging(logging =>
        {
            logging.AddDailyRollingFile(Path.Combine(SyncPaths.GetConfigDirectory(), "Logs"));
        })
        .ConfigureServices((hostContext, services) =>
        {
            var protector = new WindowsDpapiSecretProtector();
            var resolver = new SyncConfigurationResolver(protector);
            var options = resolver.Resolve(); // Will crash service if config is fatal
            var reader = new AlegonReader(options.AlegonConnectionString);

            services.AddSingleton(options);
            services.AddSingleton(reader);
            services.AddSingleton<ISyncStatusWriter>(new SyncStatusWriter(Path.Combine(SyncPaths.GetConfigDirectory(), "status.json")));
            services.AddHostedService<RenderByteSyncWorker>();
        })
        .Build();

    await host.RunAsync();
    return 0;
}

var svcManager = new RenderByte.Sync.Agent.Services.WindowsServiceManager();

if (command == "service-install") return await ServiceInstallCommandAgent.RunAsync(svcManager, cts.Token);
if (command == "service-uninstall") return await ServiceUninstallCommandAgent.RunAsync(svcManager, cts.Token);
if (command == "service-start") return await ServiceStartCommandAgent.RunAsync(svcManager, cts.Token);
if (command == "service-stop") return await ServiceStopCommandAgent.RunAsync(svcManager, cts.Token);
if (command == "service-status") return await ServiceStatusCommandAgent.RunAsync(svcManager, cts.Token);

if (command == "configure")
{
    return await ConfigureCommandAgent.RunAsync(cts.Token);
}

if (command == "config-check")
{
    return await ConfigCheckCommandAgent.RunAsync(cts.Token);
}

// ── Configuration Resolution ──────────────────────────────────────────────────
ResolvedSyncOptions options;
try
{
    var protector = new WindowsDpapiSecretProtector();
    var resolver = new SyncConfigurationResolver(protector);
    options = resolver.Resolve();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    if (ex.Message.Contains("[CONFIG ERROR]") || ex.Message.Contains("[SECRETS ERROR]"))
    {
        Console.Error.WriteLine("\nRun:\n  RenderByte.Sync.Agent.exe configure");
    }
    return 2;
}

var connectionString = options.AlegonConnectionString;
var sourceId = options.SourceId;
var reader = new AlegonReader(connectionString);

// ── Comandos mutantes: adquirir Single Instance Guard ─────────────────────────
var mutatingCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "outbox-test", "checkpoint-test", "batch-test", "run", "products-sync-once", "stocks-sync-once" };

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
    catch (SyncPermissionException ex)
    {
        Console.Error.WriteLine($"[ERROR] {ex.Message}");
        return 4; // exit code específico: error de permisos
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
            await ContinuousRunAgent.RunAsync(
                options, 
                reader, 
                cts.Token, 
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger("RenderByte.Sync.Agent"),
                new RenderByte.Sync.Agent.Services.SyncStatusWriter(Path.Combine(SyncPaths.GetConfigDirectory(), "status.json"))
            ),

        "outbox-sync" =>
            await OutboxSyncAgent.RunAsync(sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "idempotency-test" =>
            await IdempotencyTestAgent.RunAsync(sourceId, args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "product-schema-test" =>
            await ProductSchemaTestAgent.RunAsync(new ProductSchemaReader(connectionString), cts.Token),

        "products-sync-once" =>
            await ProductsSyncOnceAgent.RunAsync(sourceId, new AlegonProductReader(connectionString), args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        "stocks-sync-once" =>
            await StocksSyncOnceAgent.RunAsync(sourceId, new AlegonStockReader(connectionString), args.Length > 1 ? args[1..] : Array.Empty<string>(), cts.Token),

        // Sin argumento o cualquier otro → health-check (comportamiento original intacto)
        _ =>
            await HealthCheckAgent.RunAsync(reader, cts.Token),
    };
}
finally
{
    guard?.Dispose();
}
