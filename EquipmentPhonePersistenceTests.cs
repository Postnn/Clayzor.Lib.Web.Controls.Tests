using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки атомарного сохранения телефонных связей.</summary>
public sealed class EquipmentPhonePersistenceTests
{
    /// <summary>Diff строит DELETE, UPDATE и INSERT в одной транзакции с основным UPDATE.</summary>
    [Fact]
    public void Build_ComposesAtomicDiffWithMainUpdate()
    {
        var command = EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1 },
            [Link(1), Link(2)],
            [Link(2), Link(3), Link(4, ConnectionEquipmentId: 0)]);

        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("COMMIT TRANSACTION", command.Sql);
        Assert.Contains("ROLLBACK TRANSACTION", command.Sql);
        Assert.Contains("UPDATE vwОборудование", command.Sql);
        Assert.Contains("DELETE FROM ТелефонныеНомераОборудование", command.Sql);
        Assert.Contains("UPDATE ТелефонныеНомераОборудование", command.Sql);
        Assert.Contains("INSERT INTO ТелефонныеНомераОборудование", command.Sql);
        Assert.Equal(1, command.Parameters.Get<int>("@delPhone0"));
        Assert.Equal(2, command.Parameters.Get<int>("@updPhone0"));
        Assert.Equal(3, command.Parameters.Get<int>("@newPhone0"));
        Assert.Equal(4, command.Parameters.Get<int>("@newPhone1"));
        Assert.Null(command.Parameters.Get<int?>("@newConnection1"));
        Assert.DoesNotContain("001122", command.Sql);
    }

    /// <summary>Повтор одного номера отклоняется до обращения к БД.</summary>
    [Fact]
    public void Build_DuplicatePhone_Throws() =>
        Assert.Throws<InvalidOperationException>(() => EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7 }, [], [Link(1), Link(1)]));

    /// <summary>Чтение восстанавливает поля связи и срок возврата.</summary>
    [Fact]
    public async Task GetPhones_MapsAllColumns()
    {
        DapperColumnMapper.Initialize();
        var returnDate = new DateTime(2026, 12, 1);
        var changedAt = new DateTime(2026, 11, 18, 10, 0, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодТелефонногоНомера", 11), ("КодОборудования", 7), ("Описание", "101"),
                ("Основной", true), ("КодОборудованияПодключения", 8),
                ("НомерАвтонабора", "102"), ("ЗадержкаАвтонабора", (byte)2),
                ("СрокВозврата", returnDate), ("Примечание", "note"),
                ("Изменил", "Employee"), ("Изменено", changedAt))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var phones = await service.GetPhonesAsync(7);

        var phone = Assert.Single(phones);
        Assert.Equal(11, phone.PhoneId);
        Assert.Equal(7, phone.EquipmentId);
        Assert.True(phone.IsDefault);
        Assert.Equal(8, phone.ConnectionEquipmentId);
        Assert.Equal("102", phone.AutoDialNumber);
        Assert.Equal((byte)2, phone.AutoDialDelay);
        Assert.Equal(returnDate, phone.ReturnDate);
        Assert.Equal("note", phone.Notes);
        Assert.Equal(changedAt, phone.ChangedAt);
    }

    /// <summary>Оборудование подключения восстанавливается из legacy-запроса или остаётся NULL.</summary>
    [Fact]
    public async Task GetConnectionEquipmentId_ReturnsIdOrNull()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудованияПодключения", 9))),
            Script.Rows(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        Assert.Equal(9, await service.GetConnectionEquipmentIdAsync(11, 4, 55));
        Assert.Null(await service.GetConnectionEquipmentIdAsync(12, null, 55));
        Assert.Contains("P.КодТипаТелефонныхНомеров = 200", connection.CommandLog[0]);
        Assert.Contains("TARGET.КодРасположения = @LocationId", connection.CommandLog[0]);
    }

    private static EquipmentPhoneLink Link(int phoneId, int? ConnectionEquipmentId = null) => new()
    {
        PhoneId = phoneId,
        EquipmentId = 7,
        Description = phoneId.ToString(),
        ConnectionEquipmentId = ConnectionEquipmentId,
        Notes = string.Empty,
    };
}
