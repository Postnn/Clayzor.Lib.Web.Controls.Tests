using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки периодов ответственности сотрудников.</summary>
public sealed class EquipmentResponsiblePersistenceTests
{
    /// <summary>Изъятие «На руках» атомарно создаёт расположение, закрывает период и добавляет следующего.</summary>
    [Fact]
    public void WithdrawWithNextAndLocation_ComposesCompositeTransaction()
    {
        var to = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var command = EquipmentResponsibleSaveBuilder.BuildWithdraw(
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 3, FromUtc = to.AddDays(-1), ToUtc = to },
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 4, FromUtc = to, ReturnDate = to.AddDays(2), Notes = "next" },
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 1005, FromUtc = to });

        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("INSERT ОборудованиеРасположение", command.Sql);
        Assert.Contains("UPDATE ОборудованиеСотрудников", command.Sql);
        Assert.Contains("INSERT ОборудованиеСотрудников", command.Sql);
        Assert.Contains("@ResponsibleReturn", command.Sql);
        Assert.Equal(4, command.Parameters.Get<int>("@КодСотрудника"));
        Assert.Equal(1005, command.Parameters.Get<int>("@КодРасположения"));
        Assert.Equal(to, command.Parameters.Get<DateTime>("@До"));
        Assert.Equal(to, command.Parameters.Get<DateTime>("@От"));
        Assert.Equal(to.AddDays(-1), command.Parameters.Get<DateTime>("@ТекущийОт"));
        Assert.Equal(to, command.Parameters.Get<DateTime>("@ОтСледующего"));
        Assert.Contains("SET @От = @ResponsibleFrom", command.Sql);
        Assert.Equal("next", command.Parameters.Get<string>("@Примечания"));
    }

    /// <summary>Простое изъятие не создаёт новых записей.</summary>
    [Fact]
    public void WithdrawWithoutNext_OnlyClosesCurrent()
    {
        var from = new DateTime(2026, 11, 17, 9, 0, 0);
        var to = new DateTime(2026, 11, 18, 10, 0, 0);
        var command = EquipmentResponsibleSaveBuilder.BuildWithdraw(
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 4, FromUtc = from, ToUtc = to }, null, null);

        Assert.DoesNotContain("INSERT ОборудованиеРасположение", command.Sql);
        Assert.DoesNotContain("INSERT ОборудованиеСотрудников", command.Sql);
        Assert.Contains("UPDATE ОборудованиеСотрудников", command.Sql);
        Assert.Equal(from, command.Parameters.Get<DateTime>("@ТекущийОт"));
        Assert.Equal(to, command.Parameters.Get<DateTime>("@До"));
    }

    /// <summary>История ответственных восстанавливает сотрудника, период и изменение.</summary>
    [Fact]
    public async Task GetResponsibles_MapsAllColumns()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 17, 9, 0, 0);
        var to = new DateTime(2026, 11, 18, 10, 0, 0);
        var changed = new DateTime(2026, 11, 18, 10, 5, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодОборудования", 7), ("От", from), ("До", to), ("КодСотрудника", 4),
                ("Сотрудник", "Employee"), ("СрокВозврата", to.AddDays(2)), ("Примечания", "note"),
                ("Изменил", 3), ("ИзменилФИО", "Editor"), ("Изменено", changed))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var responsibles = await service.GetResponsiblesAsync(7);

        var responsible = Assert.Single(responsibles);
        Assert.Equal(from, responsible.FromUtc);
        Assert.Equal(to, responsible.ToUtc);
        Assert.Equal(4, responsible.EmployeeId);
        Assert.Equal("Employee", responsible.EmployeeName);
        Assert.Equal("note", responsible.Notes);
        Assert.Equal(3, responsible.ChangedById);
        Assert.Equal("Editor", responsible.ChangedByName);
        Assert.Equal(changed, responsible.ChangedAtUtc);
    }

    /// <summary>Выдача использует INSERT, редактирование — UPDATE.</summary>
    [Fact]
    public async Task IssueAndUpdate_UseConfirmedSql()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        var responsible = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = new DateTime(2026, 11, 18, 9, 0, 0), Notes = "note",
        };

        await service.IssueResponsibleAsync(responsible);
        await service.UpdateResponsibleAsync(responsible);

        Assert.Contains("INSERT ОборудованиеСотрудников", connection.CommandLog[0]);
        Assert.Contains("UPDATE ОборудованиеСотрудников", connection.CommandLog[1]);
        Assert.DoesNotContain("INSERT", connection.CommandLog[1], StringComparison.OrdinalIgnoreCase);
    }
}
