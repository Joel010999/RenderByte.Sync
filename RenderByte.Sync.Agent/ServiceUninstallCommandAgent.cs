namespace RenderByte.Sync.Agent;

using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Services;

public static class ServiceUninstallCommandAgent
{
    public static async Task<int> RunAsync(IWindowsServiceManager serviceManager, CancellationToken cancellationToken)
    {
        Console.WriteLine("[INFO] Uninstalling RenderByte Sync Windows Service...");

        if (!IsAdministrator() && Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE") != "1")
        {
            Console.Error.WriteLine("[ERROR] Administrator privileges are required to uninstall a Windows Service.");
            return 1;
        }

        const string serviceName = "RenderByteSync";
        if (!serviceManager.IsInstalled(serviceName))
        {
            Console.WriteLine($"[INFO] Service {serviceName} is not installed.");
            return 0;
        }

        try
        {
            Console.WriteLine("[INFO] Stopping service if running...");
            await serviceManager.StopAsync(serviceName, TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not stop service or it was not running: {ex.Message}");
        }

        try
        {
            await serviceManager.UninstallAsync(serviceName, cancellationToken);
            Console.WriteLine("[OK] Service uninstalled successfully.");
            Console.WriteLine("[INFO] NOTE: ProgramData configuration, secrets, and SQLite database were NOT deleted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to uninstall service: {ex.Message}");
            return 1;
        }
    }

#pragma warning disable CA1416 // Validate platform compatibility
    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
#pragma warning restore CA1416
}
