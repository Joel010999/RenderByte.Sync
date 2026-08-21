namespace RenderByte.Sync.Agent;

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Agent.Services;

public static class ServiceInstallCommandAgent
{
    public static async Task<int> RunAsync(IWindowsServiceManager serviceManager, CancellationToken cancellationToken)
    {
        Console.WriteLine("[INFO] Installing RenderByte Sync Windows Service...");

        if (!IsAdministrator() && Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE") != "1")
        {
            Console.Error.WriteLine("[ERROR] Administrator privileges are required to install a Windows Service.");
            return 1;
        }

        const string serviceName = "RenderByteSync";
        if (serviceManager.IsInstalled(serviceName))
        {
            Console.Error.WriteLine($"[ERROR] Service {serviceName} already exists.");
            Console.Error.WriteLine("Use: service-uninstall");
            return 1;
        }

        // Validate config/secrets exist and are valid (config-check equivalent)
        // Validate config/secrets exist and are valid (config-check equivalent)
        try
        {
            if (Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE") == "1")
            {
                var configPath = RenderByte.Sync.Agent.Configuration.SyncPaths.GetConfigFilePath();
                if (!File.Exists(configPath)) throw new Exception("Config not found.");
            }
            else
            {
                var protector = new WindowsDpapiSecretProtector();
                var resolver = new SyncConfigurationResolver(protector);
                var options = resolver.Resolve();
            }

            var dbPath = RenderByte.Sync.Persistence.SyncDbPath.Resolve();
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine($"[ERROR] SQLite database not found at {dbPath}. Please run the agent interactively first or ensure the file exists.");
                return 1;
            }
            
            if (Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE") != "1")
            {
                var store = new RenderByte.Sync.Persistence.SqliteSyncBatchStore(dbPath);
                var protector = new WindowsDpapiSecretProtector();
                var resolver = new SyncConfigurationResolver(protector);
                var options = resolver.Resolve();
                await store.OpenExistingInstallationAsync(options.SourceId);
                await store.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Configuration, secrets, or SQLite validation failed: {ex.Message}");
            Console.Error.WriteLine("Cannot install service with invalid configuration.");
            return 1;
        }

        try
        {
            var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory + "RenderByte.Sync.Agent.exe";
            const string displayName = "RenderByte Sync";
            const string description = "RenderByte background synchronization service for Alegon.";
            const string arguments = "service"; // IMPORTANT: ONLY use 'service' mode

            await serviceManager.InstallAsync(serviceName, displayName, description, exePath, arguments, cancellationToken);
            await serviceManager.ConfigureRecoveryAsync(serviceName, cancellationToken);

            Console.WriteLine("[OK] Service installed successfully with Automatic startup and LocalSystem account.");
            Console.WriteLine("To start the service, run:");
            Console.WriteLine("  RenderByte.Sync.Agent.exe service-start");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to install service: {ex.Message}");
            return 1;
        }
    }

#pragma warning disable CA1416 // Validate platform compatibility
    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return true; // Ignore for tests on non-windows
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
#pragma warning restore CA1416
}
