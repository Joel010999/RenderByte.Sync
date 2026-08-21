namespace RenderByte.Sync.Agent;

using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Services;

public static class ServiceStartCommandAgent
{
    public static async Task<int> RunAsync(IWindowsServiceManager serviceManager, CancellationToken cancellationToken)
    {
        Console.WriteLine("[INFO] Starting RenderByte Sync Windows Service...");

        if (!IsAdministrator() && Environment.GetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE") != "1")
        {
            Console.Error.WriteLine("[ERROR] Administrator privileges are required to start the Windows Service.");
            return 1;
        }

        const string serviceName = "RenderByteSync";
        if (!serviceManager.IsInstalled(serviceName))
        {
            Console.Error.WriteLine($"[ERROR] Service {serviceName} is not installed.");
            return 1;
        }

        try
        {
            await serviceManager.StartAsync(serviceName, cancellationToken);
            Console.WriteLine("[OK] Service start request sent.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to start service: {ex.Message}");
            return 1;
        }
    }

#pragma warning disable CA1416
    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
#pragma warning restore CA1416
}
