using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки доверенного контекста и исходов внешней синхронизации EQ9.</summary>
public sealed class Eq9PhoneSyncTrustTests
{
    /// <summary>Null-plan planner повторно проверяет цель до внешнего вызова.</summary>
    [Fact]
    public async Task NullPlanRetry_ChangedTargetDoesNotReachAts()
    {
        var connection = CreateConnection(Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(PhoneRow(1, 9)));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizePhoneOperationsAsync(
            7, [], [Link(1)]));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Подложный deleted original не становится доказательством права на АТС.</summary>
    [Fact]
    public async Task ForgedDeletedOriginal_DoesNotReachAts()
    {
        var connection = CreateConnection(
            Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(), Script.Rows());
        var service = CreateService(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SynchronizePhoneOperationsAsync(
            7, [Link(1)], []));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Удалённый номер, уже назначенный другой карточке, не синхронизируется повторно.</summary>
    [Fact]
    public async Task ReassignedDeletedPhone_DoesNotReachAts()
    {
        var connection = CreateConnection(
            Script.Scalar(1), Script.Rows(EquipmentRow()), Script.Rows(), Script.Rows(Script.Row(("КодОборудования", 8))));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizePhoneOperationsAsync(
            7, [Link(1)], []));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Явная настройка требует роль по фактическому владельцу, а не ANY43.</summary>
    [Fact]
    public async Task ConfigureWrongOwner_DoesNotReachAts()
    {
        var connection = CreateConnection(
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодЛицаВладельца", 1), ("КодТипаОборудования", 30))),
            Script.Scalar(0));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfigurePhoneEquipmentAsync(7));

        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Timeout внешней процедуры является Unknown и не разрешает retry.</summary>
    [Fact]
    public async Task ExternalTimeout_ReturnsUnknownWithoutRetryPermission()
    {
        var connection = CreateConnection(Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(-2, "timeout")));
        var service = new PhoneFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var result = Assert.Single(await service.SynchronizeAsync(1, [Operation(1)]));

        Assert.Equal(PhoneSyncOutcome.Unknown, result.Status);
        Assert.True(result.IsUnknown);
        Assert.False(result.CanRetry);
    }

    /// <summary>Подтверждённый код ошибки процедуры остаётся Failed и допускает failed-only retry.</summary>
    [Fact]
    public async Task ConfirmedProcedureFailure_ReturnsFailedWithRetryPermission()
    {
        var connection = CreateConnection(Script.Scalar(1), Script.Error(new InvalidOperationException("Процедура вернула код 2.")));
        var service = new PhoneFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var result = Assert.Single(await service.SynchronizeAsync(1, [Operation(1)]));

        Assert.Equal(PhoneSyncOutcome.Failed, result.Status);
        Assert.False(result.IsUnknown);
        Assert.True(result.CanRetry);
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
