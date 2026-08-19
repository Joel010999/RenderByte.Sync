using RenderByte.Sync.Core.Alegon;

namespace RenderByte.Sync.Agent;

/// <summary>
/// Agente de discovery del schema de productos y stock.
/// Cada sección se ejecuta de forma independiente: si una falla, se registra
/// un [WARN] y se continúa con las demás. Nunca se propaga una excepción
/// no manejada al llamador.
/// </summary>
public static class ProductSchemaTestAgent
{
    public static async Task<int> RunAsync(IProductSchemaReader reader, CancellationToken ct)
    {
        Console.WriteLine("[PRODUCT DISCOVERY — M8.0.1]");
        Console.WriteLine("Database: sistema");
        Console.WriteLine("Table:    dbo.articulo / dbo.artistock");
        Console.WriteLine();

        // ── Row count ────────────────────────────────────────────────────────
        await RunSectionAsync("ROW COUNT", async () =>
        {
            var count = await reader.GetProductCountAsync(ct);
            Console.WriteLine($"dbo.articulo rows: {count}");
        });

        // ── dbo.articulo schema ───────────────────────────────────────────────
        await RunSectionAsync("ARTICULO SCHEMA (columns + indexes)", async () =>
        {
            var schema = await reader.GetSchemaInfoAsync(ct);
            Console.WriteLine(schema);
        });

        // ── dbo.artistock schema ──────────────────────────────────────────────
        await RunSectionAsync("ARTISTOCK SCHEMA (columns + indexes)", async () =>
        {
            var schema = await reader.GetArtistockSchemaAsync(ct);
            Console.WriteLine(schema);
        });

        // ── dbo.artistock sample ids ──────────────────────────────────────────
        await RunSectionAsync("ARTISTOCK SAMPLE IDS (TOP 30 distinct idarti + bulto)", async () =>
        {
            var sample = await reader.GetArtistockSampleIdsAsync(30, ct);
            Console.WriteLine(sample);
        });

        // ── dbo.artistock idarti profile ──────────────────────────────────────
        await RunSectionAsync("ARTISTOCK IDARTI PROFILE", async () =>
        {
            var profile = await reader.GetArtistockIdProfileAsync(ct);
            Console.WriteLine(profile);
        });

        // ── Relation candidates ───────────────────────────────────────────────
        await RunSectionAsync("RELATION CANDIDATES (safe — no idarti→INT cast)", async () =>
        {
            var relation = await reader.GetArtistockRelationAsync(ct);
            Console.WriteLine(relation);
        });

        // ── Duplicates ────────────────────────────────────────────────────────
        await RunSectionAsync("DUPLICATES", async () =>
        {
            var duplicates = await reader.GetDuplicatesInfoAsync(ct);
            Console.WriteLine(duplicates);
        });

        // ── Modification strategy ─────────────────────────────────────────────
        await RunSectionAsync("MODIFICATION STRATEGY (top articulo IDs as proxy)", async () =>
        {
            var mod = await reader.GetModificationDateInfoAsync(ct);
            Console.WriteLine(mod);
        });

        // ── Cost / Price columns ──────────────────────────────────────────────
        await RunSectionAsync("COST / PRICE INFO", async () =>
        {
            var cost = await reader.GetCostPriceInfoAsync(ct);
            Console.WriteLine(cost);
        });

        // ── Active / Baja columns ─────────────────────────────────────────────
        await RunSectionAsync("SOFT DELETE / ACTIVE INFO", async () =>
        {
            var soft = await reader.GetSoftDeleteInfoAsync(ct);
            Console.WriteLine(soft);
        });

        // ── Sample (TOP 20 articulo) ──────────────────────────────────────────
        await RunSectionAsync("SAMPLE articulo (TOP 20)", async () =>
        {
            var sample = await reader.GetSampleProductsAsync(20, ct);
            Console.WriteLine(sample);
        });

        Console.WriteLine();
        Console.WriteLine("Product Identity: NO DECIDIDA — evidencia pendiente de análisis de relación.");
        Console.WriteLine("[PRODUCT DISCOVERY COMPLETE]");

        return 0;
    }

    /// <summary>
    /// Ejecuta <paramref name="action"/> dentro de un bloque de sección con banner.
    /// Si la sección lanza cualquier excepción, la captura, imprime un [WARN] con el
    /// mensaje y continúa. Nunca relanza.
    /// </summary>
    private static async Task RunSectionAsync(string title, Func<Task> action)
    {
        Console.WriteLine($"--- {title} ---");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Section '{title}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }
}
