using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки получения конфигурации IP-телефона.</summary>
public sealed class EquipmentPhoneConfigPersistenceTests
{
    /// <summary>Роль 102 проверяется до вызова хранимой процедуры.</summary>
    [Fact]
    public async Task GetPhoneConfig_ChecksRoleBeforeProcedure()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await service.GetPhoneConfigAsync("001122334455");

        Assert.Equal(2, connection.CommandLog.Count);
        Assert.Contains("fn_ТекущиеРоли", connection.CommandLog[0]);
        Assert.Contains("sp_КонфигурацияIpТелефона", connection.CommandLog[1]);
    }

    /// <summary>Без роли процедура не вызывается.</summary>
    [Fact]
    public async Task GetPhoneConfigWithoutRole_DoesNotCallProcedure()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetPhoneConfigAsync("001122334455"));

        Assert.Single(connection.CommandLog);
    }
}
