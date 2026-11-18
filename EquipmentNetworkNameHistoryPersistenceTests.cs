using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки чтения истории сетевых имён.</summary>
public sealed class EquipmentNetworkNameHistoryPersistenceTests
{
    /// <summary>Запрос читает представление и восстанавливает сотрудника и время изменения.</summary>
    [Fact]
    public async Task GetNetworkNameHistory_ReadsViewAndMapsEmployee()
    {
        DapperColumnMapper.Initialize();
        var changedAt = new DateTime(2026, 11, 18, 3, 42, 51);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(Script.Row(
            ("КодОборудования", 8), ("СетевоеИмя", "clay-test-net"), ("Изменено", changedAt),
            ("Изменил", 4), ("ИзменилФИО", "Тестовый сотрудник")))]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var history = await service.GetNetworkNameHistoryAsync(8);

        var item = Assert.Single(history);
        Assert.Equal(8, item.EquipmentId);
        Assert.Equal("clay-test-net", item.NetworkName);
        Assert.Equal(changedAt, item.ChangedAtUtc);
        Assert.Equal(4, item.ChangedById);
        Assert.Equal("Тестовый сотрудник", item.ChangedByName);
        Assert.Contains("vwОборудованиеСетевыеИмена", connection.CommandLog[0]);
        Assert.Contains("ORDER BY N.Изменено DESC", connection.CommandLog[0]);
    }
}
