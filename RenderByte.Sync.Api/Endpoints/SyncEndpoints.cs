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

        app.MapPost("/v1/sync/products", async (
            HttpContext context,
            ProductSyncRequest? request,
            IConfiguration config) =>
        {
            if (request == null || request.Products == null || !request.Products.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Payload vacío o inválido.", request?.BatchId, null));
            }

            if (request.Products.Count > 1000)
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "El batch excede el límite de 1000 productos.", request.BatchId, null));
            }

            var authContext = context.Items["SyncAuthContext"] as SyncAuthContext;
            if (authContext == null || authContext.SourceId != request.SourceId)
            {
                return Results.Json(new SyncErrorResponse("SOURCE_MISMATCH", "El source_id del request no corresponde a las credenciales presentadas.", request.BatchId, null), statusCode: 403);
            }

            var connectionString = config.GetConnectionString("DefaultConnection");

            var mandatoryErrors = new List<string>();
            foreach (var prod in request.Products)
            {
                if (string.IsNullOrWhiteSpace(prod.BusinessKey) || string.IsNullOrWhiteSpace(prod.ContentHash) || prod.ArticleId <= 0)
                {
                    mandatoryErrors.Add($"Producto inválido en batch (ArticleId: {prod.ArticleId}).");
                }
            }

            if (mandatoryErrors.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Errores de validación en el batch.", request.BatchId, null));
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            int inserted = 0;
            int updated = 0;
            int unchanged = 0;

            try
            {
                var selectSql = "SELECT content_hash FROM products_raw WHERE source_id = @SourceId AND article_id = @ArticleId;";
                var insertSql = @"
                    INSERT INTO products_raw (
                        organization_id, source_id, branch_id, article_id, business_key, content_hash, payload, is_present
                    ) VALUES (
                        @OrganizationId, @SourceId, @BranchId, @ArticleId, @BusinessKey, @ContentHash, @Payload::JSONB, @IsPresent
                    );";
                var updateSql = @"
                    UPDATE products_raw SET
                        branch_id = @BranchId,
                        business_key = @BusinessKey,
                        content_hash = @ContentHash,
                        payload = @Payload::JSONB,
                        is_present = @IsPresent,
                        source_seen_at = NOW()
                    WHERE source_id = @SourceId AND article_id = @ArticleId;";

                foreach (var prod in request.Products)
                {
                    bool isTombstone = prod.ContentHash == "TOMBSTONE";

                    var existingHash = await connection.ExecuteScalarAsync<string>(selectSql, new
                    {
                        SourceId = authContext.SourceId,
                        ArticleId = prod.ArticleId
                    }, transaction);

                    if (existingHash == null)
                    {
                        await connection.ExecuteAsync(insertSql, new
                        {
                            OrganizationId = authContext.OrganizationId,
                            SourceId = authContext.SourceId,
                            BranchId = request.BranchId,
                            ArticleId = prod.ArticleId,
                            BusinessKey = prod.BusinessKey,
                            ContentHash = prod.ContentHash,
                            Payload = isTombstone ? "{}" : prod.Payload,
                            IsPresent = !isTombstone
                        }, transaction);
                        inserted++;
                    }
                    else if (existingHash != prod.ContentHash)
                    {
                        await connection.ExecuteAsync(updateSql, new
                        {
                            SourceId = authContext.SourceId,
                            ArticleId = prod.ArticleId,
                            BranchId = request.BranchId,
                            BusinessKey = prod.BusinessKey,
                            ContentHash = prod.ContentHash,
                            Payload = isTombstone ? "{}" : prod.Payload,
                            IsPresent = !isTombstone
                        }, transaction);
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }

                await transaction.CommitAsync();

                var response = new ProductSyncResponse(
                    request.BatchId,
                    request.Products.Count,
                    inserted,
                    updated,
                    unchanged,
                    DateTimeOffset.UtcNow
                );

                return Results.Ok(response);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                return Results.Problem("Error interno guardando los productos.", statusCode: 500);
            }
        });

        app.MapPost("/v1/sync/stocks", async (
            HttpContext context,
            SyncStockBatchRequest? request,
            IConfiguration config) =>
        {
            if (request == null || request.Stocks == null || !request.Stocks.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Payload vacío o inválido.", request?.BatchId, null));
            }

            if (request.Stocks.Count > 1000)
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "El batch excede el límite de 1000 stocks.", request.BatchId, null));
            }

            var authContext = context.Items["SyncAuthContext"] as SyncAuthContext;
            if (authContext == null || authContext.SourceId != request.SourceId)
            {
                return Results.Json(new SyncErrorResponse("SOURCE_MISMATCH", "El source_id del request no corresponde a las credenciales presentadas.", request.BatchId, null), statusCode: 403);
            }

            var connectionString = config.GetConnectionString("DefaultConnection");

            var hashRegex = new System.Text.RegularExpressions.Regex("^[a-f0-9]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);
            var mandatoryErrors = new List<string>();
            foreach (var stock in request.Stocks)
            {
                if (string.IsNullOrWhiteSpace(stock.BusinessKey) || string.IsNullOrWhiteSpace(stock.ContentHash) || stock.ArticleId <= 0 || string.IsNullOrWhiteSpace(stock.Bulto))
                {
                    mandatoryErrors.Add($"Stock inválido en batch (ArticleId: {stock.ArticleId}, Bulto: {stock.Bulto}).");
                }
                
                if (stock.BusinessKey != null && !hashRegex.IsMatch(stock.BusinessKey))
                {
                    mandatoryErrors.Add($"Stock business_key inválido (ArticleId: {stock.ArticleId}, Bulto: {stock.Bulto}). Debe ser SHA256 64 chars lowercase hex.");
                }

                if (stock.ContentHash != null && !hashRegex.IsMatch(stock.ContentHash))
                {
                    mandatoryErrors.Add($"Stock content_hash inválido (ArticleId: {stock.ArticleId}, Bulto: {stock.Bulto}). Debe ser SHA256 64 chars lowercase hex.");
                }

                if (stock.Depo != request.BranchId)
                {
                    mandatoryErrors.Add($"Depot Validation Failed: El depo {stock.Depo} no coincide con el branch_id del request {request.BranchId}.");
                }
            }

            if (mandatoryErrors.Any())
            {
                return Results.BadRequest(new SyncErrorResponse("INVALID_PAYLOAD", "Errores de validación en el batch.", request.BatchId, null));
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            int inserted = 0;
            int updated = 0;
            int unchanged = 0;

            try
            {
                var selectSql = "SELECT content_hash FROM stock_levels_raw WHERE source_id = @SourceId AND depo = @Depo AND article_id = @ArticleId AND bulto = @Bulto;";
                var insertSql = @"
                    INSERT INTO stock_levels_raw (
                        organization_id, source_id, branch_id, depo, article_id, bulto, business_key, content_hash, costo, precio, saldo, piezas, is_present
                    ) VALUES (
                        @OrganizationId, @SourceId, @BranchId, @Depo, @ArticleId, @Bulto, @BusinessKey, @ContentHash, @Costo::NUMERIC, @Precio::NUMERIC, @Saldo::NUMERIC, @Piezas::NUMERIC, @IsPresent
                    );";
                var updateSql = @"
                    UPDATE stock_levels_raw SET
                        branch_id = @BranchId,
                        business_key = @BusinessKey,
                        content_hash = @ContentHash,
                        costo = @Costo::NUMERIC,
                        precio = @Precio::NUMERIC,
                        saldo = @Saldo::NUMERIC,
                        piezas = @Piezas::NUMERIC,
                        is_present = @IsPresent,
                        source_seen_at = NOW()
                    WHERE source_id = @SourceId AND depo = @Depo AND article_id = @ArticleId AND bulto = @Bulto;";

                foreach (var stock in request.Stocks)
                {
                    var existingHash = await connection.ExecuteScalarAsync<string>(selectSql, new
                    {
                        SourceId = authContext.SourceId,
                        Depo = stock.Depo,
                        ArticleId = stock.ArticleId,
                        Bulto = stock.Bulto
                    }, transaction);

                    if (existingHash == null)
                    {
                        await connection.ExecuteAsync(insertSql, new
                        {
                            OrganizationId = authContext.OrganizationId,
                            SourceId = authContext.SourceId,
                            BranchId = request.BranchId,
                            Depo = stock.Depo,
                            ArticleId = stock.ArticleId,
                            Bulto = stock.Bulto,
                            BusinessKey = stock.BusinessKey,
                            ContentHash = stock.ContentHash,
                            Costo = stock.Costo,
                            Precio = stock.Precio,
                            Saldo = stock.Saldo,
                            Piezas = stock.Piezas,
                            IsPresent = stock.IsPresent
                        }, transaction);
                        inserted++;
                    }
                    else if (existingHash != stock.ContentHash)
                    {
                        await connection.ExecuteAsync(updateSql, new
                        {
                            SourceId = authContext.SourceId,
                            BranchId = request.BranchId,
                            Depo = stock.Depo,
                            ArticleId = stock.ArticleId,
                            Bulto = stock.Bulto,
                            BusinessKey = stock.BusinessKey,
                            ContentHash = stock.ContentHash,
                            Costo = stock.Costo,
                            Precio = stock.Precio,
                            Saldo = stock.Saldo,
                            Piezas = stock.Piezas,
                            IsPresent = stock.IsPresent
                        }, transaction);
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }

                await transaction.CommitAsync();

                var response = new SyncStockBatchResponse
                {
                    BatchId = request.BatchId,
                    Accepted = request.Stocks.Count,
                    Inserted = inserted,
                    Updated = updated,
                    Unchanged = unchanged,
                    ReceivedAt = DateTimeOffset.UtcNow.DateTime
                };

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
                return Results.Problem("Error interno guardando los stocks.", statusCode: 500);
            }
        });

    }
}
