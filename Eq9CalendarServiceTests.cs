using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки согласованного календарного дня EQ9-11 на серверной границе записи.</summary>
public sealed class Eq9CalendarServiceTests
{
    /// <summary>Будущая относительно business Today дата SIM отклоняется независимо от серверного Today.</summary>
    [Fact]
    public async Task SaveSimService_AfterBusinessToday_RejectedBeforeDml()
    {
        var businessToday = new DateTime(2099, 2, 28);
        var connection = SimServiceConnection();
        var service = CreateService(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSimServiceAsync(
            SimService(businessToday.AddDays(1)), businessToday));

        Assert.Equal("Фактические даты не могут быть в будущем.", error.Message);
        AssertNoDml(connection);
    }

    /// <summary>Одинаковые даты начала и окончания SIM сохраняют inclusive UI и exclusive DB границы.</summary>
    [Fact]
    public async Task SaveSimService_SameBusinessDay_PreservesExclusiveDatabaseEnd()
    {
        var businessToday = new DateTime(2099, 3, 1);
        var connection = SimServiceConnection();
        var service = CreateService(connection);
        var value = SimService(businessToday.AddHours(12));
        value.DateTo = businessToday.AddHours(18);

        await service.SaveSimServiceAsync(value, businessToday);

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Equal(businessToday.Date, connection.ParameterLog[2]["От"]);
        Assert.Equal(businessToday.Date.AddDays(1), connection.ParameterLog[2]["До"]);
    }

    /// <summary>Отсутствие business Today не заменяется серверной датой.</summary>
    [Fact]
    public async Task SaveSimService_WithoutBusinessToday_FailsClosed()
    {
        var connection = SimServiceConnection();
        var service = CreateService(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveSimServiceAsync(SimService(new DateTime(2099, 3, 1))));

        Assert.Contains("Календарная дата клиента не задана", error.Message);
        AssertNoDml(connection);
    }

    /// <summary>Возврат в локальный день начала допускается при offset 0, +180 и −180 через границу месяца.</summary>
    [Theory]
    [InlineData(0, 3, 1, 1)]
    [InlineData(180, 3, 1, 1)]
    [InlineData(-180, 2, 28, 22)]
    public async Task WithdrawResponsible_SameLocalStartDay_AllowedAcrossUtcDateBoundary(
        int offsetMinutes, int month, int day, int hour)
    {
        var localStart = new DateTime(2099, month, day, hour, 0, 0);
        var currentFrom = new DateTime(2099, 2, 20, 12, 0, 0, DateTimeKind.Utc);
        var connection = ResponsibleConnection(currentFrom);
        var next = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7,
            EmployeeId = 4,
            FromUtc = localStart.AddMinutes(-offsetMinutes),
            ReturnDate = localStart.Date,
        };

        await CreateService(connection).WithdrawResponsibleAsync(
            Current(currentFrom), next, null, localStart.Date);

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Contains("BEGIN TRANSACTION", connection.CommandLog[2]);
    }

    /// <summary>Локальный вчерашний день отклоняется, даже если он совпадает с UTC-датой начала.</summary>
    [Fact]
    public async Task WithdrawResponsible_ReturnBeforeLocalStart_RejectedDespiteUtcMatch()
    {
        var localStart = new DateTime(2099, 3, 1, 1, 0, 0);
        var currentFrom = new DateTime(2099, 2, 20, 12, 0, 0, DateTimeKind.Utc);
        var connection = ResponsibleConnection(currentFrom);
        var next = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7,
            EmployeeId = 4,
            FromUtc = localStart.AddHours(-3),
            ReturnDate = localStart.Date.AddDays(-1),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(connection).WithdrawResponsibleAsync(Current(currentFrom), next, null, localStart.Date));

        Assert.Equal("Срок возврата следующего ответственного не может быть раньше его выдачи.", error.Message);
        AssertNoDml(connection);
    }

    /// <summary>Прежний API не восстанавливает локальный день из UTC и отклоняет неоднозначную запись.</summary>
    [Fact]
    public async Task WithdrawResponsible_WithoutLocalStartDate_FailsClosed()
    {
        var currentFrom = new DateTime(2099, 2, 20, 12, 0, 0, DateTimeKind.Utc);
        var connection = ResponsibleConnection(currentFrom);
        var next = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7,
            EmployeeId = 4,
            FromUtc = new DateTime(2099, 2, 28, 22, 0, 0, DateTimeKind.Utc),
            ReturnDate = new DateTime(2099, 2, 28),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(connection).WithdrawResponsibleAsync(Current(currentFrom), next, null));

        Assert.Contains("Локальная календарная дата", error.Message);
        AssertNoDml(connection);
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection) =>
        new(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

    private static EquipmentSimService SimService(DateTime from) => new()
    {
        EquipmentId = 7,
        PhoneNumber = "79000000001",
        ServiceTypeId = "_TEST",
        ServiceName = "Test",
        DateFrom = from,
    };

    private static ScriptedConnection SimServiceConnection() => new(new Queue<ScriptEntry>([
        Script.Scalar(1),
        Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", (byte)1))),
        Script.NonQuery(),
    ]));

    private static EquipmentResponsiblePeriod Current(DateTime from) => new()
    {
        EquipmentId = 7,
        EmployeeId = 3,
        FromUtc = from,
        ToUtc = new DateTime(2099, 3, 1, 1, 0, 0, DateTimeKind.Utc),
    };

    private static ScriptedConnection ResponsibleConnection(DateTime currentFrom) => new(new Queue<ScriptEntry>([
        Script.Scalar(1),
        Script.Rows(Script.Row(
            ("КодОборудования", 7), ("От", currentFrom), ("До", null),
            ("КодСотрудника", 3), ("Сотрудник", "Current"))),
        Script.NonQuery(),
    ]));

    private static void AssertNoDml(ScriptedConnection connection)
    {
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql =>
            sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
    }
}
