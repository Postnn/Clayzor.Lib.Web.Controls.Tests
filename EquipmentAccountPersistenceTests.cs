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
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодОборудования", 7), ("Логин", "test"),
                ("ОграниченныеПрава", (byte)1), ("Примечание", "note"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var accounts = await service.GetAccountsAsync(7);

        var account = Assert.Single(accounts);
        Assert.Equal("test", account.Login);
        Assert.Equal(1, account.LimitedRights);
        Assert.Equal("note", account.Notes);
        Assert.DoesNotContain("Пароль", connection.CommandLog[1]);
    }

    /// <summary>Добавление, изменение и удаление используют подтверждённый составной ключ.</summary>
    [Fact]
    public async Task AccountCrud_UsesEquipmentAndLoginKey()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Scalar(1), Script.Rows(Script.Row(("ЕстьMACадрес", (byte)1))), Script.NonQuery(),
            Script.Scalar(1), Script.Scalar(1), Script.Rows(Script.Row(("ЕстьMACадрес", (byte)1))), Script.NonQuery(),
            Script.Scalar(1), Script.Scalar(1), Script.Rows(Script.Row(("ЕстьMACадрес", (byte)1))), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());
        var account = new EquipmentAccount { EquipmentId = 7, Login = "test", Password = "secret", Notes = "note" };

        await service.AddAccountAsync(account);
        await service.UpdateAccountAsync(account);
        await service.DeleteAccountAsync(account);

        Assert.Contains("INSERT ОборудованиеУчётныеЗаписи", connection.CommandLog[3]);
        Assert.Contains("UPDATE ОборудованиеУчётныеЗаписи", connection.CommandLog[7]);
        Assert.Contains("DELETE ОборудованиеУчётныеЗаписи", connection.CommandLog[11]);
        Assert.All(new[] { connection.CommandLog[3], connection.CommandLog[7], connection.CommandLog[11] },
            sql => Assert.Contains("Логин", sql));
    }

    /// <summary>Запись учётной записи без роли 43 отклоняется до SQL.</summary>
    [Fact]
    public async Task AddAccountWithoutInventoryRole_DoesNotWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddAccountAsync(
            new EquipmentAccount { EquipmentId = 7, Login = "test" }));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT"));
    }

    /// <summary>Пароль запрашивается отдельно после успешной проверки роли.</summary>
    [Fact]
    public async Task GetAccountPassword_ChecksRoleBeforeReadingSecret()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("Пароль", "secret"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var password = await service.GetAccountPasswordAsync(7, "test");

        Assert.Equal("secret", password);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.Contains("fn_ТекущиеРоли", connection.CommandLog[0]);
        Assert.Contains("SELECT Пароль", connection.CommandLog[1]);
    }

    /// <summary>После ошибки повторное чтение может вернуть легитимный пустой пароль без записи.</summary>
    [Fact]
    public async Task GetAccountPasswordAfterFailure_RetryCanReturnLegitimateEmpty()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(53, "connection error")),
            Script.Scalar(1), Script.Rows(Script.Row(("Пароль", string.Empty))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => service.GetAccountPasswordAsync(7, "test"));
        Assert.Equal(string.Empty, await service.GetAccountPasswordAsync(7, "test"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Ошибка соединения при чтении пароля не превращается в пустой пароль.</summary>
    [Theory]
    [InlineData(53)]
    [InlineData(-2)]
    [InlineData(10054)]
    public async Task GetAccountPasswordConnectivity_ThrowsInsteadOfReturningEmpty(int errorNumber)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(errorNumber, "connection error")),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => service.GetAccountPasswordAsync(7, "test"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Отсутствие строки учётной записи отличается от легитимного пустого пароля.</summary>
    [Fact]
    public async Task GetAccountPasswordNoRow_ThrowsInsteadOfReturningEmpty()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), new ScriptEntry()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccountPasswordAsync(7, "test"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Без роли запрос секрета не выполняется.</summary>
    [Fact]
    public async Task GetAccountPasswordWithoutRole_DoesNotReadSecret()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(0)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAccountPasswordAsync(7, "test"));

        Assert.Single(connection.CommandLog);
    }
}
