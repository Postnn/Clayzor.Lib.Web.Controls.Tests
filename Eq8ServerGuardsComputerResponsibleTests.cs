using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки Computer и Responsible server guards EQ8-10/EQ8-13.</summary>
public sealed class Eq8ServerGuardsComputerResponsibleTests
{
    private static readonly DateTime Today = new(2026, 11, 20);

    /// <summary>Два фактических банка модели запрещают третий RAM-компонент до DML.</summary>
    [Fact]
    public async Task ComputerActualMemoryBanks_RejectThirdComponent()
    {
        var connection = ComputerConnection(memoryBanks: 2, hasComputer: 1);
        var snapshot = new EquipmentComputerSnapshot { EquipmentId = 7, RamComponents = [1024, 1024, 1024] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(connection).SaveComputerAsync(snapshot, Today));

        Assert.Contains("2", error.Message);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE Компьютеры"));
    }

    /// <summary>Удаление лишнего RAM исправляет тот же snapshot; daily upsert и details сохраняются.</summary>
    [Fact]
    public async Task ComputerCorrectedDraft_SavesWithActualBanksAndExplicitToday()
    {
        var connection = ComputerConnection(memoryBanks: 2, hasComputer: 1);
        var snapshot = new EquipmentComputerSnapshot
        {
            EquipmentId = 7,
            Date = Today.AddHours(8),
            RamComponents = [1024, 1024],
            HdComponents = [100.5m],
            Configuration1Id = 1,
            Configuration1Date = Today,
        };

        await CreateService(connection).SaveComputerAsync(snapshot, Today);

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Equal("1024+1024", snapshot.RamDetail);
        Assert.Contains("IF EXISTS", connection.CommandLog[2]);
        Assert.Equal(Today.Date, connection.ParameterLog[2]["С"]);
    }

    /// <summary>Noncomputer и будущая configuration date отклоняются до DML.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ComputerInvalidContextOrFutureDate_Rejected(byte hasComputer, bool futureDate)
    {
        var connection = ComputerConnection(memoryBanks: 2, hasComputer: hasComputer);
        var snapshot = new EquipmentComputerSnapshot
        {
            EquipmentId = 7,
            Configuration1Id = 1,
            Configuration1Date = futureDate ? Today.AddDays(1) : Today,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(connection).SaveComputerAsync(snapshot, Today));

        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE Компьютеры"));
    }

    /// <summary>Fresh current context запрещает следующего сотрудника, равного действующему.</summary>
    [Fact]
    public async Task WithdrawSameEmployee_RejectedUsingFreshCurrentContext()
    {
        var connection = WithdrawConnection(activeEmployeeId: 3);
        var current = Current(employeeId: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).WithdrawResponsibleAsync(
            current, Next(employeeId: 3, from: Today.AddDays(2), returnDate: Today.AddDays(3)), null));

        AssertNoWithdrawDml(connection);
    }

    /// <summary>ReturnDate следующего не раньше ЕГО FromUtc даже при разрыве после изъятия.</summary>
    [Fact]
    public async Task WithdrawNextReturnBeforeOwnStart_Rejected()
    {
        var connection = WithdrawConnection(activeEmployeeId: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).WithdrawResponsibleAsync(
            Current(3), Next(4, from: Today.AddDays(2), returnDate: Today.AddDays(1)), null));

        AssertNoWithdrawDml(connection);
    }

    /// <summary>Stale current DTO не заменяет свежий активный период.</summary>
    [Fact]
    public async Task WithdrawStaleCurrent_RejectedBeforeCompositeDml()
    {
        var connection = WithdrawConnection(activeEmployeeId: 3);
        var stale = Current(employeeId: 4);
        stale.FromUtc = stale.FromUtc.AddMilliseconds(500);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(connection).WithdrawResponsibleAsync(
            stale, Next(5, Today.AddDays(2), Today.AddDays(3)), null));

        AssertNoWithdrawDml(connection);
    }

    /// <summary>Допустимый разрыв и возврат после начала следующего сохраняют одну транзакцию.</summary>
    [Fact]
    public async Task WithdrawValidGap_ComposesAtomicSave()
    {
        DapperColumnMapper.Initialize();
        var connection = WithdrawConnection(activeEmployeeId: 3);

        await CreateService(connection).WithdrawResponsibleAsync(
            Current(3), Next(4, Today.AddDays(2), Today.AddDays(3)), null, Today.AddDays(2));

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Contains("BEGIN TRANSACTION", connection.CommandLog[2]);
        Assert.Contains("UPDATE ОборудованиеСотрудников", connection.CommandLog[2]);
        Assert.Contains("INSERT ОборудованиеСотрудников", connection.CommandLog[2]);
    }

    private static ScriptedConnection ComputerConnection(int memoryBanks, byte hasComputer) =>
        new(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(
                ("ModelId", 5), ("EquipmentTypeId", 1),
                ("HasComputerCharacteristics", hasComputer), ("MemoryBanks", memoryBanks))),
            Script.NonQuery(),
        ]));

    private static ScriptedConnection WithdrawConnection(int activeEmployeeId) =>
        new(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(
                ("КодОборудования", 7), ("От", Today.AddDays(-5)), ("До", null),
                ("КодСотрудника", activeEmployeeId), ("Сотрудник", "Current"))),
            Script.NonQuery(),
        ]));

    private static EquipmentResponsiblePeriod Current(int employeeId) => new()
    {
        EquipmentId = 7,
        EmployeeId = employeeId,
        FromUtc = Today.AddDays(-5),
        ToUtc = Today,
    };

    private static EquipmentResponsiblePeriod Next(int employeeId, DateTime from, DateTime returnDate) => new()
    {
        EquipmentId = 7,
        EmployeeId = employeeId,
        FromUtc = from,
        ReturnDate = returnDate,
    };

    private static void AssertNoWithdrawDml(ScriptedConnection connection)
    {
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE ОборудованиеСотрудников"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT ОборудованиеСотрудников"));
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection) =>
        new(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());
}
