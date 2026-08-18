using RenderByte.Sync.Core.Alegon;

namespace RenderByte.Sync.Agent;

public static class ProductSchemaTestAgent
{
    public static async Task<int> RunAsync(IProductSchemaReader reader, CancellationToken ct)
    {
        Console.WriteLine("[PRODUCT DISCOVERY]");
        Console.WriteLine("Database: sistema");
        Console.WriteLine("Table: dbo.articulo");
        
        var count = await reader.GetProductCountAsync(ct);
        Console.WriteLine($"Rows: {count}\n");

        Console.WriteLine("--- SCHEMA ---");
        var schema = await reader.GetSchemaInfoAsync(ct);
        Console.WriteLine(schema);

        Console.WriteLine("--- RELATION TO ARTISTOCK ---");
        var relation = await reader.GetArtistockRelationAsync(ct);
        Console.WriteLine(relation);

        Console.WriteLine("--- DUPLICATES ---");
        var duplicates = await reader.GetDuplicatesInfoAsync(ct);
        Console.WriteLine(duplicates);

        Console.WriteLine("--- MODIFICATION STRATEGY ---");
        var mod = await reader.GetModificationDateInfoAsync(ct);
        Console.WriteLine(mod);

        Console.WriteLine("--- SAMPLE (TOP 20) ---");
        var sample = await reader.GetSampleProductsAsync(20, ct);
        Console.WriteLine(sample);

        Console.WriteLine("Potential Product Identity: NO DECIDIDA / evidencia pendiente.");

        return 0;
    }
}
