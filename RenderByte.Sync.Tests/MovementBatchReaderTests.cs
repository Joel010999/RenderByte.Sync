using Xunit;
using RenderByte.Sync.Core.Alegon;
using RenderByte.Sync.Core.Alegon.Models;

namespace RenderByte.Sync.Tests;

/// <summary>
/// Tests de MovementBatchReader usando un stub en memoria que replica la lógica del cursor
/// compuesto (fedepo > last OR fedepo = last AND CLAVEU > last OR ...).
/// No requiere conexión a SQL Server.
/// </summary>
public sealed class MovementBatchReaderTests
{
    private static readonly DateTime BaseDate = new(2026, 8, 14, 17, 0, 0);

    // ─── EmptySource ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadNextBatch_EmptySource_ReturnsBatchResultEmpty()
    {
        var stub       = new StubAlegonReader(Array.Empty<AlegonMovement>());
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var checkpoint  = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(checkpoint);

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.Count);
        Assert.Equal(checkpoint, result.CheckpointAfter); // no avanza si no hay datos
    }

    // ─── CheckpointAfter apunta al último movimiento ──────────────────────────

    [Fact]
    public async Task ReadNextBatch_WithMovements_CheckpointAfterIsLastMovement()
    {
        var movements = new[]
        {
            MakeMovement(BaseDate,              "CL001", 1),
            MakeMovement(BaseDate,              "CL001", 2),
            MakeMovement(BaseDate.AddSeconds(1),"CL002", 1),
        };
        var stub       = new StubAlegonReader(movements);
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var checkpoint  = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(checkpoint);

        Assert.Equal(3, result.Count);
        var last = movements[^1];
        Assert.Equal(last.FechaDeposito!.Value, result.CheckpointAfter.Fedepo);
        Assert.Equal(last.ClaveU,               result.CheckpointAfter.ClaveU);
        Assert.Equal(last.Item,                 result.CheckpointAfter.Item);
    }

    // ─── Sin pérdidas ni duplicados entre batches ─────────────────────────────

    [Fact]
    public async Task ThreeBatches_NoGapsNoDuplicates()
    {
        // 15 movimientos ordenados: 5 por fecha, con distintos CLAVEU/item
        var allMovements = BuildOrderedMovements(count: 15);
        var stub         = new StubAlegonReader(allMovements);
        var batchReader   = new MovementBatchReader(stub, branchNumber: 2, batchSize: 5);
        var checkpoint    = MovementCheckpoint.Initial(BaseDate);

        var readIds = new List<string>();

        for (int i = 0; i < 3; i++)
        {
            var result = await batchReader.ReadNextBatchAsync(checkpoint);

            Assert.False(result.IsEmpty, $"Batch {i + 1} no debería estar vacío.");
            Assert.Equal(5, result.Count);

            // Acumular identidades leídas
            foreach (var m in result.Movements)
                readIds.Add($"{m.ClaveU}:{m.Item}");

            checkpoint = result.CheckpointAfter;
        }

        // El cuarto batch debe estar vacío (se agotaron los 15)
        var empty = await batchReader.ReadNextBatchAsync(checkpoint);
        Assert.True(empty.IsEmpty);

        // Sin duplicados
        Assert.Equal(readIds.Count, readIds.Distinct().Count());

        // Total leído = 15
        Assert.Equal(15, readIds.Count);
    }

    // ─── Cursor no retrocede ──────────────────────────────────────────────────

    [Fact]
    public async Task CheckpointAfter_IsAlwaysAfterInitial()
    {
        var movements = new[]
        {
            MakeMovement(BaseDate, "CL001", 1),
            MakeMovement(BaseDate, "CL001", 2),
        };
        var stub       = new StubAlegonReader(movements);
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var initial     = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(initial);

        // CheckpointAfter debe ser "mayor" que el inicial
        Assert.NotEqual(initial, result.CheckpointAfter);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static List<AlegonMovement> BuildOrderedMovements(int count)
    {
        var list = new List<AlegonMovement>(count);
        for (int i = 0; i < count; i++)
        {
            // Distribuye en 3 fechas distintas, con 5 elementos cada una
            var fedepo = BaseDate.AddMinutes(i / 5);
            var claveU = $"CL{(i % 5 + 1):D3}";  // CL001..CL005
            var item   = i + 1;
            list.Add(MakeMovement(fedepo, claveU, item));
        }
        return list;
    }

    private static AlegonMovement MakeMovement(DateTime fedepo, string claveU, int item) =>
        new(
            Depo:              2,
            TipoMovimiento:    "VT",
            Fecha:             fedepo.Date,
            CodigoComprobante: "TEST",
            PuntoVenta:        "0001",
            Numero:            "00000001",
            Proveedor:         "PROV",
            ArticleId:         "ART001",
            Bulto:             "U",
            Local:             2,
            Item:              item,
            FechaDeposito:     fedepo,
            Oferta:            null,
            Cantidad:          1m,
            Saldo:             0m,
            Costo:             100m,
            Precio:            150m,
            ClaveU:            claveU,
            Piezas:            null
        );

    // ─── Stub ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Implementación en memoria de IAlegonReader para tests.
    /// Replica la lógica del cursor compuesto (idéntica a la SQL de AlegonReader)
    /// para verificar que no hay gaps ni duplicados.
    /// </summary>
    internal sealed class StubAlegonReader : IAlegonReader
    {
        private readonly List<AlegonMovement> _all;

        public StubAlegonReader(IEnumerable<AlegonMovement> movements) =>
            _all = movements.ToList();

        public Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(int branchNumber, MovementCheckpoint checkpoint, int limit, CancellationToken cancellationToken = default)
        {
            return GetMovementsAfterAsync(branchNumber, checkpoint, limit, false, cancellationToken);
        }

        public Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(int branchNumber, MovementCheckpoint checkpoint, int limit, bool salesOnly, CancellationToken cancellationToken = default)
        {
            var result = _all.Where(m => ComparePosition(m, checkpoint) > 0)
                                  .OrderBy(m => m.FechaDeposito ?? DateTime.MinValue)
                                  .ThenBy(m => m.ClaveU, StringComparer.Ordinal)
                                  .ThenBy(m => m.Item)
                                  .Take(limit)
                                  .ToList();
            return Task.FromResult<IReadOnlyList<AlegonMovement>>(result);
        }

        private static int ComparePosition(AlegonMovement m, MovementCheckpoint cp)
        {
            var fedepo = m.FechaDeposito!.Value;
            if (fedepo > cp.Fedepo) return 1;
            if (fedepo < cp.Fedepo) return -1;

            var claveComp = string.Compare(m.ClaveU, cp.ClaveU, StringComparison.Ordinal);
            if (claveComp != 0) return claveComp;

            return m.Item.CompareTo(cp.Item);
        }

        // Métodos no utilizados en estos tests
        public Task<AlegonHealthCheck>            GetHealthCheckAsync(CancellationToken ct = default)                         => throw new NotImplementedException();
        public Task<int>                          GetBranchNumberAsync(CancellationToken ct = default)                        => throw new NotImplementedException();
        public Task<IReadOnlyList<AlegonProduct>> GetProductsAsync(CancellationToken ct = default)                            => throw new NotImplementedException();
        public Task<IReadOnlyList<AlegonStock>>   GetCurrentStockAsync(int b, CancellationToken ct = default)                 => throw new NotImplementedException();
        public Task<DateTime?>                    GetLatestMovementInsertionDateAsync(int b, CancellationToken ct = default)   => throw new NotImplementedException();
    }

    // ─── RawStubAlegonReader ──────────────────────────────────────────────────
    // Retorna los movimientos tal cual se los indica, SIN aplicar el cursor.
    // Usado para testear la validación defensiva con datos intencionalmente inválidos.

    private sealed class RawStubAlegonReader : IAlegonReader
    {
        private readonly IReadOnlyList<AlegonMovement> _raw;

        public RawStubAlegonReader(params AlegonMovement[] raw) => _raw = raw;

        public Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(int branchNumber, MovementCheckpoint checkpoint, int limit, CancellationToken cancellationToken = default)
        {
            return GetMovementsAfterAsync(branchNumber, checkpoint, limit, false, cancellationToken);
        }

        public Task<IReadOnlyList<AlegonMovement>> GetMovementsAfterAsync(int branchNumber, MovementCheckpoint checkpoint, int limit, bool salesOnly, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AlegonMovement>>(_raw);
        } public Task<AlegonHealthCheck>            GetHealthCheckAsync(CancellationToken ct = default)                       => throw new NotImplementedException();
        public Task<int>                          GetBranchNumberAsync(CancellationToken ct = default)                      => throw new NotImplementedException();
        public Task<IReadOnlyList<AlegonProduct>> GetProductsAsync(CancellationToken ct = default)                          => throw new NotImplementedException();
        public Task<IReadOnlyList<AlegonStock>>   GetCurrentStockAsync(int b, CancellationToken ct = default)               => throw new NotImplementedException();
        public Task<DateTime?>                    GetLatestMovementInsertionDateAsync(int b, CancellationToken ct = default) => throw new NotImplementedException();
    }

    // ─── Tests de cursor: mismo fedepo, diferentes CLAVEU ────────────────────

    [Fact]
    public async Task SameFedepo_DifferentClaveU_ReadsInOrder()
    {
        // Mismo fedepo, tres CLAVEUs distintos → deben leerse en orden CLAVEU ASC
        var m1 = MakeMovement(BaseDate, "AA001", 1);
        var m2 = MakeMovement(BaseDate, "BB001", 1);
        var m3 = MakeMovement(BaseDate, "CC001", 1);

        var stub       = new StubAlegonReader(new[] { m3, m1, m2 }); // desordenados en el stub
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var checkpoint  = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(checkpoint);

        Assert.Equal(3, result.Count);
        Assert.Equal("AA001", result.Movements[0].ClaveU);
        Assert.Equal("BB001", result.Movements[1].ClaveU);
        Assert.Equal("CC001", result.Movements[2].ClaveU);
        Assert.Equal("CC001", result.CheckpointAfter.ClaveU);
    }

    [Fact]
    public async Task SameFedepo_SameClaveU_DifferentItem_ReadsInOrder()
    {
        // Mismo fedepo + CLAVEU, items distintos → deben leerse en orden item ASC
        var m1 = MakeMovement(BaseDate, "CL001", 1);
        var m2 = MakeMovement(BaseDate, "CL001", 2);
        var m3 = MakeMovement(BaseDate, "CL001", 3);

        var stub       = new StubAlegonReader(new[] { m3, m1, m2 });
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var checkpoint  = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(checkpoint);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result.Movements[0].Item);
        Assert.Equal(2, result.Movements[1].Item);
        Assert.Equal(3, result.Movements[2].Item);
        Assert.Equal(3, result.CheckpointAfter.Item);
    }

    [Fact]
    public async Task FedepoChange_BatchCrossesTimestampBoundary_NoDuplicates()
    {
        // Dos fechas distintas con CLAVEUs y items mezclados
        var date2 = BaseDate.AddMinutes(1);
        var movements = new[]
        {
            MakeMovement(BaseDate, "CL001", 1),
            MakeMovement(BaseDate, "CL002", 1),
            MakeMovement(date2,    "CL001", 1),
            MakeMovement(date2,    "CL002", 1),
        };
        var stub       = new StubAlegonReader(movements);
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var initial     = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(initial);

        Assert.Equal(4, result.Count);

        // Segundo batch debe estar vacío
        var empty = await batchReader.ReadNextBatchAsync(result.CheckpointAfter);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public async Task CheckpointAfter_MatchesExactlyLastMovement()
    {
        var movements = new[]
        {
            MakeMovement(BaseDate,              "CL001", 1),
            MakeMovement(BaseDate,              "CL001", 2),
            MakeMovement(BaseDate.AddSeconds(5),"CL099", 7),
        };
        var stub       = new StubAlegonReader(movements);
        var batchReader = new MovementBatchReader(stub, branchNumber: 2, batchSize: 10);
        var checkpoint  = MovementCheckpoint.Initial(BaseDate);

        var result = await batchReader.ReadNextBatchAsync(checkpoint);

        var last = movements[^1];
        Assert.Equal(last.FechaDeposito!.Value, result.CheckpointAfter.Fedepo);
        Assert.Equal(last.ClaveU,               result.CheckpointAfter.ClaveU);
        Assert.Equal(last.Item,                 result.CheckpointAfter.Item);
    }

    // ─── Tests de validación defensiva ────────────────────────────────────────

    [Fact]
    public async Task BatchOutOfOrder_Throws_InvalidOperation()
    {
        // El RawStub retorna movimientos intencionalmente desordenados
        var checkpoint = MovementCheckpoint.Initial(BaseDate);
        var m1         = MakeMovement(BaseDate, "BB001", 2);  // mayor
        var m2         = MakeMovement(BaseDate, "AA001", 1);  // menor → viola el orden

        var raw        = new RawStubAlegonReader(m1, m2);
        var batchReader = new MovementBatchReader(raw, branchNumber: 2, batchSize: 10);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchReader.ReadNextBatchAsync(checkpoint));

        Assert.Contains("[BUG]", ex.Message);
        Assert.Contains("desordenado", ex.Message);
    }

    [Fact]
    public async Task MovementEqualToCheckpoint_Throws_InvalidOperation()
    {
        // El checkpoint exactamente igual a un movimiento → viola la condición de "estrictamente posterior"
        var checkpoint = new MovementCheckpoint(BaseDate, "CL001", 5);
        var m          = MakeMovement(BaseDate, "CL001", 5);  // igual al checkpoint, no estrictamente posterior

        var raw        = new RawStubAlegonReader(m);
        var batchReader = new MovementBatchReader(raw, branchNumber: 2, batchSize: 10);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchReader.ReadNextBatchAsync(checkpoint));

        Assert.Contains("[BUG]", ex.Message);
        Assert.Contains("estrictamente posterior", ex.Message);
    }

    [Fact]
    public async Task MovementBeforeCheckpoint_Throws_InvalidOperation()
    {
        // Movimiento con fedepo < checkpoint.Fedepo → debe fallar
        var checkpoint = new MovementCheckpoint(BaseDate.AddMinutes(1), "AA000", 0);
        var m          = MakeMovement(BaseDate, "ZZ999", 99);  // fedepo anterior al checkpoint

        var raw        = new RawStubAlegonReader(m);
        var batchReader = new MovementBatchReader(raw, branchNumber: 2, batchSize: 10);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => batchReader.ReadNextBatchAsync(checkpoint));

        Assert.Contains("[BUG]", ex.Message);
    }
}

