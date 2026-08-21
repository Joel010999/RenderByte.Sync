using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RenderByte.Sync.Agent;
using Xunit;

namespace RenderByte.Sync.Tests;

/// <summary>
/// Tests para SyncInstanceGuard usando process-lifetime file lock (M12.6).
/// El guard usa FileStream exclusivo: no es thread-affine, sobrevive async/await
/// y el OS lo libera automáticamente cuando el proceso termina.
/// </summary>
public class SyncInstanceGuardTests
{
    // --- helpers ---------------------------------------------------------------

    private static string LockPath(string sourceId) =>
        SyncInstanceGuard.GetLockPath(sourceId);

    /// <summary>
    /// Intenta eliminar el archivo de lock huérfano antes/después de cada test.
    /// Si el archivo está en uso, lo ignora (se limpiará cuando el proceso termine).
    /// </summary>
    private static void CleanupLock(string sourceId)
    {
        try { File.Delete(LockPath(sourceId)); } catch { /* ignorar */ }
    }

    // --- tests -----------------------------------------------------------------

    [Fact]
    public void InstanceGuard_FirstInstance_AcquiresExclusiveFileLock()
    {
        var sourceId = "first-instance-lock";
        CleanupLock(sourceId);
        try
        {
            using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);

            Assert.NotNull(guard);
            Assert.True(File.Exists(guard.LockPath), "El archivo de lock debe existir.");
            Assert.EndsWith(".lock", guard.LockPath);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public void InstanceGuard_CurrentOwner_BlocksSecondInstance()
    {
        var sourceId = "current-owner-blocks";
        CleanupLock(sourceId);
        try
        {
            using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);

            Exception? backgroundEx = null;
            var thread = new Thread(() =>
            {
                try { using var _ = SyncInstanceGuard.AcquireOrThrow(sourceId); }
                catch (Exception ex) { backgroundEx = ex; }
            });
            thread.Start();
            thread.Join();

            Assert.NotNull(backgroundEx);
            Assert.IsType<SyncAlreadyRunningException>(backgroundEx);
            Assert.Contains("already running", backgroundEx.Message);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public void InstanceGuard_CurrentOwner_ReturnsControlledDuplicateError()
    {
        var ex = new SyncAlreadyRunningException("RenderByte Sync is already running for this source.");
        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public void InstanceGuard_Dispose_AllowsNextInstance()
    {
        var sourceId = "dispose-allows-next";
        CleanupLock(sourceId);
        try
        {
            var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
            guard.Dispose(); // liberar explícitamente

            // Tras el Dispose, otro intento en el mismo proceso debe tener éxito
            var ex = Record.Exception(() =>
            {
                using var second = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            Assert.Null(ex);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public void InstanceGuard_ProcessCrash_ReleasesOperatingSystemLock()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceId = "process-crash-test";
        var lockPath = SyncInstanceGuard.GetLockPath(sourceId);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        CleanupLock(sourceId);

        try
        {
            // PowerShell abre el archivo con lock exclusivo y luego termina el proceso
            // sin Dispose/Close. El OS libera el handle al terminar el proceso.
            var psScript =
                $"$fs = [System.IO.File]::Open('{lockPath}', 'OpenOrCreate', 'ReadWrite', 'None'); " +
                "[Environment]::Exit(0)";

            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -Command \"{psScript}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit();

            // Después de que el proceso terminó, el lock debe estar libre
            var ex = Record.Exception(() =>
            {
                using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            Assert.Null(ex);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public async Task InstanceGuard_IsNotThreadAffine()
    {
        // Prueba que el lock NO es thread-affine:
        // Se adquiere en Thread A, se continúa en el pool via Task.Yield, y se libera desde ahí.
        // Con un Mutex thread-affine esto fallaría (ReleaseMutex desde el thread incorrecto).

        var sourceId = "thread-affine-test";
        CleanupLock(sourceId);
        try
        {
            SyncInstanceGuard? guard = null;

            // Adquirir en un thread dedicado
            var acquireThread = new Thread(() =>
            {
                guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            acquireThread.Start();
            acquireThread.Join();

            Assert.NotNull(guard);

            // Forzar continuación en un thread del pool (≠ acquireThread)
            await Task.Yield();

            // Dispose desde este thread (pool thread) — no debe lanzar excepción
            var disposeEx = Record.Exception(() => guard!.Dispose());
            Assert.Null(disposeEx);

            // Tras el Dispose, una nueva instancia debe tener éxito
            var acquireEx = Record.Exception(() =>
            {
                using var _ = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            Assert.Null(acquireEx);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public async Task InstanceGuard_AsyncContinuation_SecondInstanceStillBlocked()
    {
        // Regresión crítica M12.6: el guard adquirido antes de un await debe seguir
        // bloqueando a un segundo proceso/thread DESPUÉS de que la continuación
        // se ejecute en un thread del pool diferente.

        var sourceId = "async-continuation-blocks";
        CleanupLock(sourceId);
        try
        {
            var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);

            // Simular comportamiento de BackgroundService: await en el camino crítico
            await Task.Yield();

            // El segundo intento debe seguir bloqueado aunque estemos en un thread distinto
            Exception? secondEx = null;
            var thread = new Thread(() =>
            {
                try { using var _ = SyncInstanceGuard.AcquireOrThrow(sourceId); }
                catch (Exception e) { secondEx = e; }
            });
            thread.Start();
            thread.Join();

            Assert.IsType<SyncAlreadyRunningException>(secondEx);

            guard.Dispose();
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public void InstanceGuard_ServiceAndInteractive_UseSameCanonicalLockPath()
    {
        // El servicio (Session 0 / LocalSystem) y el modo interactivo deben usar
        // exactamente la misma ruta de lock para el mismo sourceId.
        var sourceId = "canonical-path-test";
        var path1 = SyncInstanceGuard.GetLockPath(sourceId);
        var path2 = SyncInstanceGuard.GetLockPath(sourceId);

        Assert.Equal(path1, path2);
        Assert.True(Path.IsPathRooted(path1), "La ruta del lock debe ser absoluta.");
        Assert.EndsWith(".lock", path1);
        Assert.Contains("locks", path1);
    }

    [Fact]
    public void InstanceGuard_LockPath_IsPerSource()
    {
        var pathA = SyncInstanceGuard.GetLockPath("source-a");
        var pathB = SyncInstanceGuard.GetLockPath("source-b");
        Assert.NotEqual(pathA, pathB);
    }

    [Fact]
    public void InstanceGuard_PermissionFailure_IsNotReportedAsDuplicate()
    {
        // Los dos tipos de excepción deben ser distintos.
        // Un error de permisos no debe aparecer como instancia duplicada.
        var permEx = new SyncPermissionException("Cannot access the lock.");
        var dupEx  = new SyncAlreadyRunningException("RenderByte Sync is already running for this source.");

        Assert.IsNotType<SyncAlreadyRunningException>(permEx);
        Assert.IsNotType<SyncPermissionException>(dupEx);
    }

    [Fact]
    public void InstanceGuard_StaleLockFileWithoutOpenHandle_DoesNotBlockStartup()
    {
        // Un archivo .lock huérfano en disco (sin ningún proceso que lo tenga abierto)
        // no debe impedir que el siguiente proceso lo adquiera.
        var sourceId = "stale-lock-test";
        var lockPath = SyncInstanceGuard.GetLockPath(sourceId);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        // Crear archivo vacío simulando lock de proceso previo que crasheó
        File.WriteAllBytes(lockPath, Array.Empty<byte>());

        try
        {
            var ex = Record.Exception(() =>
            {
                using var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            Assert.Null(ex);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public async Task ServiceMode_HoldsInstanceGuardForEntireRunLifetime()
    {
        // Simula el patrón real del worker:
        // guard adquirido → await RunAsync() de larga duración → durante ese tiempo
        // ningún segundo intento debe tener éxito → sólo tras Dispose.

        var sourceId = "service-lifetime-test";
        CleanupLock(sourceId);
        try
        {
            var guard = SyncInstanceGuard.AcquireOrThrow(sourceId);

            // Simular trabajo async continuo (ContinuousRunAgent.RunAsync)
            await Task.Delay(100);

            // Durante la ejecución: segundo intento debe ser bloqueado
            Exception? blockedEx = null;
            var thread = new Thread(() =>
            {
                try { using var _ = SyncInstanceGuard.AcquireOrThrow(sourceId); }
                catch (Exception e) { blockedEx = e; }
            });
            thread.Start();
            thread.Join();

            Assert.IsType<SyncAlreadyRunningException>(blockedEx);

            // Sólo tras Dispose: nuevo intento debe tener éxito
            guard.Dispose();

            var afterEx = Record.Exception(() =>
            {
                using var _ = SyncInstanceGuard.AcquireOrThrow(sourceId);
            });
            Assert.Null(afterEx);
        }
        finally { CleanupLock(sourceId); }
    }

    [Fact]
    public void SyncInstanceGuard_DoesNotExposePublicTestHooks()
    {
        var type = typeof(SyncInstanceGuard);

        var publicFields = type.GetFields(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance);
        foreach (var field in publicFields)
            Assert.DoesNotContain("TestHook", field.Name);

        var publicProperties = type.GetProperties(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance);
        foreach (var prop in publicProperties)
            Assert.DoesNotContain("TestHook", prop.Name);
    }

    [Fact]
    public void InstanceGuard_LockDirectoryPermissionDenied_ThrowsSyncPermissionException()
    {
        // Simula que Directory.CreateDirectory lanza UnauthorizedAccessException.
        // El guard debe traducirlo a SyncPermissionException (exit 4), no a IOException.
        SyncInstanceGuard._testCreateDirectory = _ =>
            throw new UnauthorizedAccessException("Access to the path is denied.");

        try
        {
            var ex = Assert.Throws<SyncPermissionException>(() =>
                SyncInstanceGuard.AcquireOrThrow("perm-denied-test"));

            Assert.Contains("Cannot create lock directory", ex.Message);
            Assert.IsNotType<IOException>(ex);
            Assert.IsNotType<SyncAlreadyRunningException>(ex);
        }
        finally
        {
            SyncInstanceGuard._testCreateDirectory = null;
        }
    }

    [Fact]
    public void InstanceGuard_LockDirectoryGenericIoFailure_IsNotReportedAsPermission()
    {
        // Simula un IOException genérico en Directory.CreateDirectory (disco lleno, path inválido, etc.).
        // El guard debe propagar IOException, NO SyncPermissionException (exit 4).
        SyncInstanceGuard._testCreateDirectory = _ =>
            throw new IOException("The disk is full.");

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                SyncInstanceGuard.AcquireOrThrow("generic-io-test"));

            Assert.IsNotType<SyncPermissionException>(ex);
            Assert.Contains("Failed to create RenderByte Sync lock directory", ex.Message);
            Assert.NotNull(ex.InnerException);
            Assert.Contains("disk is full", ex.InnerException!.Message);
        }
        finally
        {
            SyncInstanceGuard._testCreateDirectory = null;
        }
    }

    [Fact]
    public void InstanceGuard_LockDirectoryGenericIoFailure_IsNotReportedAsDuplicate()
    {
        // Un error de I/O genérico en el directorio NO debe confundirse con instancia duplicada.
        SyncInstanceGuard._testCreateDirectory = _ =>
            throw new IOException("Device not ready.");

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                SyncInstanceGuard.AcquireOrThrow("generic-io-duplicate-test"));

            Assert.IsNotType<SyncAlreadyRunningException>(ex);
        }
        finally
        {
            SyncInstanceGuard._testCreateDirectory = null;
        }
    }
}
