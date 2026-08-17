using Dapper;
using Npgsql;
using RenderByte.Sync.Api.Auth;
using RenderByte.Sync.Contracts;
using System.Text.Json;

namespace RenderByte.Sync.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sync/movements", async (
            HttpContext context, 
            SyncBatchRequest? request, 
            IConfiguration config) =>
        {
            if (request == null || request.Movements == null || !request.Movements.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Payload vacío o inválido.", request?.BatchId, null));
            }

            if (request.Movements.Count > 5000)
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "El batch excede el límite de 5000 movimientos.", request.BatchId, null));
            }

            // Validar que el source_id del request coincida con el token
            var authContext = context.Items["SyncAuthContext"] as SyncAuthContext;
            if (authContext == null || authContext.SourceId != request.SourceId)
            {
                return Results.Json(new SyncErrorResponse("SOURCE_MISMATCH", "El source_id del request no corresponde a las credenciales presentadas.", request.BatchId, null), statusCode: 403);
            }

            var connectionString = config.GetConnectionString("DefaultConnection");
            
            // 1. Batch Validation
            var mandatoryErrors = new List<string>();
            foreach (var mov in request.Movements)
            {
                if (string.IsNullOrWhiteSpace(mov.MovementKey) || string.IsNullOrWhiteSpace(mov.BusinessKey))
                {
                    mandatoryErrors.Add($"Movimiento sin movement_key o business_key (Bulto: {mov.Bulto}).");
                }

                // Validar Decimales
                if (mov.Cantidad != null && !decimal.TryParse(mov.Cantidad, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    mandatoryErrors.Add($"Cantidad inválida: {mov.Cantidad}");
                if (mov.Saldo != null && !decimal.TryParse(mov.Saldo, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    mandatoryErrors.Add($"Saldo inválido: {mov.Saldo}");
                if (mov.Costo != null && !decimal.TryParse(mov.Costo, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    mandatoryErrors.Add($"Costo inválido: {mov.Costo}");
                if (mov.Precio != null && !decimal.TryParse(mov.Precio, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    mandatoryErrors.Add($"Precio inválido: {mov.Precio}");
                if (mov.Piezas != null && !decimal.TryParse(mov.Piezas, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                    mandatoryErrors.Add($"Piezas inválidas: {mov.Piezas}");
            }

            if (mandatoryErrors.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Errores de validación en el batch.", request.BatchId, null));
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            int inserted = 0;
            int duplicates = 0;

            try
            {
                var sql = @"
                    INSERT INTO stock_movements_raw (
                        organization_id, source_id, branch_id, movement_key, business_key,
                        depo, tipomov, fecha, codcom, ptovta, numero, proveedor, idarti, bulto, local_, item,
                        fedepo, oferta, cantidad, saldo, costo, precio, clave_u, piezas
                    ) VALUES (
                        @OrganizationId, @SourceId, @BranchId, @MovementKey, @BusinessKey,
                        @Depo, @TipoMov, @Fecha, @CodCom, @PtoVta, @Numero, @Proveedor, @IdArti, @Bulto, @Local, @Item,
                        @Fedepo, @Oferta, @Cantidad::NUMERIC, @Saldo::NUMERIC, @Costo::NUMERIC, @Precio::NUMERIC, @ClaveU, @Piezas::NUMERIC
                    ) ON CONFLICT (movement_key) DO NOTHING;";

                foreach (var mov in request.Movements)
                {
                    var rowsAffected = await connection.ExecuteAsync(sql, new
                    {
                        OrganizationId = authContext.OrganizationId,
                        SourceId = authContext.SourceId,
                        BranchId = request.BranchId, // Desde el batch
                        MovementKey = mov.MovementKey,
                        BusinessKey = mov.BusinessKey,
                        Depo = mov.Depo,
                        TipoMov = mov.TipoMov,
                        Fecha = mov.Fecha,
                        CodCom = mov.CodCom,
                        PtoVta = mov.PtoVta,
                        Numero = mov.Numero,
                        Proveedor = mov.Proveedor,
                        IdArti = mov.IdArti,
                        Bulto = mov.Bulto,
                        Local = mov.Local,
                        Item = mov.Item,
                        Fedepo = mov.Fedepo,
                        Oferta = mov.Oferta,
                        Cantidad = mov.Cantidad, // String, the ::NUMERIC cast parses it
                        Saldo = mov.Saldo,
                        Costo = mov.Costo,
                        Precio = mov.Precio,
                        ClaveU = mov.ClaveU,
                        Piezas = mov.Piezas
                    }, transaction);

                    if (rowsAffected == 1)
                    {
                        inserted++;
                    }
                    else
                    {
                        duplicates++;
                    }
                }

                await transaction.CommitAsync();

                var response = new SyncBatchResponse(
                    request.BatchId,
                    request.Movements.Count,
                    inserted,
                    duplicates,
                    DateTimeOffset.UtcNow
                );

                return Results.Ok(response);
            }
            catch (PostgresException ex) when (ex.SqlState == "22P03" || ex.SqlState == "22P02") // Invalid text representation (numeric parse error)
            {
                await transaction.RollbackAsync();
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Error de parseo numérico o formato inválido.", request.BatchId, null));
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                // Devolver 500
                return Results.Problem("Error interno guardando los movimientos.", statusCode: 500);
            }
        });
    }
}
