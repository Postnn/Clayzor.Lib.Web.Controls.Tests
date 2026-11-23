using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки свежей авторизации и актуальности контекста при повторе АТС.</summary>
public sealed class Eq8PhoneSyncRetryTests
{
    /// <summary>Отзыв роли запрещает повтор до чтения цели и внешнего вызова.</summary>
    [Fact]
    public async Task RevokedRole_DoesNotReachAts()
    {
        var connection = CreateConnection(Script.Scalar(0));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecutePhoneSyncOperationsAsync(
            7, [], [Link(1)], [Operation(1)]));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Изменение связи другой сессией сохраняет конфликт и не выполняет внешнюю операцию.</summary>
    [Fact]
    public async Task ChangedTarget_DoesNotReachAts()
    {
        var connection = CreateConnection(
            Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(PhoneRow(1, 9)));
        var service = CreateService(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecutePhoneSyncOperationsAsync(
            7, [], [Link(1)], [Operation(1)]));

        Assert.Contains("другой сессии", error.Message);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Переданная операция для чужого номера не считается подтверждённой серверным diff.</summary>
    [Fact]
    public async Task ForeignOperation_DoesNotReachAts()
    {
        var connection = CreateConnection(
            Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(PhoneRow(1, 8)));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecutePhoneSyncOperationsAsync(
            7, [], [Link(1)], [Operation(2)]));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Разрешённый повтор выполняет только переданную незавершённую операцию без UPDATE.</summary>
    [Fact]
    public async Task AllowedRetry_ExecutesOnlyPendingOperation()
    {
        var connection = CreateConnection(
            Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(PhoneRow(1, 8)),
            Script.Scalar(1), Script.NonQuery());
        var service = CreateService(connection);

        var results = await service.ExecutePhoneSyncOperationsAsync(
            7, [], [Link(1)], [Operation(1)]);

        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }

    private static ScriptedConnection CreateConnection(params ScriptEntry[] entries) =>
        new(new Queue<ScriptEntry>(entries));

    private static PhoneSyncOperation Operation(int phoneId) =>
        new(PhoneSyncKind.Xorcom, phoneId.ToString(), false);

    private static EquipmentPhoneLink Link(int phoneId) => new()
    {
        EquipmentId = 7, PhoneId = phoneId, ConnectionEquipmentId = 8,
    };

    private static Dictionary<string, object?> EquipmentRow() => Script.Row(("КодОборудования", 7));

    private static Dictionary<string, object?> PhoneRow(int phoneId, int connectionId) => Script.Row(
        ("КодТелефонногоНомера", phoneId), ("КодОборудования", 7),
        ("КодОборудованияПодключения", connectionId), ("Основной", false),
        ("НомерАвтонабора", null), ("ЗадержкаАвтонабора", null));
}
