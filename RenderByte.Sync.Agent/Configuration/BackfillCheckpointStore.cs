using System.Text.Json;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Agent.Configuration;

/// <summary>
/// Provee persistencia en archivo (JSON) para el estado/checkpoint del backfill de movimientos.
/// Es totalmente independiente del SQLite (sync.db) que usa la sincronización continua.
/// </summary>
public sealed class BackfillCheckpointStore
{
    private readonly string _filePath;

    public BackfillCheckpointStore(string? overridePath = null)
    {
        _filePath = overridePath ?? Path.Combine(SyncPaths.GetConfigDirectory(), "backfill_checkpoint.json");
    }

    /// <summary>
    /// Retorna el checkpoint previamente guardado, o null si no existe.
    /// </summary>
    public async Task<MovementCheckpoint?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return null;

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var dto = JsonSerializer.Deserialize<BackfillCheckpointDto>(json);
            if (dto == null) return null;

            return new MovementCheckpoint(dto.Fedepo, dto.ClaveU, dto.Item);
        }
        catch (JsonException)
        {
            return null; // Archivo corrupto o inválido, arranca de cero (o según lo indique --from)
        }
    }

    /// <summary>
    /// Persiste el checkpoint en el disco de manera segura.
    /// </summary>
    public async Task SaveAsync(MovementCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var dto = new BackfillCheckpointDto(
            checkpoint.Fedepo,
            checkpoint.ClaveU,
            checkpoint.Item,
            DateTimeOffset.UtcNow
        );

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        
        // Escritura segura: a un temporal y luego movemos para evitar corrupción
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Elimina el archivo de estado de backfill (para forzar reinicio).
    /// </summary>
    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private record BackfillCheckpointDto(
        DateTime Fedepo,
        string ClaveU,
        int Item,
        DateTimeOffset UpdatedAt
    );
}
