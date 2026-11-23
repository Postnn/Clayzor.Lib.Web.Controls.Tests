using System.Security.Claims;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки единой авторизации Employee и областей ролей.</summary>
public sealed class EmployeeAuthorizationTests
{
    /// <summary>Глобальная и личная области проверяются через fn_ТекущиеРоли после подтверждения principal.</summary>
    [Fact]
    public async Task RoleChecks_UseTrustedIdentityAndPersonScopes()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([
            Script.Rows(EmployeeRow()), Script.Rows(Script.Row(("Value", 1))),
            Script.Rows(EmployeeRow()), Script.Rows(Script.Row(("Value", 0))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new FakeAuthenticationStateProvider());

        Assert.True(await service.HasRoleForPersonAsync(43, 1));
        Assert.False(await service.HasRoleForPersonAsync(43, 2));
        Assert.Equal(2, connection.CommandLog.Count(sql => sql.Contains("fn_ТекущиеРоли")));
        Assert.Equal(1, connection.ParameterLog[1]["PersonId"]);
        Assert.Equal(2, connection.ParameterLog[3]["PersonId"]);
    }

    /// <summary>Principal mismatch запрещает роль без дополнительного SQL.</summary>
    [Fact]
    public async Task PrincipalMismatch_FailsClosed()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([Script.Rows()]));
        var service = new EquipmentFormService(CreateDb(connection), new FakeAuthenticationStateProvider());

        Assert.False(await service.HasRoleAsync(43));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Отзыв роли после открытия формы возвращает запрет.</summary>
    [Fact]
    public async Task RevokedRole_ReturnsFalse()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([
            Script.Rows(EmployeeRow()), Script.Rows(Script.Row(("Value", 0))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new FakeAuthenticationStateProvider());

        Assert.False(await service.HasRoleAsync(43));
    }

    /// <summary>Ошибка identity не маскируется под допустимый отказ в правах.</summary>
    [Fact]
    public async Task IdentitySqlError_Throws()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([
            Script.Error(SqlExceptionFactory.Create(53, "connection error")),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new FakeAuthenticationStateProvider());

        await Assert.ThrowsAsync<SqlException>(() => service.HasRoleAsync(43));
    }

    /// <summary>Отсутствие серверного provider не считается доказательством роли.</summary>
    [Fact]
    public async Task MissingProvider_FailsClosedWithoutSql()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([]));
        var service = new EquipmentFormService(CreateDb(connection));

        Assert.False(await service.HasRoleAsync(43));
        Assert.Empty(connection.CommandLog);
    }

    /// <summary>CurrentUser загружается по совпавшему Windows/SQL principal.</summary>
    [Fact]
    public async Task CurrentUser_UsesTrustedIdentity()
    {
        var connection = CreateConnection(new Queue<ScriptEntry>([Script.Rows(EmployeeRow())]));
        var service = new EquipmentFormService(CreateDb(connection), new FakeAuthenticationStateProvider());

        var current = await service.GetCurrentUserAsync();

        Assert.NotNull(current);
        Assert.Equal(4, current.Id);
        Assert.Equal("DOMAIN\\devadmin", connection.ParameterLog[0]["WindowsPrincipal"]);
    }

    private static Dictionary<string, object?> EmployeeRow() => Script.Row(
        ("КодСотрудника", 4), ("Сотрудник", "Тестов Администратор"),
        ("Employee", "Test"), ("КодЛицаЗаказчика", 1));

    private static ScriptedConnection CreateConnection(Queue<ScriptEntry> entries) =>
        new(entries, emulateEquipmentIdentity: false);

    private static DbManager CreateDb(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
    }

    private sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
    {
        /// <inheritdoc />
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\devadmin")], "Negotiate");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
