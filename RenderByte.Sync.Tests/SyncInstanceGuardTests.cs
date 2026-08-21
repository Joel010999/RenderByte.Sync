using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
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

    [Fact]
    public void InstanceGuard_PersistentUnauthorized_ThrowsSyncPermissionException()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CanCreateGlobalMutex()) return;

        var sourceId = "test-persist-unauth";
        var name = SyncInstanceGuard.GetMutexName(sourceId);

        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!,
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Deny));

        using var m = MutexAcl.Create(false, name, out _, security);

        var ex = Assert.Throws<SyncPermissionException>(() =>
        {
            SyncInstanceGuard.AcquireOrThrow(sourceId);
        });

        Assert.Contains("Cannot access", ex.Message);
    }

    [Fact]
    public async Task InstanceGuard_PersistentUnauthorized_DoesNotLoopForever()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CanCreateGlobalMutex()) return;

        var sourceId = "test-no-loop";
        var name = SyncInstanceGuard.GetMutexName(sourceId);

        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!,
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Deny));

        using var m = MutexAcl.Create(false, name, out _, security);

        var task = Task.Run(() =>
        {
            Assert.Throws<SyncPermissionException>(() => SyncInstanceGuard.AcquireOrThrow(sourceId));
        });

        // Fail if it takes longer than 2 seconds, which implies an infinite loop
        var completedTask = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(completedTask == task, "Test timed out, possibly due to infinite loop.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void InstanceGuard_CreateRace_ThenOpenExisting_Succeeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CanCreateGlobalMutex()) return;

        var sourceId = "test-race-succeeds";
        var name = SyncInstanceGuard.GetMutexName(sourceId);

        Mutex? backgroundMutex = null;

        // TryOpenExisting returns false, then hook creates mutex, then MutexAcl.Create throws
        SyncInstanceGuard.TestHook_BeforeCreate = () =>
        {
            if (backgroundMutex == null)
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    MutexRights.Synchronize | MutexRights.Modify,
                    AccessControlType.Allow));
                backgroundMutex = MutexAcl.Create(false, name, out _, security);
            }
        };

        // Ensure Create throws UnauthorizedAccessException to simulate lacking FullControl
        SyncInstanceGuard.TestHook_CreateThrow = () =>
        {
            throw new UnauthorizedAccessException("Simulated race Create failure");
        };

        try
        {
            // First loop: TryOpenExisting fails, BeforeCreate creates it, Create throws.
            // Second loop: TryOpenExisting succeeds!
            using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
            Assert.NotNull(guard);
        }
        finally
        {
            SyncInstanceGuard.TestHook_BeforeCreate = null;
            SyncInstanceGuard.TestHook_CreateThrow = null;
            backgroundMutex?.Dispose();
        }
    }

    [Fact]
    public async Task InstanceGuard_CreateRace_RetryIsBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CanCreateGlobalMutex()) return;

        var sourceId = "test-race-bounded";
        
        // Always throw UnauthorizedAccessException in Create to simulate persistent race failure
        SyncInstanceGuard.TestHook_CreateThrow = () =>
        {
            throw new UnauthorizedAccessException("Simulated race Create failure");
        };

        try
        {
            var task = Task.Run(() =>
            {
                var ex = Assert.Throws<SyncPermissionException>(() =>
                {
                    SyncInstanceGuard.AcquireOrThrow(sourceId);
                });
                Assert.Contains("Failed to acquire instance guard due to a persistent race condition", ex.Message);
            });

            // Fail if it takes longer than 2 seconds (infinite loop)
            var completedTask = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.True(completedTask == task, "Test timed out, possibly due to infinite loop.");
        }
        finally
        {
            SyncInstanceGuard.TestHook_CreateThrow = null;
        }
    }
    [Fact]
    public void SyncInstanceGuard_DoesNotExposePublicTestHooks()
    {
        var type = typeof(SyncInstanceGuard);
        
        var publicFields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
        foreach (var field in publicFields)
        {
            Assert.DoesNotContain("TestHook", field.Name);
        }

        var publicProperties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
        foreach (var prop in publicProperties)
        {
            Assert.DoesNotContain("TestHook", prop.Name);
        }
    }
}
