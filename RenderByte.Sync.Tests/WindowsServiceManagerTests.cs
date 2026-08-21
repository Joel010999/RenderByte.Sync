using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent.Services;
using Xunit;

namespace RenderByte.Sync.Tests;

public class WindowsServiceManagerTests
{
    private class TestableWindowsServiceManager : WindowsServiceManager
    {
        public List<string[]> ExecutedCommands { get; } = new();
        public bool SimulateIsInstalled { get; set; } = true;
        public bool SimulateCommandFailureOnDescription { get; set; } = false;

        public override bool IsInstalled(string serviceName)
        {
            return SimulateIsInstalled;
        }

        protected override Task RunScCommandAsync(string[] arguments, CancellationToken cancellationToken)
        {
            ExecutedCommands.Add(arguments);
            
            if (SimulateCommandFailureOnDescription && arguments.Length > 0 && arguments[0] == "description")
            {
                throw new InvalidOperationException("Simulated failure setting description");
            }
            
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WindowsServiceManager_Create_UsesScCompatibleStartTokenization()
    {
        var manager = new TestableWindowsServiceManager();
        await manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None);

        var createCmd = manager.ExecutedCommands.First(c => c[0] == "create");
        var startIndex = Array.IndexOf(createCmd, "start=");
        Assert.True(startIndex >= 0);
        Assert.Equal("auto", createCmd[startIndex + 1]);
    }

    [Fact]
    public async Task WindowsServiceManager_Create_UsesScCompatibleBinPathTokenization()
    {
        var manager = new TestableWindowsServiceManager();
        await manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None);

        var createCmd = manager.ExecutedCommands.First(c => c[0] == "create");
        var binPathIndex = Array.IndexOf(createCmd, "binPath=");
        Assert.True(binPathIndex >= 0);
        Assert.Equal("\"C:\\exe.exe\" arg1", createCmd[binPathIndex + 1]);
    }

    [Fact]
    public async Task WindowsServiceManager_Create_UsesScCompatibleObjTokenization()
    {
        var manager = new TestableWindowsServiceManager();
        await manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None);

        var createCmd = manager.ExecutedCommands.First(c => c[0] == "create");
        var objIndex = Array.IndexOf(createCmd, "obj=");
        Assert.True(objIndex >= 0);
        Assert.Equal("LocalSystem", createCmd[objIndex + 1]);
    }

    [Fact]
    public async Task WindowsServiceManager_Description_UsesArgumentListCorrectly()
    {
        var manager = new TestableWindowsServiceManager();
        await manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None);

        var descCmd = manager.ExecutedCommands.First(c => c[0] == "description");
        Assert.Equal("description", descCmd[0]);
        Assert.Equal("MyService", descCmd[1]);
        Assert.Equal("My Desc", descCmd[2]);
    }

    [Fact]
    public async Task WindowsServiceManager_Recovery_UsesScCompatibleTokenization()
    {
        var manager = new TestableWindowsServiceManager();
        await manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None);

        var failCmd = manager.ExecutedCommands.First(c => c[0] == "failure");
        
        var resetIndex = Array.IndexOf(failCmd, "reset=");
        Assert.True(resetIndex >= 0);
        Assert.Equal("86400", failCmd[resetIndex + 1]);

        var actionsIndex = Array.IndexOf(failCmd, "actions=");
        Assert.True(actionsIndex >= 0);
        Assert.Equal("restart/60000/restart/60000/restart/60000", failCmd[actionsIndex + 1]);
    }

    [Fact]
    public async Task ServiceInstall_RollsBackIfPostCreateConfigurationFails()
    {
        var manager = new TestableWindowsServiceManager
        {
            SimulateCommandFailureOnDescription = true
        };
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None));
            
        var deleteCmd = manager.ExecutedCommands.FirstOrDefault(c => c[0] == "delete");
        Assert.NotNull(deleteCmd);
        Assert.Equal("MyService", deleteCmd[1]);
    }

    [Fact]
    public async Task ServiceInstall_VerifiesServiceExistsAfterCreate()
    {
        var manager = new TestableWindowsServiceManager
        {
            SimulateIsInstalled = false // simulate create succeeds but service doesn't appear
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            manager.InstallAsync("MyService", "My Display Name", "My Desc", "C:\\exe.exe", "arg1", CancellationToken.None));
            
        Assert.Contains("was not found after creation", ex.Message);
    }
}
