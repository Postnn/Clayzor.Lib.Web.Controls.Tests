using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки точных UTC-ключей и конфликтов историй EQ8-06B.</summary>
public sealed class Eq8HistoryPrecisionTests
{
    /// <summary>Location edit сравнивает исходный От без округления и сохраняет миллисекунды параметра.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task LocationUpdate_UsesExactFractionalFromKey(int milliseconds)
    {
        var from = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var period = new EquipmentLocationPeriod
        {
            EquipmentId = 7,
            FromUtc = from,
            ReturnDate = from.AddDays(2),
            Notes = "changed",
        };

        await period.UpdateAsync(CreateDb(connection));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("AND От = @От", sql);
        Assert.DoesNotContain("DATEADD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DATEDIFF", sql, StringComparison.OrdinalIgnoreCase);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(from, parameters["От"]);
        Assert.Equal(7, parameters["КодОборудования"]);
        Assert.Equal(from.AddDays(2), parameters["СрокВозврата"]);
        Assert.Equal("changed", parameters["Примечания"]);
    }

    /// <summary>Ноль или несколько затронутых location-строк означают конфликт, а не успешное сохранение.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task LocationUpdate_RejectsMissingOrAmbiguousRow(int affectedRows)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(affectedRows)]));
        var period = new EquipmentLocationPeriod
        {
            EquipmentId = 7,
            FromUtc = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => period.UpdateAsync(CreateDb(connection)));
    }

    /// <summary>Responsible edit использует точный ключ и меняет только ReturnDate/Notes, не устаревшее До.</summary>
    [Fact]
    public async Task ResponsibleUpdate_PreservesConcurrentCloseAndExactFrom()
    {
        var from = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var period = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7,
            EmployeeId = 4,
            FromUtc = from,
            ToUtc = null,
            ReturnDate = from.AddDays(3),
            Notes = "changed",
        };

        await period.UpdateAsync(CreateDb(connection));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("AND От = @От", sql);
        Assert.DoesNotContain("DATEDIFF", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("До = @До", sql);
        Assert.Contains("SET СрокВозврата = @СрокВозврата, Примечания = @Примечания", sql);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(from, parameters["От"]);
        Assert.Equal(from.AddDays(3), parameters["СрокВозврата"]);
        Assert.Equal("changed", parameters["Примечания"]);
        Assert.DoesNotContain("До", parameters.Keys);
    }

    /// <summary>Ноль или несколько затронутых responsible-строк отклоняются.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ResponsibleUpdate_RejectsMissingOrAmbiguousRow(int affectedRows)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(affectedRows)]));
        var period = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7,
            FromUtc = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => period.UpdateAsync(CreateDb(connection)));
    }

    /// <summary>В withdraw точный UPDATE проверяется до next INSERT; ошибка попадает в общий rollback.</summary>
    [Fact]
    public void Withdraw_ChecksSingleExactCloseBeforeNextRowsAndRollsBackComposite()
    {
        var from = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var to = from.AddHours(1);
        var command = EquipmentResponsibleSaveBuilder.BuildWithdraw(
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 3, FromUtc = from, ToUtc = to },
            new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 4, FromUtc = to },
            new EquipmentLocationPeriod { EquipmentId = 7, LocationId = 1005, FromUtc = to });

        var sql = command.Sql;
        Assert.Contains("AND От = @ТекущийОт", sql);
        Assert.DoesNotContain("DATEDIFF(second", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(from, command.Parameters.Get<DateTime>("@ТекущийОт"));
        var locationInsert = sql.IndexOf("INSERT ОборудованиеРасположение", StringComparison.Ordinal);
        var closeUpdate = sql.IndexOf("UPDATE ОборудованиеСотрудников", StringComparison.Ordinal);
        var rowCountGuard = sql.IndexOf("IF @@ROWCOUNT <> 1", closeUpdate, StringComparison.Ordinal);
        var nextInsert = sql.IndexOf("INSERT ОборудованиеСотрудников", closeUpdate, StringComparison.Ordinal);
        var commit = sql.IndexOf("COMMIT TRANSACTION", closeUpdate, StringComparison.Ordinal);
        var rollback = sql.IndexOf("ROLLBACK TRANSACTION", commit, StringComparison.Ordinal);
        Assert.True(locationInsert >= 0 && locationInsert < closeUpdate);
        Assert.True(rowCountGuard > closeUpdate && rowCountGuard < nextInsert);
        Assert.True(nextInsert < commit);
        Assert.True(rollback > commit);
        Assert.Contains("BEGIN CATCH", sql);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
