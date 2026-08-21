namespace RenderByte.Sync.Agent;

using System;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

/// <summary>
/// Excepción lanzada cuando ya existe una instancia del agente en ejecución.
/// </summary>
public sealed class SyncAlreadyRunningException(string message) : Exception(message);

/// <summary>
/// Excepción lanzada cuando hay un error de permisos intentando acceder al Mutex.
/// </summary>
public sealed class SyncPermissionException(string message) : Exception(message);

/// <summary>
/// Guard de instancia única usando Named Mutex de Windows.
/// Previene que múltiples instancias del agente muten el estado local simultáneamente.
/// </summary>
public sealed class SyncInstanceGuard : IDisposable
{
    private readonly Mutex  _mutex;
    private readonly string _mutexName;
    private          bool   _owned;

    private SyncInstanceGuard(Mutex mutex, string mutexName)
    {
        _mutex     = mutex;
        _mutexName = mutexName;
        _owned     = true;
    }

    /// <summary>Nombre completo del mutex adquirido (para diagnóstico).</summary>
    public string MutexName => _mutexName;

    /// <summary>
    /// Devuelve el nombre canónico del Mutex para el sourceId dado.
    /// </summary>
    public static string GetMutexName(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        
        // Sanitizar sourceId para nombre válido de kernel object (solo alfanuméricos y guiones)
        var safeName = new string(sourceId.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        if (OperatingSystem.IsWindows())
        {
            return $@"Global\RenderByteSync-{safeName}";
        }
        
        return $@"Local\RenderByteSync-{safeName}";
    }

    /// <summary>
    /// Intenta adquirir la instancia única para el <paramref name="sourceId"/> dado.
    /// </summary>
    public static SyncInstanceGuard AcquireOrThrow(string sourceId)
    {
        var mutexName = GetMutexName(sourceId);
        Mutex mutex;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (true)
                {
                    try
                    {
                        if (MutexAcl.TryOpenExisting(mutexName, MutexRights.Synchronize | MutexRights.Modify, out var existingMutex))
                        {
                            mutex = existingMutex!;
                            break;
                        }

                        var security = new MutexSecurity();
                        
                        // Allow LocalSystem
                        security.AddAccessRule(new MutexAccessRule(
                            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                            MutexRights.Synchronize | MutexRights.Modify,
                            AccessControlType.Allow));
                        
                        // Allow Built-in Administrators
                        security.AddAccessRule(new MutexAccessRule(
                            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                            MutexRights.Synchronize | MutexRights.Modify,
                            AccessControlType.Allow));
                        
                        // Allow Authenticated Users (so non-elevated interactive runs can wait on it and detect it's locked)
                        security.AddAccessRule(new MutexAccessRule(
                            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                            MutexRights.Synchronize | MutexRights.Modify,
                            AccessControlType.Allow));

                        mutex = MutexAcl.Create(false, mutexName, out _, security);
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Race condition: another process created the mutex between our TryOpenExisting 
                        // and Create. If we just don't have permission to create it (or to open the now-existing one 
                        // with Create's default FullControl), TryOpenExisting will either succeed (if we have 
                        // Synchronize | Modify) or throw its own UnauthorizedAccessException on the next loop 
                        // (which is then correctly surfaced below).
                        
                        // If it's a genuine permission issue that persists, TryOpenExisting will throw,
                        // breaking the loop and bubbling out to the outer catch.
                        
                        // Wait a tiny bit just to avoid tight spinning, though usually TryOpenExisting 
                        // will immediately succeed or throw.
                        Thread.Sleep(10);
                    }
                }
            }
            else
            {
                mutex = new Mutex(false, mutexName);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new SyncPermissionException(
                $"Cannot access the global RenderByte Sync instance guard. Run as Administrator or repair permissions.");
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Abandoned mutex means the previous owner crashed without releasing it.
            // Ownership is automatically transferred to us.
            Console.WriteLine("[INSTANCE GUARD] Recovered abandoned mutex.");
            acquired = true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new SyncAlreadyRunningException($"RenderByte Sync is already running for this source.");
        }

        return new SyncInstanceGuard(mutex, mutexName);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_owned)
        {
            try   { _mutex.ReleaseMutex(); }
            catch { /* Si el mutex fue abandonado, ignorar */ }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
