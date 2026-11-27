using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки этапов записи периодов расположения и ответственности.</summary>
public sealed class EquipmentPeriodWritePhaseTests
{
    private static readonly DateTime Date = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Таймаут проверки прав или исходного периода возникает до любого DML.</summary>
    [Theory]
    [InlineData("add-location", false)]
    [InlineData("add-location", true)]
    [InlineData("add-location-expected", false)]
    [InlineData("update-location", false)]
    [InlineData("issue-responsible", false)]
    [InlineData("update-responsible", false)]
    [InlineData("withdraw-responsible", false)]
    [InlineData("withdraw-responsible", true)]
    public async Task TimeoutDuringPreflight_IsNotSent(string operation, bool roleAlreadyChecked)
    {
        var timeout = SqlExceptionFactory.Create(-2, "fixture preflight timeout");
        var steps = new List<ScriptEntry>();
        if (roleAlreadyChecked) steps.Add(Script.Scalar(1));
        steps.Add(Script.Error(timeout));
        using var connection = Connection(steps);
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => ExecuteAsync(service, operation));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Same(timeout, error.InnerException);
        Assert.Equal(roleAlreadyChecked ? 2 : 1, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, IsDml);
    }

    /// <summary>Сбой после передачи команды не разрешает повтор записи.</summary>
    [Theory]
    [InlineData("add-location", -2)]
    [InlineData("add-location-expected", -2)]
    [InlineData("update-location", -2)]
    [InlineData("issue-responsible", -2)]
    [InlineData("update-responsible", -2)]
    [InlineData("withdraw-responsible", -2)]
    [InlineData("update-location", 50000)]
    [InlineData("withdraw-responsible", 50000)]
    public async Task SqlErrorAfterDispatch_StaysUnknownWithoutReplay(string operation, int number)
    {
        // Неоднозначный пользовательский RAISERROR не является подтверждением rollback.
        var message = operation == "withdraw-responsible"
            ? "Период ответственности был изменен или удален другим пользователем. Обновите данные и повторите операцию."
            : "Период расположения был изменён или удалён другим пользователем.";
        var failure = SqlExceptionFactory.Create(number, message);
        var steps = new List<ScriptEntry> { Script.Scalar(1) };
        if (operation == "add-location") steps.Add(Script.Scalar(0));
        if (operation == "withdraw-responsible") steps.Add(CurrentRow());
        steps.Add(Script.Error(failure));
        using var connection = Connection(steps);
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() => ExecuteAsync(service, operation));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Same(failure, error.InnerException);
        var command = Assert.Single(connection.CommandLog, IsDml);
        if (operation != "issue-responsible")
            Assert.Contains("ROLLBACK TRANSACTION", command);
    }

    /// <summary>Однозначно изменившийся действующий ответственный не доходит до dispatch.</summary>
    [Fact]
    public async Task WithdrawRejectsChangedResponsibleWithoutDispatch()
    {
        using var connection = Connection([Script.Scalar(1), CurrentRow(changed: true)]);
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(service, "withdraw-responsible"));

        Assert.Contains("Действующий ответственный изменился", error.Message);
        Assert.DoesNotContain(connection.CommandLog, IsDml);
    }

    /// <summary>Подтверждённый нулевой rowcount — конфликт, а не неизвестный исход или повтор.</summary>
    [Theory]
    [InlineData("update-location")]
    [InlineData("update-responsible")]
    public async Task ConfirmedNoRows_ReportsConflictAndKeepsTheRollbackGuard(string operation)
    {
        using var connection = Connection([Script.Scalar(1), Script.NonQuery(0)]);
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(service, operation));

        Assert.Contains("изменён или удалён", error.Message);
        var command = Assert.Single(connection.CommandLog, IsDml);
        Assert.Contains("IF @@ROWCOUNT <> 1", command);
        Assert.Contains("ROLLBACK TRANSACTION", command);
    }

    private static ScriptEntry CurrentRow(bool changed = false) => Script.Rows(Script.Row(
        ("EquipmentId", 7), ("EmployeeId", 4), ("FromUtc", Date),
        ("ChangedAtUtc", changed ? Date.AddMinutes(1) : Date)));

    private static EquipmentFormService Service(ScriptedConnection connection) =>
        new(new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

    private static ScriptedConnection Connection(IEnumerable<ScriptEntry> entries) => new(new Queue<ScriptEntry>(entries));

    private static bool IsDml(string command) =>
        command.Contains("INSERT Оборудование", StringComparison.Ordinal) ||
        command.Contains("UPDATE Оборудование", StringComparison.Ordinal);

    private static Task ExecuteAsync(EquipmentFormService service, string operation)
    {
        var location = new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 14, FromUtc = Date, ChangedAtUtc = Date };
        var responsible = new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 4, FromUtc = Date, ChangedAtUtc = Date };
        switch (operation)
        {
            case "add-location": return service.AddLocationAsync(location);
            case "add-location-expected": return service.AddLocationAsync(location, null, false);
            case "update-location": return service.UpdateLocationAsync(location);
            case "issue-responsible": return service.IssueResponsibleAsync(responsible);
            case "update-responsible": return service.UpdateResponsibleAsync(responsible);
            case "withdraw-responsible":
                responsible.ToUtc = Date.AddDays(1);
                return service.WithdrawResponsibleAsync(responsible, null, null);
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}
