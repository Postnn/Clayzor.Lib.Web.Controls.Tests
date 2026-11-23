using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки read-only предупреждения о другом оборудовании на рабочем месте EQ8-12.</summary>
public sealed class Eq8WorkplaceAdvisoryTests
{
    /// <summary>Запрос сохраняет legacy-группы, параметры и фильтры, а DTO даёт текст для UI.</summary>
    [Fact]
    public async Task GetWorkplaceAdvisory_UsesLegacyQueryContractAndMapsDisplayText()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(
                Script.Row(("EquipmentId", 8), ("ModelName", "ThinkPad"), ("NetworkName", "ws-08")),
                Script.Row(("EquipmentId", 9), ("ModelName", "Latitude"), ("NetworkName", null))),
        ]));
        var service = CreateService(connection);

        var items = await service.GetWorkplaceAdvisoryAsync(7, 55);

        Assert.Equal(2, items.Count);
        Assert.Equal(8, items[0].EquipmentId);
        Assert.Equal("ThinkPad (ws-08)", items[0].DisplayText);
        Assert.Equal("Latitude", items[1].DisplayText);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("vwОборудованиеСписок E", sql);
        Assert.Contains("CurrentEquipment.КодОборудования = @EquipmentId", sql);
        Assert.Contains("E.КодРасположения = @Id", sql);
        Assert.Contains("@IT = 1 AND E.КодТипаОборудования < 100", sql);
        Assert.Contains("E.Списано IS NULL", sql);
        Assert.Contains("E.КодОборудования <> @EquipmentId", sql);
        Assert.Contains("E.РабочееМесто = 1", sql);
        Assert.Contains("IN (1, 3, 5)", sql);
        Assert.Contains("IN (6, 7)", sql);
        Assert.Contains("IN (20, 21, 30)", sql);
        Assert.DoesNotContain("INSERT ", sql);
        Assert.DoesNotContain("UPDATE ", sql);
        Assert.DoesNotContain("DELETE ", sql);
        Assert.Equal(55, connection.ParameterLog[0]["Id"]);
        Assert.Equal(1, connection.ParameterLog[0]["IT"]);
        Assert.Equal(7, connection.ParameterLog[0]["EquipmentId"]);
    }

    /// <summary>Отсутствие другого оборудования является пустым списком, а не ошибкой.</summary>
    [Fact]
    public async Task GetWorkplaceAdvisoryNoRows_ReturnsEmpty()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var service = CreateService(connection);

        var items = await service.GetWorkplaceAdvisoryAsync(7, 55);

        Assert.Empty(items);
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Ошибка SQL не подменяется отсутствием предупреждения.</summary>
    [Fact]
    public async Task GetWorkplaceAdvisorySqlError_PropagatesInsteadOfReturningEmpty()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Error(SqlExceptionFactory.Create(53, "connection error")),
        ]));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            service.GetWorkplaceAdvisoryAsync(7, 55));

        Assert.Single(connection.CommandLog);
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection) =>
        new(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
}
