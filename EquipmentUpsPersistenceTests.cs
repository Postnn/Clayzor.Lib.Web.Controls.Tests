using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки UPS и последней инвентаризации.</summary>
public sealed class EquipmentUpsPersistenceTests
{
    /// <summary>Быстрая связь и её удаление используют пару UPS и сервер.</summary>
    [Fact]
    public async Task QuickLink_UsesUpsAndServerIds()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.AddUpsLinkAsync(10, 7);
        await service.DeleteUpsLinkAsync(10, 7);

        Assert.Contains("INSERT vwПодключенияUPS", connection.CommandLog[0]);
        Assert.Contains("НастроеноВыключение", connection.CommandLog[0]);
        Assert.Contains("DELETE vwПодключенияUPS", connection.CommandLog[1]);
    }

    /// <summary>Diff удаляет только связи, меняет флаги и сохраняет тесты одной транзакцией.</summary>
    [Fact]
    public void DetailsDiff_ComposesTransactionWithoutTestDelete()
    {
        var command = UpsSaveBuilder.Build(10,
            [
                new UpsServerLink { ServerEquipmentId = 1 },
                new UpsServerLink { ServerEquipmentId = 3 },
            ],
            [
                new UpsServerLink { ServerEquipmentId = 1, TurnOffConfigured = 1 },
                new UpsServerLink { ServerEquipmentId = 2 },
            ],
            [new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 1), BatteryTime = 10 }],
            [
                new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 2), BatteryTime = 20 },
                new UpsTest { TestedAt = new DateTime(2026, 11, 3), BatteryTime = 30 },
            ]);

        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("DELETE vwПодключенияUPS", command.Sql);
        Assert.Contains("UPDATE vwПодключенияUPS", command.Sql);
        Assert.Contains("INSERT vwПодключенияUPS", command.Sql);
        Assert.Contains("UPDATE vwПодключенияUPSтестирование", command.Sql);
        Assert.Contains("INSERT vwПодключенияUPSтестирование", command.Sql);
        Assert.DoesNotContain("DELETE vwПодключенияUPSтестирование", command.Sql);
        Assert.True(command.Parameters.ParameterNames.Count() >= 15);
    }

    /// <summary>Серверы и тесты восстанавливают отображаемые поля.</summary>
    [Fact]
    public async Task GetServersAndTests_MapsFields()
    {
        DapperColumnMapper.Initialize();
        var serverConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудованияUPS", 10), ("КодОборудованияСервера", 7),
                ("НастроеноВыключение", (byte)1), ("НазваниеСервера", "server"))),
        ]));
        var testConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодТестирования", 5), ("КодОборудованияUPS", 10),
                ("ДатаТестирования", new DateTime(2026, 11, 1)), ("ВремяБатарейUPS", 20),
                ("СервераПолучившиеОповещение", "server"), ("ОшибкиТестирования", "issue"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => serverConnection));
        var testService = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => testConnection));

        var server = Assert.Single(await service.GetUpsServersAsync(10));
        var test = Assert.Single(await testService.GetUpsTestsAsync(10));

        Assert.Equal("server", server.ServerName);
        Assert.Equal(1, server.TurnOffConfigured);
        Assert.Equal(20, test.BatteryTime);
        Assert.Equal("issue", test.Issues);
    }

    /// <summary>Последняя инвентаризация читается по максимальному времени без записи.</summary>
    [Fact]
    public async Task GetLastInventory_MapsFieldsWithoutCrud()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("ВремяПроверки", new DateTime(2026, 11, 1)), ("КодОборудования", 7),
                ("Оборудование", "equipment"), ("КодРасположения", 55),
                ("СломаноРемонтуНеПодлежит", (byte)1), ("Примечание", "note"),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var inventory = await service.GetLastInventoryAsync(7);

        Assert.NotNull(inventory);
        Assert.Equal(55, inventory.LocationId);
        Assert.Equal(1, inventory.IsBeyondRepair);
        Assert.Equal("Editor", inventory.ChangedByName);
        Assert.Contains("ORDER BY A.ВремяПроверки DESC", connection.CommandLog[0]);
        Assert.DoesNotContain("UPDATE", connection.CommandLog[0]);
        Assert.DoesNotContain("INSERT", connection.CommandLog[0]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[0]);
    }
}
