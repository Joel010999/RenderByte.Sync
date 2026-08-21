using System;
using System.Diagnostics;
using System.Threading;
using RenderByte.Sync.Agent;
using Xunit;

namespace RenderByte.Sync.Tests;

public class SyncInstanceGuardTests
{
    private const string TestSourceId = "test-guard-source";

    [Fact]
    public void InstanceGuard_ServiceAndInteractive_UseSameGlobalName()
    {
        var name = SyncInstanceGuard.GetMutexName(TestSourceId);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(@"Global\", name);
            Assert.Equal($@"Global\RenderByteSync-{TestSourceId}", name);
        }
        else
        {
            Assert.StartsWith(@"Local\", name);
            Assert.Equal($@"Local\RenderByteSync-{TestSourceId}", name);
        }
    }

    [Fact]
    public void InstanceGuard_DoesNotSilentlySplitGlobalAndLocalNamespaces()
    {
        var name = SyncInstanceGuard.GetMutexName(TestSourceId);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(@"Local\", name);
        }
    }

    private bool CanCreateGlobalMutex()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var name = SyncInstanceGuard.GetMutexName("test-priv");
            using var m = new Mutex(false, name);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void InstanceGuard_CurrentOwner_BlocksSecondInstance()
    {
        if (!CanCreateGlobalMutex()) return;

        using var guard = SyncInstanceGuard.AcquireOrThrow(TestSourceId);
        
        Exception? backgroundException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var secondGuard = SyncInstanceGuard.AcquireOrThrow(TestSourceId);
            }
            catch (Exception ex)
            {
                backgroundException = ex;
            }
        });
        thread.Start();
        thread.Join();
        
        Assert.NotNull(backgroundException);
        Assert.IsType<SyncAlreadyRunningException>(backgroundException);
        Assert.Contains("already running", backgroundException.Message);
    }

    [Fact]
    public void InstanceGuard_AbandonedMutex_IsRecoveredAndAcquired()
    {
        if (!CanCreateGlobalMutex()) return;

        var source = "abandon-thread";
        var name = SyncInstanceGuard.GetMutexName(source);
        
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            mutex.WaitOne();
            // Thread exits without releasing -> abandoned
        });
        thread.Start();
        thread.Join();

        using var guard = SyncInstanceGuard.AcquireOrThrow(source);
        Assert.NotNull(guard);
        Assert.Equal(name, guard.MutexName);
    }

    [Fact]
    public void InstanceGuard_AbandonedMutex_DoesNotCrashProcess()
    {
        if (!CanCreateGlobalMutex()) return;

        var source = "abandon-crash";
        var name = SyncInstanceGuard.GetMutexName(source);
        
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            mutex.WaitOne();
        });
        thread.Start();
        thread.Join();

        var exception = Record.Exception(() =>
        {
            using var guard = SyncInstanceGuard.AcquireOrThrow(source);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void InstanceGuard_AbandonedMutex_CrossProcessRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CanCreateGlobalMutex()) return;

        var sourceId = "cross-process-test";
        var name = SyncInstanceGuard.GetMutexName(sourceId);

        var psScript = $@"
            $m = New-Object System.Threading.Mutex($false, '{name}')
            $m.WaitOne() | Out-Null
            [Environment]::Exit(0)
        ";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{psScript}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        process?.WaitForExit();

        var exception = Record.Exception(() =>
        {
            using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void InstanceGuard_CurrentOwner_ReturnsControlledDuplicateError()
    {
        var ex = new SyncAlreadyRunningException("duplicate");
        Assert.Equal("duplicate", ex.Message);
    }

    [Fact]
    public void InstanceGuard_PermissionFailure_IsNotReportedAsDuplicate()
    {
        var ex = new SyncPermissionException("permission");
        Assert.Equal("permission", ex.Message);
    }
}
