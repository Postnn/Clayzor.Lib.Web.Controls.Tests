using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки записи и чтения периодов расположения.</summary>
public sealed class EquipmentLocationPersistenceTests
{
    /// <summary>Добавление «На руках» сохраняет расположение и ответственного в одной транзакции.</summary>
    [Fact]
    public void AddWithResponsible_ComposesCompositeTransaction()
    {
        var from = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);
        var command = EquipmentLocationSaveBuilder.BuildAdd(
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 55, FromUtc = from, ReturnDate = null, Notes = "loc" },
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 4, FromUtc = from });

        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("INSERT ОборудованиеРасположение", command.Sql);
        Assert.Contains("INSERT ОборудованиеСотрудников", command.Sql);
        Assert.Contains("SimКартыВТелефоннойСтанции", command.Sql);
        Assert.Equal(55, command.Parameters.Get<int>("@КодРасположения"));
        Assert.Equal(4, command.Parameters.Get<int>("@КодСотрудника"));
        Assert.Equal(from, command.Parameters.Get<DateTime>("@От"));
        Assert.Equal("loc", command.Parameters.Get<string>("@Примечания"));
    }

    /// <summary>Без обязательного ответственного добавляется только расположение.</summary>
    [Fact]
    public void AddWithoutResponsible_DoesNotInsertEmployee()
    {
        var command = EquipmentLocationSaveBuilder.BuildAdd(
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 4, Notes = string.Empty }, null);

        Assert.DoesNotContain("INSERT ОборудованиеСотрудников", command.Sql);
        Assert.DoesNotContain("@КодСотрудника", command.Parameters.ParameterNames);
    }

    /// <summary>История расположений полностью восстанавливается маппером.</summary>
    [Fact]
    public async Task GetLocations_MapsAllColumns()
    {
        DapperColumnMapper.Initialize();
        var from = new DateTime(2026, 11, 18, 9, 30, 0);
        var changed = new DateTime(2026, 11, 18, 10, 0, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодОборудования", 7), ("От", from), ("КодРасположения", 4),
                ("РасположениеPath1", "CLAY_TEST / Room"), ("СрокВозврата", null),
                ("Примечания", "note"), ("Изменил", 4), ("ИзменилФИО", "Employee"), ("Изменено", changed))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var locations = await service.GetLocationsAsync(7);

        var location = Assert.Single(locations);
        Assert.Equal(7, location.EquipmentId);
        Assert.Equal(from, location.FromUtc);
        Assert.Equal(4, location.LocationId);
        Assert.Equal("CLAY_TEST / Room", location.LocationName);
        Assert.Equal("note", location.Notes);
        Assert.Equal(4, location.ChangedById);
        Assert.Equal("Employee", location.ChangedByName);
        Assert.Equal(changed, location.ChangedAtUtc);
    }

    /// <summary>Редактирование использует только UPDATE срока и примечаний.</summary>
    [Fact]
    public async Task Update_UsesConfirmedSql()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.UpdateLocationAsync(new EquipmentLocationPeriod
        {
            EquipmentId = 7, FromUtc = new DateTime(2026, 11, 18, 9, 30, 0), Notes = "changed",
        });

        Assert.Equal(2, connection.CommandLog.Count);
        var sql = connection.CommandLog[1];
        Assert.Contains("UPDATE ОборудованиеРасположение", sql);
        Assert.Contains("SET СрокВозврата = @СрокВозврата, Примечания = @Примечания", sql);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
    }
}
