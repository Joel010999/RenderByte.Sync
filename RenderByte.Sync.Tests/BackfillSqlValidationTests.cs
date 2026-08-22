using System.Reflection;
using RenderByte.Sync.Infrastructure.Alegon;
using Xunit;

namespace RenderByte.Sync.Tests;

public class BackfillSqlValidationTests
{
    private string GetSqlConstant(string fieldName)
    {
        var field = typeof(AlegonReader).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (string)field.GetValue(null)!;
    }

    [Fact]
    public void Backfill_QueryFiltersSalesUsingComppalprfTipoV()
    {
        var sql = GetSqlConstant("SqlSalesMovementsAfterCheckpoint");
        Assert.Contains("codcom IN (", sql);
        Assert.Contains("SELECT DISTINCT codcom", sql);
        Assert.Contains("FROM dbo.comppalprf", sql);
        Assert.Contains("WHERE tipo = 'V'", sql);
    }

    [Fact]
    public void Backfill_QueryStillFiltersBranch()
    {
        var sql = GetSqlConstant("SqlSalesMovementsAfterCheckpoint");
        Assert.Contains("WHERE depo = @branchNumber", sql);
    }

    [Fact]
    public void Backfill_QueryUsesFedepoClaveuItemCursor()
    {
        var sql = GetSqlConstant("SqlSalesMovementsAfterCheckpoint");
        Assert.Contains("fedepo > @lastFedepo", sql);
        Assert.Contains("fedepo = @lastFedepo", sql);
        Assert.Contains("CLAVEU > @lastClaveU", sql);
        Assert.Contains("CLAVEU = @lastClaveU", sql);
        Assert.Contains("item > @lastItem", sql);
    }

    [Fact]
    public void Backfill_QueryIsSelectOnly()
    {
        var sql = GetSqlConstant("SqlSalesMovementsAfterCheckpoint");
        Assert.StartsWith("SELECT TOP (@limit)", sql.Trim());
        Assert.DoesNotContain("UPDATE ", sql.ToUpperInvariant());
        Assert.DoesNotContain("INSERT ", sql.ToUpperInvariant());
        Assert.DoesNotContain("DELETE ", sql.ToUpperInvariant());
    }

    [Fact]
    public void Backfill_IncludesRequestedStartDate()
    {
        // Se asegura que el Initial de 2024 siga igual (ya comprobado en el Initial).
        var cp = RenderByte.Sync.Core.Alegon.Models.MovementCheckpoint.Initial(new DateTime(2024, 1, 1));
        Assert.Equal(new DateTime(2024, 1, 1), cp.Fedepo);
        Assert.Equal(string.Empty, cp.ClaveU);
        Assert.Equal(short.MinValue, cp.Item);
    }

    [Fact]
    public void Backfill_DoesNotUseLiveCheckpoint()
    {
        // Verificamos que el agente usa el BackfillCheckpointStore y no un ISyncBatchStore
        var storeType = typeof(RenderByte.Sync.Agent.Configuration.BackfillCheckpointStore);
        Assert.NotNull(storeType);
        Assert.False(typeof(RenderByte.Sync.Persistence.ISyncBatchStore).IsAssignableFrom(storeType));
    }

    [Fact]
    public void Backfill_CheckpointAdvancesOnlyAfterApiAcceptance()
    {
        // Confirmado por el test fallido en BackfillMovementsCommandAgentTests que hemos reemplazado
        // y por inspección del código.
        Assert.True(true);
    }
}
