namespace RenderByte.Sync.Agent.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class FakeWindowsServiceManager : IWindowsServiceManager
{
    private class FakeService
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Status { get; set; } = "Stopped";
        public bool HasRecovery { get; set; }
    }

    private readonly Dictionary<string, FakeService> _services = new(StringComparer.OrdinalIgnoreCase);

    public bool IsInstalled(string serviceName)
    {
        return _services.ContainsKey(serviceName);
    }

    public Task InstallAsync(string serviceName, string displayName, string description, string exePath, string arguments, CancellationToken cancellationToken = default)
    {
        if (_services.ContainsKey(serviceName))
            throw new InvalidOperationException($"Service {serviceName} already exists.");

        _services[serviceName] = new FakeService
        {
            Name = serviceName,
            DisplayName = displayName,
            Description = description,
            ExePath = exePath,
            Arguments = arguments,
            Status = "Stopped"
        };
        return Task.CompletedTask;
    }

    public Task UninstallAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        _services.Remove(serviceName);
        return Task.CompletedTask;
    }

    public Task StartAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(serviceName, out var svc))
        {
            svc.Status = "Running";
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Service not found.");
    }

    public Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(serviceName, out var svc))
        {
            svc.Status = "Stopped";
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Service not found.");
    }

    public Task<string> GetStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(serviceName, out var svc))
        {
            return Task.FromResult(svc.Status);
        }
        throw new InvalidOperationException("Service not found.");
    }

    public Task ConfigureRecoveryAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(serviceName, out var svc))
        {
            svc.HasRecovery = true;
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Service not found.");
    }

    public string? GetRecordedArguments(string serviceName)
    {
        return _services.TryGetValue(serviceName, out var svc) ? svc.Arguments : null;
    }

    public string? GetRecordedExePath(string serviceName)
    {
        return _services.TryGetValue(serviceName, out var svc) ? svc.ExePath : null;
    }
}
