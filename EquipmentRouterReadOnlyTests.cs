using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки read-only данных маршрутизатора.</summary>
public sealed class EquipmentRouterReadOnlyTests
{
    /// <summary>Основные сведения читаются из vwRouters без операций записи.</summary>
    [Fact]
    public async Task GetRouterInfo_MapsDescription()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("СетевоеИмя", "router"),
                ("OSVersion", "1.0"), ("Description", "Description"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var info = await service.GetRouterInfoAsync(7);

        Assert.NotNull(info);
        Assert.Equal("router", info.NetworkName);
        Assert.Equal("Description", info.Description);
        Assert.DoesNotContain("UPDATE", connection.CommandLog[0]);
        Assert.DoesNotContain("INSERT", connection.CommandLog[0]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[0]);
    }

    /// <summary>Строки инвентаризации читаются из vwRoutersInventory с сортировкой по коду.</summary>
    [Fact]
    public async Task GetRouterInventory_MapsRowsWithoutCrud()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("Код", 1), ("КодОборудования", 7),
                ("СетевоеИмя", "router"), ("Модуль", "Module"), ("Характеристика", "Value"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var rows = await service.GetRouterInventoryAsync(7);

        var item = Assert.Single(rows);
        Assert.Equal("Module", item.Module);
        Assert.Equal("Value", item.Characteristic);
        Assert.Contains("ORDER BY Код", connection.CommandLog[0]);
        Assert.DoesNotContain("UPDATE", connection.CommandLog[0]);
        Assert.DoesNotContain("INSERT", connection.CommandLog[0]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[0]);
    }
}
