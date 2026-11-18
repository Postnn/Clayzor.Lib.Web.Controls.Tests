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
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.NonQuery(), Script.NonQuery(), Script.Scalar(0),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.AttachNetworkAsync(7);
        await service.DetachNetworkAsync(7);
        var count = await service.GetNetworkAttachmentCountAsync(7);

        Assert.Contains("INSERT vwЛокальнаяСетьПрисоединениеРабочейСети", connection.CommandLog[0]);
        Assert.Contains("UPDATE vwЛокальнаяСетьПрисоединениеРабочейСети", connection.CommandLog[1]);
        Assert.Contains("Открыто > 0", connection.CommandLog[1]);
        Assert.Equal(0, count);
    }
}
