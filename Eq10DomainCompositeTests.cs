using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Прямые production service checks атомарных location55/responsible контрактов EQ10.</summary>
public sealed class Eq10DomainCompositeTests
{
    /// <summary>Expected-no-active проверяется в той же транзакции до INSERT.</summary>
    [Fact]
    public async Task AddLocation_ExpectedNoActive_HasAtomicGuardBeforeInsert()
    {
        var from = Version().AddDays(-1);
        var connection = Connection(Script.Scalar(1), Script.NonQuery());
        var responsible = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = from,
        };

        await Service(connection).AddLocationAsync(Location(55, from), responsible, expectedActiveResponsible: false);

        var sql = Assert.Single(connection.CommandLog, text => text.Contains("BEGIN TRANSACTION", StringComparison.Ordinal));
        Assert.Contains("@ДействующихОтветственных <> @ОжидаетсяДействующийОтветственный", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.True(sql.IndexOf("@ДействующихОтветственных <>", StringComparison.Ordinal) <
                    sql.IndexOf("INSERT ОборудованиеРасположение", StringComparison.Ordinal));
        Assert.Equal(0, Parameter(connection.ParameterLog.Single(item =>
            HasParameter(item, "ОжидаетсяДействующийОтветственный")), "ОжидаетсяДействующийОтветственный"));
        Assert.Contains("ROLLBACK TRANSACTION", sql);
    }

    /// <summary>Existing active + move55 не создаёт лишнего responsible.</summary>
    [Fact]
    public async Task AddLocation_ActiveResponsibleMoveOnHands_DoesNotInsertResponsible()
    {
        var connection = Connection(Script.Scalar(1), Script.NonQuery());

        await Service(connection).AddLocationAsync(Location(55), null, expectedActiveResponsible: true);

        var sql = Assert.Single(connection.CommandLog, text => text.Contains("BEGIN TRANSACTION", StringComparison.Ordinal));
        Assert.DoesNotContain("INSERT ОборудованиеСотрудников", sql);
        Assert.Equal(1, Parameter(connection.ParameterLog.Single(item =>
            HasParameter(item, "ОжидаетсяДействующийОтветственный")), "ОжидаетсяДействующийОтветственный"));
    }

    /// <summary>Отзыв роли перед direct composite write даёт нулевой DML.</summary>
    [Fact]
    public async Task AddLocation_RevokedRole_ZeroDml()
    {
        var connection = Connection(Script.Scalar(0));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(connection).AddLocationAsync(
            Location(55), Responsible(4), expectedActiveResponsible: false));

        Assert.Single(connection.CommandLog);
        AssertNoDml(connection);
    }

    /// <summary>Direct AddLocation с неверной датой отклоняется до DML.</summary>
    [Fact]
    public async Task AddLocation_InvalidDate_ZeroDml()
    {
        var connection = Connection(Script.Scalar(1));
        var location = Location(55);
        location.FromUtc = default;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).AddLocationAsync(
            location, Responsible(4), expectedActiveResponsible: false));

        AssertNoDml(connection);
    }

    /// <summary>Direct Withdraw с null ToUtc отклоняется до active read и DML.</summary>
    [Fact]
    public async Task Withdraw_NullEndDate_ZeroDml()
    {
        var connection = Connection(Script.Scalar(1));
        var current = Current();
        current.ToUtc = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).WithdrawResponsibleAsync(
            current, null, null));

        Assert.Single(connection.CommandLog);
        AssertNoDml(connection);
    }

    /// <summary>Direct Withdraw с чужим equipment identity отклоняется до DML.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Withdraw_MismatchedEquipment_ZeroDml(bool mismatchedLocation)
    {
        var connection = Connection(Script.Scalar(1));
        var next = mismatchedLocation ? null : Responsible(5);
        var location = mismatchedLocation ? Location(1005) : null;
        if (next is not null) next.EquipmentId = 999;
        if (location is not null) location.EquipmentId = 999;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).WithdrawResponsibleAsync(
            Current(), next, location));

        Assert.Single(connection.CommandLog);
        AssertNoDml(connection);
    }

    /// <summary>Stale active ChangedAt не приводит к close/next INSERT.</summary>
    [Fact]
    public async Task Withdraw_StaleActiveVersion_ZeroDml()
    {
        var current = Current();
        var fresh = current.ChangedAtUtc.AddSeconds(1);
        var connection = Connection(Script.Scalar(1), Script.Rows(ActiveRow(current, fresh)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(connection).WithdrawResponsibleAsync(
            current, Responsible(5), null));

        AssertNoDml(connection);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT ОборудованиеСотрудников", StringComparison.Ordinal));
    }

    /// <summary>Valid withdraw сохраняет raw timestamps, CAS и допустимые равные границы периодов.</summary>
    [Fact]
    public async Task Withdraw_Valid_PreservesRawTimestampAndEqualPeriodBoundary()
    {
        var current = Current();
        var next = Responsible(5);
        next.FromUtc = current.ToUtc!.Value;
        next.ReturnDate = next.FromUtc.Date;
        var location = Location(1005);
        location.FromUtc = current.ToUtc.Value;
        var connection = Connection(Script.Scalar(1), Script.Rows(ActiveRow(current, current.ChangedAtUtc)), Script.NonQuery());

        await Service(connection).WithdrawResponsibleAsync(Current(current.FromUtc, current.ToUtc, current.ChangedAtUtc),
            next, location, next.FromUtc.Date);

        var parameters = connection.ParameterLog.Single(item => HasParameter(item, "ТекущееИзменено"));
        Assert.Equal(current.ChangedAtUtc, Parameter(parameters, "ТекущееИзменено"));
        Assert.Equal(current.ToUtc, Parameter(parameters, "До"));
        Assert.Equal(next.FromUtc, Parameter(parameters, "ОтСледующего"));
        var sql = connection.CommandLog.Single(text => text.Contains("UPDATE ОборудованиеСотрудников", StringComparison.Ordinal));
        Assert.Contains("AND Изменено = @ТекущееИзменено", sql);
        Assert.Contains("AND До IS NULL", sql);
        Assert.Contains("IF @@ROWCOUNT <> 1", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
    }

    /// <summary>Withdraw с actual55 требует новое location≠55 даже при next employee.</summary>
    [Fact]
    public void WithdrawBuilder_ActualOnHands_RequiresDifferentLocation()
    {
        var command = EquipmentResponsibleSaveBuilder.BuildWithdraw(Current(), Responsible(5), Location(55));

        Assert.Contains("КодРасположения = @РасположениеНаРуках", command.Sql);
        Assert.Contains("@КодРасположения IS NULL OR @КодРасположения = @РасположениеНаРуках", command.Sql);
        Assert.Contains("укажите новое расположение", command.Sql);
    }

    private static EquipmentFormService Service(ScriptedConnection connection)
    {
        Clayzor.Lib.Entities.MedicalTests.DapperColumnMapper.Initialize();
        return new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }

    private static ScriptedConnection Connection(params ScriptEntry[] entries) => new(new Queue<ScriptEntry>(entries));

    private static DateTime Version() => new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(500);

    private static EquipmentLocationPeriod Location(int locationId, DateTime? from = null) => new()
    {
        EquipmentId = 7, LocationId = locationId, FromUtc = from ?? Version().AddHours(-1),
    };

    private static EquipmentResponsiblePeriod Responsible(int employeeId) => new()
    {
        EquipmentId = 7, EmployeeId = employeeId, FromUtc = Version().AddHours(-1),
    };

    private static EquipmentResponsiblePeriod Current(DateTime? from = null, DateTime? to = null, DateTime? changedAt = null) => new()
    {
        EquipmentId = 7,
        EmployeeId = 4,
        FromUtc = from ?? Version().AddDays(-2),
        ToUtc = to ?? Version(),
        ChangedAtUtc = changedAt ?? Version(),
    };

    private static Dictionary<string, object?> ActiveRow(EquipmentResponsiblePeriod current, DateTime changedAt) => Script.Row(
        ("КодОборудования", 7), ("От", current.FromUtc), ("До", null),
        ("КодСотрудника", current.EmployeeId), ("Сотрудник", "Current"), ("Изменено", changedAt));

    private static void AssertNoDml(ScriptedConnection connection) =>
        Assert.DoesNotContain(connection.CommandLog, sql =>
            sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));

    private static bool HasParameter(IReadOnlyDictionary<string, object?> parameters, string name) =>
        parameters.Keys.Any(key => key.TrimStart('@') == name);

    private static object? Parameter(IReadOnlyDictionary<string, object?> parameters, string name) =>
        parameters.Single(item => item.Key.TrimStart('@') == name).Value;
}
