using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Persistence;

namespace RenderByte.Sync.Agent;

public static class ConfigCheckCommandAgent
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("RenderByte Sync Config Check");
        Console.WriteLine("----------------------------");

        var configPath = SyncPaths.GetConfigFilePath();
        var secretsPath = SyncPaths.GetSecretsFilePath();

        if (File.Exists(configPath))
            Console.WriteLine($"[CONFIG] file: OK ({configPath})");
        else
            Console.WriteLine($"[CONFIG] file: MISSING ({configPath})");

        if (File.Exists(secretsPath))
            Console.WriteLine($"[SECRETS] file: OK ({secretsPath})");
        else
            Console.WriteLine($"[SECRETS] file: MISSING ({secretsPath})");

        var protector = new WindowsDpapiSecretProtector();
        var resolver = new SyncConfigurationResolver(protector, configPath, secretsPath);

        ResolvedSyncOptions options;
        try
        {
            options = resolver.Resolve();
            Console.WriteLine("[CONFIG] resolution: OK");
            Console.WriteLine("[SECRETS] DPAPI decryption: OK");
            Console.WriteLine("[API CONFIG] OK");
            Console.WriteLine("[SQL CONFIG] OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Falló la resolución de configuración: {ex.Message}");
            return 1;
        }

        try
        {
            var dbPath = SyncDbPath.Resolve();
            await using var store = new SqliteSyncBatchStore(dbPath);
            await store.OpenExistingInstallationAsync(options.SourceId, cancellationToken);
            Console.WriteLine("[SOURCE] SQLite match: OK");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("[SOURCE MISMATCH]"))
        {
            Console.WriteLine($"[ERROR] [SOURCE MISMATCH] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] No se pudo verificar la instalación SQLite (puede que sea primera ejecución): {ex.Message}");
        }

        Console.WriteLine("\n[OK] Config check passed.");
        return 0;
    }
}
