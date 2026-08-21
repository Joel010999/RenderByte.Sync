namespace RenderByte.Sync.Agent.Services;

using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CA1416 // Validate platform compatibility

public class WindowsServiceManager : IWindowsServiceManager
{
    public virtual bool IsInstalled(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var status = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task InstallAsync(string serviceName, string displayName, string description, string exePath, string arguments, CancellationToken cancellationToken = default)
    {
        var args = new[] 
        { 
            "create", 
            serviceName, 
            "binPath=", $"\"{exePath}\" {arguments}", 
            "start=", "auto", 
            "obj=", "LocalSystem", 
            "DisplayName=", displayName 
        };
        
        await RunScCommandAsync(args, cancellationToken);
        
        if (!IsInstalled(serviceName))
        {
            throw new InvalidOperationException($"Service '{serviceName}' was not found after creation.");
        }

        try
        {
            var descArgs = new[] { "description", serviceName, description };
            await RunScCommandAsync(descArgs, cancellationToken);
            
            await ConfigureRecoveryAsync(serviceName, cancellationToken);
        }
        catch (Exception)
        {
            try { await UninstallAsync(serviceName, CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task UninstallAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        await RunScCommandAsync(new[] { "delete", serviceName }, cancellationToken);
    }

    public Task StartAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            sc.Start();
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
        {
            sc.Stop();
        }
        
        var stopwatch = Stopwatch.StartNew();
        while (sc.Status != ServiceControllerStatus.Stopped && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(500, cancellationToken);
            sc.Refresh();
        }
        
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            throw new InvalidOperationException($"Could not stop service {serviceName} within timeout.");
        }
    }

    public Task<string> GetStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        using var sc = new ServiceController(serviceName);
        return Task.FromResult(sc.Status.ToString());
    }

    public async Task ConfigureRecoveryAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var args = new[] { "failure", serviceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000" };
        await RunScCommandAsync(args, cancellationToken);
    }

    protected virtual async Task RunScCommandAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null) throw new InvalidOperationException("Failed to start sc.exe");

        await process.WaitForExitAsync(cancellationToken);
        
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe failed with exit code {process.ExitCode}. Stdout: {stdout}. Stderr: {stderr}");
        }
    }
}
