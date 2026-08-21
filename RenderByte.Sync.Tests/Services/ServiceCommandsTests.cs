namespace RenderByte.Sync.Tests.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent;
using RenderByte.Sync.Agent.Configuration;
using RenderByte.Sync.Agent.Services;
using Xunit;

[Collection("EnvVars")]
public class ServiceCommandsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _configPath;
    private readonly string _secretsPath;
    private readonly string _dbPath;

    public ServiceCommandsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
        _configPath = Path.Combine(_testDir, "config.json");
        _secretsPath = Path.Combine(_testDir, "secrets.json");
        _dbPath = Path.Combine(_testDir, "sync.db");

        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_CONFIG_PATH", _configPath);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", _dbPath);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE", "1");
        File.WriteAllText(_configPath, "{\"SourceId\":\"" + Guid.NewGuid() + "\", \"ApiUrl\":\"https://api\"}");
        File.WriteAllText(_secretsPath, "{\"Version\":1}");
        File.WriteAllText(_dbPath, "fake sqlite");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_DB", null);
        Environment.SetEnvironmentVariable("RENDERBYTE_SYNC_TEST_MODE", null);
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
    }

    [Fact]
    public async Task ServiceInstall_UsesAbsoluteCurrentExecutablePath()
    {
        var mgr = new FakeWindowsServiceManager();
        var result = await ServiceInstallCommandAgent.RunAsync(mgr, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.True(mgr.IsInstalled("RenderByteSync"));
        Assert.NotNull(mgr.GetRecordedExePath("RenderByteSync"));
        Assert.True(Path.IsPathRooted(mgr.GetRecordedExePath("RenderByteSync")));
    }

    [Fact]
    public async Task ServiceInstall_UsesServiceModeArgument()
    {
        var mgr = new FakeWindowsServiceManager();
        await ServiceInstallCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal("service", mgr.GetRecordedArguments("RenderByteSync"));
    }

    [Fact]
    public async Task ServiceInstall_FailsWhenConfigInvalid()
    {
        File.Delete(_configPath);
        var mgr = new FakeWindowsServiceManager();
        var result = await ServiceInstallCommandAgent.RunAsync(mgr, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.False(mgr.IsInstalled("RenderByteSync"));
    }

    [Fact]
    public async Task ServiceInstall_FailsWhenServiceAlreadyExists()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        var result = await ServiceInstallCommandAgent.RunAsync(mgr, CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ServiceUninstall_WhenNotInstalled_IsSafe()
    {
        var mgr = new FakeWindowsServiceManager();
        var result = await ServiceUninstallCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ServiceUninstall_StopsRunningServiceBeforeDelete()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        await mgr.StartAsync("RenderByteSync");

        var result = await ServiceUninstallCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.False(mgr.IsInstalled("RenderByteSync"));
    }

    [Fact]
    public async Task ServiceUninstall_DoesNotDeleteProgramData()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        
        await ServiceUninstallCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.True(File.Exists(_configPath));
        Assert.True(File.Exists(_secretsPath));
    }

    [Fact]
    public async Task ServiceStart_StartsInstalledService()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        var result = await ServiceStartCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.Equal("Running", await mgr.GetStatusAsync("RenderByteSync"));
    }

    [Fact]
    public async Task ServiceStop_StopsInstalledService()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        await mgr.StartAsync("RenderByteSync");
        var result = await ServiceStopCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.Equal("Stopped", await mgr.GetStatusAsync("RenderByteSync"));
    }

    [Fact]
    public async Task ServiceStatus_ReturnsCurrentState()
    {
        var mgr = new FakeWindowsServiceManager();
        await mgr.InstallAsync("RenderByteSync", "d", "d", "e", "a");
        var result = await ServiceStatusCommandAgent.RunAsync(mgr, CancellationToken.None);
        Assert.Equal(0, result);
    }
}
