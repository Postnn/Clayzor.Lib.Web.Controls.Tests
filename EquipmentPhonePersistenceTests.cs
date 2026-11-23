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
            [Link(1), Link(2, ReturnDate: new DateTime(2026, 12, 1))],
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

    /// <summary>Неизменённая общая связь не обновляется и не затирает чужие изменения.</summary>
    [Fact]
    public void Build_SkipsUnchangedExistingPhone()
    {
        var command = EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1 },
            [Link(1, ReturnDate: new DateTime(2026, 12, 1), Notes: "old")],
            [Link(1, ReturnDate: new DateTime(2026, 12, 1), Notes: "old"), Link(2)]);

        Assert.DoesNotContain("UPDATE ТелефонныеНомераОборудование", command.Sql);
        Assert.DoesNotContain("@updPhone0", command.Parameters.ParameterNames);
        Assert.Equal(2, command.Parameters.Get<int>("@newPhone0"));
    }

    /// <summary>Новая связь сохраняет срок возврата и примечание в том же INSERT.</summary>
    [Fact]
    public void Build_SavesMetadataForNewPhone()
    {
        var returnDate = new DateTime(2026, 12, 31);
        var command = EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1 }, [],
            [Link(2, ConnectionEquipmentId: 8, ReturnDate: returnDate, Notes: "return to stock")]);

        Assert.Contains("СрокВозврата, Примечание", command.Sql);
        Assert.Equal(returnDate, command.Parameters.Get<DateTime?>("@newReturn0"));
        Assert.Equal("return to stock", command.Parameters.Get<string>("@newNotes0"));
    }

    /// <summary>Повтор одного номера отклоняется до обращения к БД.</summary>
    [Fact]
    public void Build_DuplicatePhone_Throws() =>
        Assert.Throws<InvalidOperationException>(() => EquipmentPhoneSaveBuilder.Build(
            new Equipment { Id = 7 }, [], [Link(1), Link(1)]));

    /// <summary>CCM получает только оборудование подключения; текущее оборудование добавляется лишь как fallback.</summary>
    [Fact]
    public async Task SynchronizePhones_MatchesLegacyRecipients()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 1), ("КодТелефоннойСтанции", 0), ("XorcomName", ""))),
            Script.NonQuery(), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.SynchronizePhonesAsync(7,
            [Link(1, ConnectionEquipmentId: 8)], [Link(1, ConnectionEquipmentId: 9)]);

        static object? EquipmentId(Dictionary<string, object?> parameters) =>
            parameters.GetValueOrDefault("@КодОборудования") ?? parameters["КодОборудования"];
        static bool HasEquipmentId(Dictionary<string, object?> parameters) =>
            parameters.ContainsKey("@КодОборудования") || parameters.ContainsKey("КодОборудования");
        var recipients = connection.ParameterLog.Where(HasEquipmentId).ToList();
        Assert.Equal([8, 9], recipients.Select(EquipmentId).ToArray());
        Assert.DoesNotContain(recipients, item => Equals(EquipmentId(item), 7));

        var fallbackConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 1), ("КодТелефоннойСтанции", 0), ("XorcomName", ""))),
            Script.NonQuery(),
        ]));
        var fallbackService = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => fallbackConnection), new TestAuthenticationStateProvider());
        await fallbackService.SynchronizePhonesAsync(7, [], [Link(1)]);
        Assert.Equal(7, EquipmentId(fallbackConnection.ParameterLog.Single(HasEquipmentId)));
    }

    /// <summary>При смешанном наборе Xorcom имеет приоритет и CCM не вызывается, как в legacy.</summary>
    [Fact]
    public async Task SynchronizePhones_WithXorcomAndCcm_SkipsCcmBranch()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 1), ("КодТелефоннойСтанции", 2), ("XorcomName", "X"))),
            Script.Scalar(0), Script.NonQuery(),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 2), ("КодТелефоннойСтанции", 0), ("XorcomName", ""))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.SynchronizePhonesAsync(7,
            [Link(1, ConnectionEquipmentId: 8)], [Link(2, ConnectionEquipmentId: 8)]);

        Assert.Contains(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync"));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("sp_CCM_PhoneSync"));
    }

    /// <summary>Синхронизация не затрагивает неизменённые номера и их оборудование подключения.</summary>
    [Fact]
    public async Task SynchronizePhones_UsesOnlyAffectedPhoneDiff()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1),
            Script.Rows(Script.Row(("КодТелефонногоНомера", 2), ("КодТелефоннойСтанции", 0), ("XorcomName", ""))),
            Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.SynchronizePhonesAsync(7,
            [Link(1, ConnectionEquipmentId: 8)],
            [Link(1, ConnectionEquipmentId: 8), Link(2, ConnectionEquipmentId: 9)]);

        Assert.Equal(3, connection.CommandLog.Count);
        var requestedPhone = Assert.Single(connection.ParameterLog, item => item.ContainsKey("Id"));
        Assert.Equal(2, requestedPhone["Id"]);
        Assert.DoesNotContain(connection.ParameterLog, item =>
            (item.GetValueOrDefault("@КодОборудования") ?? item.GetValueOrDefault("КодОборудования"))?.Equals(8) == true);
    }

    /// <summary>Изменение только срока возврата или примечания не вызывает команды АТС.</summary>
    [Fact]
    public async Task SynchronizePhones_IgnoresMetadataOnlyChanges()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var errors = await service.SynchronizePhonesAsync(7,
            [Link(1, ReturnDate: new DateTime(2026, 12, 1), Notes: "old")],
            [Link(1, ReturnDate: new DateTime(2026, 12, 2), Notes: "new")]);

        Assert.Empty(errors);
        Assert.Single(connection.CommandLog);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync"));
    }

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
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

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
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        Assert.Equal(9, await service.GetConnectionEquipmentIdAsync(11, 4, 55));
        Assert.Null(await service.GetConnectionEquipmentIdAsync(12, null, 55));
        Assert.Contains("P.КодТипаТелефонныхНомеров = 200", connection.CommandLog[0]);
        Assert.Contains("TARGET.КодРасположения = @LocationId", connection.CommandLog[0]);
    }

    private static EquipmentPhoneLink Link(
        int phoneId,
        int? ConnectionEquipmentId = null,
        DateTime? ReturnDate = null,
        string Notes = "") => new()
    {
        PhoneId = phoneId,
        EquipmentId = 7,
        Description = phoneId.ToString(),
        ConnectionEquipmentId = ConnectionEquipmentId,
        ReturnDate = ReturnDate,
        Notes = Notes,
    };
}
