using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки optimistic concurrency основной карточки и phone batch EQ8-06A.</summary>
public sealed class Eq8EquipmentConcurrencyTests
{
    /// <summary>Обычный UPDATE использует точный исходный UTC token с миллисекундами.</summary>
    [Fact]
    public async Task Update_UsesOriginalFractionalChangedAtToken()
    {
        var version = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(1)]));
        var equipment = new Equipment { Id = 7, ModelId = 5, OwnerId = 1, ChangedAtUtc = version, Description = "A notes" };

        await equipment.UpdateAsync(CreateDb(connection));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", sql);
        Assert.DoesNotContain("DATEADD", sql, StringComparison.OrdinalIgnoreCase);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(version, parameters["ИсходноеИзменено"]);
        Assert.Equal("A notes", parameters["Примечания"]);
    }

    /// <summary>V1 A/B: после B SN update A affected 0/ambiguous и получает явный конфликт без version reread.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Update_RejectsMissingStaleOrAmbiguousRow(int affectedRows)
    {
        var version = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery(affectedRows)]));
        var equipmentA = new Equipment
        {
            Id = 7,
            ModelId = 5,
            OwnerId = 1,
            SerialNumber = "SN-V1",
            ChangedAtUtc = version,
            Description = "A notes",
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => equipmentA.UpdateAsync(CreateDb(connection)));

        Assert.Contains("другим пользователем", error.Message);
        var sql = Assert.Single(connection.CommandLog);
        Assert.StartsWith("UPDATE", sql.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(version, Assert.Single(connection.ParameterLog)["ИсходноеИзменено"]);
    }

    /// <summary>Phone batch проверяет исходный token до DELETE/INSERT и откатывает весь TRY при конфликте.</summary>
    [Fact]
    public void PhoneBatch_ChecksOriginalVersionBeforePhoneDmlAndRollsBack()
    {
        var version = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var command = EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, ChangedAtUtc = version, Description = "A notes" },
            [Link(1)],
            [Link(2)]);

        Assert.Equal(version, command.Parameters.Get<DateTime>("@ИсходноеИзменено"));
        Assert.Contains("AND Изменено = @ИсходноеИзменено", command.Sql);
        var update = command.Sql.IndexOf("UPDATE vwОборудование", StringComparison.Ordinal);
        var guard = command.Sql.IndexOf("IF @@ROWCOUNT <> 1", update, StringComparison.Ordinal);
        var delete = command.Sql.IndexOf("DELETE FROM ТелефонныеНомераОборудование", StringComparison.Ordinal);
        var insert = command.Sql.IndexOf("INSERT INTO ТелефонныеНомераОборудование", StringComparison.Ordinal);
        var rollback = command.Sql.IndexOf("ROLLBACK TRANSACTION", insert, StringComparison.Ordinal);
        Assert.True(update >= 0 && update < guard);
        Assert.True(guard < delete && guard < insert);
        Assert.True(rollback > insert);
        Assert.Contains("BEGIN CATCH", command.Sql);
    }

    /// <summary>Phone service conflict не сообщает успех и не запускает АТС внутри write path.</summary>
    [Fact]
    public async Task PhoneServiceConflict_FailsWholeBatchWithoutAts()
    {
        var version = new DateTime(2026, 11, 18, 9, 30, 0, 500, DateTimeKind.Utc);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0))),
            Script.Error(SqlExceptionFactory.Create(50000, "Оборудование изменено другим пользователем.")),
        ]));
        var service = new Kesco.App.Web.Inventory.Services.EquipmentFormService(
            CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<SqlException>(() => service.UpdateWithPhonesAsync(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1, ChangedAtUtc = version },
            [Link(1)], [Link(2)]));

        Assert.Equal(3, connection.CommandLog.Count);
        Assert.Equal(version, connection.ParameterLog[2]["ИсходноеИзменено"]);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.OrdinalIgnoreCase));
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);

    private static EquipmentPhoneLink Link(int phoneId) => new() { PhoneId = phoneId, EquipmentId = 7 };
}
