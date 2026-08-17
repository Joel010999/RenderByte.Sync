namespace RenderByte.Sync.Persistence;

/// <summary>
/// Resolución de la ubicación de <c>sync.db</c> con soporte de inyección para tests
/// y futura ejecución como Windows Service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruta de producción:</b>
/// <c>C:\ProgramData\RenderByte\Sync\sync.db</c>
/// (<see cref="Environment.SpecialFolder.CommonApplicationData"/>).
/// Esta ruta es independiente del usuario que ejecuta el proceso, lo que es
/// crítico para la futura ejecución como Windows Service bajo otra identidad de Windows.
/// </para>
///
/// <para>
/// <b>Permisos requeridos:</b><br/>
/// La identidad que ejecute RenderByte Sync DEBERÁ tener permisos de lectura/escritura 
/// sobre esa carpeta. 
/// NO se debe asumir que ejecutar una vez como Administrador para crear la carpeta 
/// resuelve definitivamente los permisos futuros.
/// Cuando exista el instalador/Windows Service, el proceso de instalación deberá 
/// configurar esos permisos explícitamente.
/// Para desarrollo y pruebas, se debe usar la variable de entorno <c>RENDERBYTE_SYNC_DB</c>
/// apuntando a una ruta escribible sin requerir permisos de administrador.
/// </para>
///
/// <para>
/// Si el proceso no tiene permisos, se lanzarán excepciones claras.
/// NO se inventará un fallback silencioso a otra ruta, ni se sugerirá
/// al usuario modificar los permisos automáticamente.
/// </para>
///
/// <para>
/// <b>Override por variable de entorno:</b><br/>
/// Si <c>RENDERBYTE_SYNC_DB</c> está definida, se usa esa ruta en lugar del default.
/// Uso principal: tests unitarios (ruta temporal), entornos CI, debugging local.
/// </para>
/// </remarks>
public static class SyncDbPath
{
    /// <summary>Variable de entorno para sobreescribir la ruta del DB.</summary>
    public const string EnvVar = "RENDERBYTE_SYNC_DB";

    /// <summary>Nombre del archivo de base de datos.</summary>
    public const string FileName = "sync.db";

    /// <summary>
    /// Retorna la ruta efectiva del archivo <c>sync.db</c>:
    /// <list type="number">
    ///   <item>Si <c>RENDERBYTE_SYNC_DB</c> está definida, usa ese valor exacto.</item>
    ///   <item>Si no, usa <c>C:\ProgramData\RenderByte\Sync\sync.db</c>.</item>
    /// </list>
    /// </summary>
    public static string Resolve() =>
        Environment.GetEnvironmentVariable(EnvVar)
        ?? GetDefaultPath();

    /// <summary>
    /// Ruta de producción: <c>C:\ProgramData\RenderByte\Sync\sync.db</c>.
    /// Válida tanto para ejecución como usuario de dominio como futura Windows Service.
    /// </summary>
    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RenderByte", "Sync", FileName);

    /// <summary>
    /// Crea el directorio que contiene <paramref name="dbPath"/> si no existe.
    /// Lanza una excepción clara si el proceso no tiene permisos de creación o escritura.
    /// No inventa un fallback silencioso.
    /// </summary>
    public static void EnsureDirectory(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException($"No se pudo determinar el directorio para la ruta: {dbPath}");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"[PERMISOS] El proceso actual no tiene permisos para crear o escribir en el directorio '{dir}'. " +
                $"La identidad que ejecuta RenderByte Sync debe tener permisos explícitos sobre esta ruta. " +
                $"Para desarrollo, use la variable de entorno {EnvVar} para apuntar a una ruta escribible. " +
                $"No se intentará una ruta alternativa.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[ERROR_IO] Fallo al crear el directorio '{dir}': {ex.Message}", ex);
        }
    }
}

