using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки подключения рабочей сети.</summary>
public sealed class EquipmentNetworkPersistenceTests
{
    /// <summary>Отсутствие записи означает неподключённое состояние.</summary>
    [Fact]
    public async Task GetNetworkAttachmentCount_NoRowsReturnsZero()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        Assert.Equal(0, await service.GetNetworkAttachmentCountAsync(7));
        Assert.Contains("Открыто <> 0", connection.CommandLog[0]);
    }

    /// <summary>Attach и detach используют подтверждённые SQL-операции.</summary>
    [Fact]
    public async Task AttachAndDetach_UseConfirmedSql()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиКомпьютера", (byte)1))),
            Script.Scalar(1), Script.NonQuery(),
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиКомпьютера", (byte)1))),
            Script.Scalar(1), Script.NonQuery(), Script.Scalar(0),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.AttachNetworkAsync(7);
        await service.DetachNetworkAsync(7);
        var count = await service.GetNetworkAttachmentCountAsync(7);

        Assert.Contains("INSERT vwЛокальнаяСетьПрисоединениеРабочейСети", connection.CommandLog[3]);
        Assert.Contains("UPDATE vwЛокальнаяСетьПрисоединениеРабочейСети", connection.CommandLog[7]);
        Assert.Contains("Открыто > 0", connection.CommandLog[7]);
        Assert.Equal(0, count);
    }

    /// <summary>Без роли 103 сетевое подключение не выполняется.</summary>
    [Fact]
    public async Task AttachWithoutSupportRole_DoesNotWrite()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиКомпьютера", (byte)1))),
            Script.Scalar(0),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AttachNetworkAsync(7));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT"));
    }
}
