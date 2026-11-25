using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки исходных версий и атомарной адресации дочерних записей EQ9.</summary>
public sealed class Eq9ChildConcurrencyTests
{
    /// <summary>Location edit передаёт исходную версию и проверяет cardinality до commit.</summary>
    [Fact]
    public async Task LocationUpdate_ComparesOriginalVersionInsideTransaction()
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var period = new EquipmentLocationPeriod { EquipmentId = 7, FromUtc = changedAt.AddDays(-1), ChangedAtUtc = changedAt };

        await period.UpdateAsync(CreateDb(connection));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
        Assert.True(sql.IndexOf("IF @@ROWCOUNT <> 1", StringComparison.Ordinal) < sql.IndexOf("COMMIT TRANSACTION", StringComparison.Ordinal));
        Assert.Equal(changedAt, Assert.Single(connection.ParameterLog)["ИсходноеИзменено"]);
    }

    /// <summary>Responsible edit и withdraw не принимают другую версию или уже закрытый период.</summary>
    [Fact]
    public async Task ResponsibleGuards_UseOriginalVersionAndOpenPeriod()
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var current = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, EmployeeId = 4, FromUtc = changedAt.AddDays(-1), ChangedAtUtc = changedAt,
        };
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));

        await current.UpdateAsync(CreateDb(connection));
        var command = EquipmentResponsibleSaveBuilder.BuildWithdraw(current, null,
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 1005, FromUtc = changedAt });

        Assert.Contains("AND Изменено = @ИсходноеИзменено", Assert.Single(connection.CommandLog));
        Assert.Contains("AND До IS NULL", command.Sql);
        Assert.Contains("AND Изменено = @ТекущееИзменено", command.Sql);
        Assert.Contains("КодРасположения = @РасположениеНаРуках", command.Sql);
        Assert.Equal(changedAt, command.Parameters.Get<DateTime>("@ТекущееИзменено"));
    }

    /// <summary>Компьютер различает expected existing и expected absent.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComputerSave_BindsExpectedExistenceAndVersion(bool expectedExisting)
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var snapshot = new EquipmentComputerSnapshot
        {
            EquipmentId = 7, Date = changedAt.Date, ExpectedExisting = expectedExisting, ChangedAt = changedAt,
        };

        await snapshot.SaveAsync(CreateDb(connection));

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(expectedExisting ? 1 : 0, parameters["ОжидаетсяСуществующий"]);
        if (expectedExisting)
            Assert.Equal(changedAt, parameters["ИсходноеИзменено"]);
        else
            Assert.Equal(DBNull.Value, parameters["ИсходноеИзменено"]);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.Contains("BEGIN CATCH", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
    }

    /// <summary>Лицензия сохраняет исходный baseline вместо безусловного UPSERT.</summary>
    [Fact]
    public async Task LicenseSave_ComparesOriginalVersion()
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var license = new EquipmentLicense { EquipmentId = 7, ExpectedExisting = true, ChangedAt = changedAt };

        await license.SaveAsync(CreateDb(connection));

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(1, parameters["ОжидаетсяСуществующий"]);
        Assert.Equal(changedAt, parameters["ИсходноеИзменено"]);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", Assert.Single(connection.CommandLog));
    }

    /// <summary>Software update не перемещает установку и отклоняет affected0.</summary>
    [Fact]
    public async Task SoftwareUpdate_BindsEquipmentAndRejectsMissingRow()
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(0)]));
        var installation = new EquipmentSoftwareInstallation
        {
            Id = 15, EquipmentId = 7, ChangedAt = changedAt,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => installation.SaveAsync(CreateDb(connection)));

        var sql = Assert.Single(connection.CommandLog);
        Assert.DoesNotContain("SET КодОборудования", sql);
        Assert.Contains("AND КодОборудования = @ИсходныйКодОборудования", sql);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
        Assert.Equal(changedAt, Assert.Single(connection.ParameterLog)["ИсходноеИзменено"]);
    }

    /// <summary>SIM service атомарно связан с текущей SIM и исходной версией услуги.</summary>
    [Fact]
    public async Task SimServiceUpdate_BindsCurrentSimAndOriginalVersion()
    {
        var changedAt = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var service = new EquipmentSimService
        {
            Id = 15, EquipmentId = 7, PhoneNumber = "79000000001", ChangedAt = changedAt,
        };

        await service.SaveAsync(CreateDb(connection));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("КодОборудования = @КодОборудования AND НомерТелефона = @НомерТелефона", sql);
        Assert.Contains("AND НомерТелефона = @НомерТелефона", sql);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
        Assert.Equal(changedAt, Assert.Single(connection.ParameterLog)["ИсходноеИзменено"]);
    }

    /// <summary>Добавление «На руках» проверяет active responsible в той же транзакции.</summary>
    [Fact]
    public void AddOnHands_ChecksResponsibleAtomically()
    {
        var command = EquipmentLocationSaveBuilder.BuildAdd(
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 55 }, null);

        var guard = command.Sql.IndexOf("NOT EXISTS", StringComparison.Ordinal);
        var insert = command.Sql.IndexOf("INSERT ОборудованиеРасположение", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < insert);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", command.Sql);
        Assert.Contains("BEGIN CATCH", command.Sql);
        Assert.Contains("ROLLBACK TRANSACTION", command.Sql);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
