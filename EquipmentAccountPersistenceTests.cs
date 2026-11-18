using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки учётных записей оборудования и защиты паролей.</summary>
public sealed class EquipmentAccountPersistenceTests
{
    /// <summary>Общий список не запрашивает пароли и восстанавливает поля записи.</summary>
    [Fact]
    public async Task GetAccounts_DoesNotReadPasswords()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("Логин", "test"),
                ("ОграниченныеПрава", (byte)1), ("Примечание", "note"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var accounts = await service.GetAccountsAsync(7);

        var account = Assert.Single(accounts);
        Assert.Equal("test", account.Login);
        Assert.Equal(1, account.LimitedRights);
        Assert.Equal("note", account.Notes);
        Assert.DoesNotContain("Пароль", connection.CommandLog[0]);
    }

    /// <summary>Добавление, изменение и удаление используют подтверждённый составной ключ.</summary>
    [Fact]
    public async Task AccountCrud_UsesEquipmentAndLoginKey()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.NonQuery(), Script.NonQuery(), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var account = new EquipmentAccount { EquipmentId = 7, Login = "test", Password = "secret", Notes = "note" };

        await service.AddAccountAsync(account);
        await service.UpdateAccountAsync(account);
        await service.DeleteAccountAsync(account);

        Assert.Contains("INSERT ОборудованиеУчётныеЗаписи", connection.CommandLog[0]);
        Assert.Contains("UPDATE ОборудованиеУчётныеЗаписи", connection.CommandLog[1]);
        Assert.Contains("DELETE ОборудованиеУчётныеЗаписи", connection.CommandLog[2]);
        Assert.All(connection.CommandLog, sql => Assert.Contains("Логин", sql));
    }

    /// <summary>Пароль запрашивается отдельно после успешной проверки роли.</summary>
    [Fact]
    public async Task GetAccountPassword_ChecksRoleBeforeReadingSecret()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.Scalar(1)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var password = await service.GetAccountPasswordAsync(7, "test");

        Assert.Equal("1", password);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.Contains("fn_ТекущиеРоли", connection.CommandLog[0]);
        Assert.Contains("SELECT Пароль", connection.CommandLog[1]);
    }

    /// <summary>Без роли запрос секрета не выполняется.</summary>
    [Fact]
    public async Task GetAccountPasswordWithoutRole_DoesNotReadSecret()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAccountPasswordAsync(7, "test"));

        Assert.Single(connection.CommandLog);
    }
}
