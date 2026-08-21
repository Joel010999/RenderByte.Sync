namespace RenderByte.Sync.Agent;

using System;
using System.IO;
using System.Linq;
using RenderByte.Sync.Agent.Configuration;

/// <summary>
/// Excepción lanzada cuando ya existe una instancia del agente en ejecución.
/// </summary>
public sealed class SyncAlreadyRunningException(string message) : Exception(message);

/// <summary>
/// Excepción lanzada cuando hay un error de permisos intentando acceder al lock de instancia.
/// </summary>
public sealed class SyncPermissionException(string message) : Exception(message);

/// <summary>
/// Guard de instancia única usando un <see cref="FileStream"/> exclusivo como lock de proceso.
///
/// El lock pertenece al proceso (no al thread), por lo que:
/// - Sobrevive async/await con continuaciones en threads arbitrarios del pool.
/// - Funciona entre Session 0 (LocalSystem) y sesiones interactivas.
/// - El OS lo libera automáticamente cuando el proceso termina por cualquier causa
///   (normal, Ctrl+C, crash, kill), sin necesidad de Dispose explícito.
/// - Un archivo .lock huérfano en disco sin handle abierto no bloquea el inicio.
/// </summary>
public sealed class SyncInstanceGuard : IDisposable
{
    private readonly FileStream _lockStream;
    private readonly string     _lockPath;

    private SyncInstanceGuard(FileStream lockStream, string lockPath)
    {
        _lockStream = lockStream;
        _lockPath   = lockPath;
    }

    /// <summary>Ruta absoluta del archivo de lock adquirido (para diagnóstico).</summary>
    public string LockPath => _lockPath;

    /// <summary>
    /// Devuelve la ruta canónica del archivo de lock para el <paramref name="sourceId"/> dado.
    /// El directorio de locks es <c>SyncPaths.GetConfigDirectory()\locks</c>.
    /// Normalmente: <c>C:\ProgramData\RenderByte\Sync\locks\&lt;safe-source-id&gt;.lock</c>
    /// </summary>
    public static string GetLockPath(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        // Sanitizar sourceId para nombre de archivo válido (solo alfanuméricos y guiones)
        var safeName = new string(sourceId.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        var locksDir = Path.Combine(SyncPaths.GetConfigDirectory(), "locks");
        return Path.Combine(locksDir, $"{safeName}.lock");
    }

    /// <summary>
    /// Intenta adquirir la instancia única para el <paramref name="sourceId"/> dado.
    /// </summary>
    /// <exception cref="SyncAlreadyRunningException">
    /// Otro proceso ya posee el lock exclusivo del archivo.
    /// </exception>
    /// <exception cref="SyncPermissionException">
    /// No hay permisos suficientes para crear el directorio o abrir el archivo de lock.
    /// </exception>
    /// <exception cref="IOException">
    /// Error de I/O no relacionado con concurrencia (disco lleno, ruta inválida, etc.).
    /// </exception>
    public static SyncInstanceGuard AcquireOrThrow(string sourceId)
    {
        var lockPath = GetLockPath(sourceId);
        var locksDir = Path.GetDirectoryName(lockPath)!;

        try
        {
            Directory.CreateDirectory(locksDir);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SyncPermissionException(
                $"Cannot create lock directory '{locksDir}'. Run as Administrator or repair permissions.");
        }
        catch (IOException ex)
        {
            throw new SyncPermissionException(
                $"Cannot create lock directory '{locksDir}': {ex.Message}");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new SyncAlreadyRunningException(
                "RenderByte Sync is already running for this source.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SyncPermissionException(
                "Cannot access the RenderByte Sync instance lock. Run as Administrator or repair permissions.");
        }

        return new SyncInstanceGuard(stream, lockPath);
    }

    /// <summary>
    /// Determina si una <see cref="IOException"/> corresponde a una violación de sharing
    /// (el archivo está siendo usado en exclusiva por otro proceso).
    /// </summary>
    private static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            // Win32 ERROR_SHARING_VIOLATION = 32  → HResult 0x80070020
            // Win32 ERROR_LOCK_VIOLATION    = 33  → HResult 0x80070021
            const int SharingViolation = unchecked((int)0x80070020);
            const int LockViolation    = unchecked((int)0x80070021);
            return ex.HResult == SharingViolation || ex.HResult == LockViolation;
        }

        // En plataformas no-Windows, cualquier IOException al abrir con FileShare.None
        // indica que el archivo está bloqueado por otro proceso.
        // UnauthorizedAccessException (permisos) es capturada por separado.
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _lockStream.Dispose();
    }
}
