namespace RenderByte.Sync.Agent;

/// <summary>
/// Excepción lanzada cuando ya existe una instancia del agente en ejecución.
/// </summary>
public sealed class SyncAlreadyRunningException(string message) : Exception(message);

/// <summary>
/// Guard de instancia única usando Named Mutex de Windows.
/// Previene que múltiples instancias del agente muten el estado local simultáneamente.
/// </summary>
/// <remarks>
/// Estrategia de namespace del Mutex:
/// <list type="bullet">
///   <item>
///     Primero intenta <c>Global\RenderByteSync-{sourceId}</c>.
///     El namespace <c>Global\</c> es visible entre todas las sesiones de Windows.
///     Funciona correctamente cuando el agente corre como Windows Service (cuenta de servicio).
///   </item>
///   <item>
///     Si Windows niega el acceso (usuario interactivo sin <c>SeCreateGlobalPrivilege</c>),
///     cae automáticamente a <c>Local\RenderByteSync-{sourceId}</c>, visible solo dentro
///     de la misma sesión de usuario. Se emite un warning en consola.
///   </item>
/// </list>
/// Dispose libera el Mutex, permitiendo que otra instancia pueda adquirirlo.
/// </remarks>
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
    /// Intenta adquirir la instancia única para el <paramref name="sourceId"/> dado.
    /// </summary>
    /// <exception cref="SyncAlreadyRunningException">
    /// Si ya hay una instancia adquiriendo el mismo mutex.
    /// </exception>
    public static SyncInstanceGuard AcquireOrThrow(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        // Sanitizar sourceId para nombre válido de kernel object (solo alfanuméricos y guiones)
        var safeName = new string(sourceId.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        var globalName = $@"Global\RenderByteSync-{safeName}";
        var localName  = $@"Local\RenderByteSync-{safeName}";

        Mutex   mutex;
        string  mutexName;
        bool    useGlobal;

        try
        {
            mutex     = new Mutex(false, globalName);
            mutexName = globalName;
            useGlobal = true;
        }
        catch (UnauthorizedAccessException)
        {
            // Sin SeCreateGlobalPrivilege — ocurre en sesiones no-interactivas sin ser servicio
            Console.WriteLine(
                $"[WARN] Sin acceso a mutex Global\\. Usando Local\\. " +
                $"Como Windows Service, Global\\ estará disponible sin cambios de código.");
            mutex     = new Mutex(false, localName);
            mutexName = localName;
            useGlobal = false;
        }

        _ = useGlobal; // evita warning de variable no usada

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new SyncAlreadyRunningException(
                $"Ya existe una instancia de RenderByte Sync en ejecución " +
                $"para source_id='{sourceId}'. " +
                $"Mutex: {mutexName}. " +
                $"Detenga el proceso existente antes de iniciar uno nuevo.");
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
