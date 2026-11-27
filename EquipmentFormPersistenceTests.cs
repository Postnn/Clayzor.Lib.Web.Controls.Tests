using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки контракта чтения и обновления основной записи оборудования.</summary>
public sealed class EquipmentFormPersistenceTests
{
    /// <summary>UPDATE содержит ключ и ровно 15 изменяемых полей legacy-контракта.</summary>
    [Fact]
    public void UpdateParameters_MatchLegacyContract()
    {
        var writtenOff = new DateTime(2026, 11, 17, 12, 30, 0, DateTimeKind.Utc);
        var equipment = new Equipment
        {
            Id = 7, SerialNumber = "SN", CameraIsConnected = 1, WriteOffAtUtc = writtenOff,
            DocumentWriteOffId = 8, NetworkName = "host", MacAddress = "001122334455",
            MacAddress2 = "66778899AABB", MacAddressIlo = "CCDDEEFF0011", Description = "note",
            OwnerId = 1, TenantId = 2, ShipperId = 3, PhonePoolId = 4, ModelId = 5,
            DocumentReceiptOuId = 6,
        };

        var parameters = equipment.CreateUpdateParameters();

        Assert.Equal(16, parameters.ParameterNames.Count());
        Assert.Equal(7, parameters.Get<int>("@КодОборудования"));
        Assert.Equal((byte)1, parameters.Get<byte>("@ПодключенаКамера"));
        Assert.Equal(writtenOff, parameters.Get<DateTime>("@Списано"));
        Assert.Equal(8, parameters.Get<int>("@КодДокументаСписания"));
        Assert.Equal(4, parameters.Get<int>("@КодПулаТелефонов"));
        Assert.Equal(5, parameters.Get<int>("@КодМоделиОборудования"));
        Assert.Equal(6, parameters.Get<int>("@КодДокументаПоступления"));
    }

    /// <summary>Обновление использует параметризованный UPDATE и не содержит INSERT.</summary>
    [Fact]
    public async Task Update_UsesConfirmedSql()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0))), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await service.UpdateAsync(new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 });

        Assert.Equal(3, connection.CommandLog.Count);
        var sql = connection.CommandLog[2];
        Assert.Contains("UPDATE vwОборудование", sql);
        Assert.Contains("WHERE КодОборудования = @КодОборудования", sql);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Отсутствие затронутой записи не считается успешным сохранением.</summary>
    [Fact]
    public async Task UpdateWithoutAffectedRow_Throws()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0))), Script.NonQuery(0),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 }));
    }

    /// <summary>Прямой backend-вызов без владельца отклоняется до UPDATE.</summary>
    [Fact]
    public async Task UpdateWithoutOwner_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(1), Script.NonQuery()]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(new Equipment { Id = 7, ModelId = 5 }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Прямой backend-вызов с обязательным пустым SN отклоняется после чтения типа.</summary>
    [Fact]
    public async Task UpdateWithoutRequiredSn_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)1), ("ЕстьMACадрес", (byte)0))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1 }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Прямой backend-вызов с неверным MAC отклоняется после чтения типа.</summary>
    [Fact]
    public async Task UpdateWithInvalidMac_ThrowsBeforeSqlWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)1))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            new Equipment { Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1, MacAddress = "invalid" }));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование"));
    }

    /// <summary>Полная выборка восстанавливает основные поля, модель и текущие связи.</summary>
    [Fact]
    public async Task Get_MapsAllEditableFieldsAndDisplayMetadata()
    {
        DapperColumnMapper.Initialize();
        var writtenOff = new DateTime(2026, 11, 17, 12, 30, 0);
        var changed = new DateTime(2026, 11, 18, 8, 15, 0);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодОборудования", 7), ("КодМоделиОборудования", 5), ("КодТипаОборудования", 30),
                ("МодельОборудования", "Phone"), ("PN", "PN-1"), ("ТипОборудования", "IP-телефон"),
                ("SN", "SN"), ("ПодключенаКамера", (byte)1), ("Списано", writtenOff),
                ("КодДокументаСписания", 8), ("СетевоеИмя", "host"), ("MACадрес", "001122334455"),
                ("MACадрес2", "66778899AABB"), ("MACадресILO", "CCDDEEFF0011"),
                ("Примечания", "note"), ("КодЛицаВладельца", 1), ("КодЛицаАрендатора", 2),
                ("КодЛицаПоставщика", 3), ("КодПулаТелефонов", 4), ("КодДокументаПоступления", 6),
                ("Изменил", 4), ("Изменено", changed), ("ИзменилФИО", "Тестов А."),
                ("КодРасположения", 55), ("КодСотрудника", 9))),
        ]));
        var service = new EquipmentFormService(CreateDb(connection), new TestAuthenticationStateProvider());

        var equipment = await service.GetAsync(7);

        Assert.NotNull(equipment);
        Assert.Equal(30, equipment.EquipmentTypeId);
        Assert.Equal("Phone", equipment.ModelName);
        Assert.Equal("PN-1", equipment.PartNumber);
        Assert.Equal("IP-телефон", equipment.EquipmentTypeName);
        Assert.Equal((byte)1, equipment.CameraIsConnected);
        Assert.Equal(writtenOff, equipment.WriteOffAtUtc);
        Assert.Equal(8, equipment.DocumentWriteOffId);
        Assert.Equal(4, equipment.PhonePoolId);
        Assert.Equal(6, equipment.DocumentReceiptOuId);
        Assert.Equal(4, equipment.ChangedById);
        Assert.Equal(changed, equipment.ChangedAtUtc);
        Assert.Equal("Тестов А.", equipment.ChangedByName);
        Assert.Equal(55, equipment.LocationId);
        Assert.Equal(9, equipment.EmployeeId);
    }

    /// <summary>Роль 43 заново проверяется внутри транзакции, любой DML при отзыве запрещён.</summary>
    [Fact]
    public async Task MainJournaledUpdateRechecksRoleInsideTransaction()
    {
        var equipment = JournaledEquipment();
        var (service, connection, journal) = JournaledService(equipment, grantInside: false, retry: false);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.UpdateAsync(equipment, 4, attemptId, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, journal.StateFor(attemptId));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
    }

    /// <summary>Потеря ответа COMMIT оставляет один SQL-результат и исходный серверный план АТС.</summary>
    [Fact]
    public async Task JournaledPhoneSaveLostCommitDoesNotRepeatDmlOrCreateAnotherAtsIntent()
    {
        var equipment = JournaledEquipment();
        var attemptId = Guid.NewGuid();
        var (service, connection, journal) = JournaledService(equipment, grantInside: true, retry: true);
        journal.LoseNextConfirmedCommitReply = true;
        EquipmentPhoneLink[] phones = [new() { EquipmentId = 7, PhoneId = 12, ConnectionEquipmentId = 9 }];

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveWithPhonesAsync(equipment, [], phones, 4, attemptId, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, journal.StateFor(attemptId));
        Assert.Equal(new[] { "equipment/9", "tel/12" }, journal.ScopesFor(attemptId).Order(StringComparer.Ordinal));

        var receipt = await service.ReadMainWriteReceiptAsync(7, 4, attemptId, includesPhones: true, default);
        Assert.NotNull(receipt);
        Assert.Equal(InventoryOperationState.Confirmed, receipt.State);
        Assert.Equal(7, receipt.ResultEntityId);
        var replay = await service.SaveWithPhonesAsync(equipment, [], phones, 4, attemptId, default);

        Assert.NotNull(replay);
        Assert.True(replay.DurableSave);
        Assert.Equal(attemptId.ToString("N"), replay.AttemptId);
        Assert.Equal(7, replay.EquipmentId);
        var sql = Assert.Single(connection.CommandLog, value => value.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
        Assert.Contains("INSERT INTO ТелефонныеНомераОборудование", sql);
        Assert.DoesNotContain("BEGIN TRANSACTION", sql);
        Assert.DoesNotContain("COMMIT TRANSACTION", sql);

        var header = Assert.Single(ParametersFor(SQLQueries.INSERT_EquipmentAtsSavedWorkflow));
        Assert.Equal(attemptId, header["AttemptId"]);
        Assert.Equal(4, header["EmployeeId"]);
        Assert.Equal(7, header["EquipmentId"]);
        Assert.Equal(1, header["OwnerId"]);
        Assert.Equal(equipment.ChangedAtUtc.AddMinutes(1), header["ExpectedChangedAtUtc"]);
        Assert.Equal(false, header["PlanPrepared"]);
        var linksFingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { PhoneId = 12, ConnectionEquipmentId = (int?)9, AutoDialNumber = "", AutoDialDelay = (byte?)null, IsDefault = false },
        })));
        Assert.Equal(linksFingerprint, header["ContextFingerprint"]);

        var scopes = ParametersFor(SQLQueries.INSERT_EquipmentAtsScope).ToArray();
        Assert.Equal(new[] { "equipment/7", "equipment/9", "tel/12" }, scopes.Select(item => (string)item["ConflictKey"]!));
        Assert.All(scopes, scope =>
        {
            Assert.Equal(attemptId, scope["AttemptId"]);
            Assert.Equal(Guid.Empty, scope["OpenSlot"]);
        });

        var candidates = ParametersFor(SQLQueries.INSERT_EquipmentAtsSaveCandidate).ToArray();
        Assert.All(candidates, target => Assert.Equal(attemptId, target["AttemptId"]));
        Assert.Equal(new[] { ((byte)1, 12, false), ((byte)2, 9, false) }, candidates
            .Select(target => ((byte)target["Kind"]!, (int)target["TargetId"]!, (bool)target["Deleting"]!))
            .OrderBy(target => target.Item1));
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Xorcom, connection.CommandLog);
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Ccm, connection.CommandLog);
        Assert.DoesNotContain(SQLQueries.UPDATE_EquipmentAtsStepState, connection.CommandLog);

        IEnumerable<Dictionary<string, object?>> ParametersFor(string statement) =>
            connection.CommandLog.Zip(connection.ParameterLog).Where(item => item.First == statement).Select(item => item.Second);
    }

    /// <summary>Изменённая сервером цель вне резерва блокирует подтверждение сохранения и постановку АТС.</summary>
    [Fact]
    public async Task JournaledPhoneSaveRejectsUnreservedPostWriteCcmTarget()
    {
        var equipment = JournaledEquipment();
        var (service, connection, journal) = JournaledService(equipment, grantInside: true, retry: true, acceptedCcm: 13);
        var id = Guid.NewGuid();
        EquipmentPhoneLink[] submitted = [new() { EquipmentId = 7, PhoneId = 12, ConnectionEquipmentId = 9 }];

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveWithPhonesAsync(equipment, [], submitted, 4, id, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, journal.StateFor(id));
        Assert.DoesNotContain(SQLQueries.INSERT_EquipmentAtsSavedWorkflow, connection.CommandLog);
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Ccm, connection.CommandLog);
    }

    private static Equipment JournaledEquipment() => new()
    {
        Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 1,
        ChangedAtUtc = new DateTime(2026, 11, 18, 8, 15, 0),
    };

    private static (EquipmentFormService Service, ScriptedConnection Connection, NetFormTests.NetJournalConnection Journal)
        JournaledService(Equipment equipment, bool grantInside, bool retry, int acceptedCcm = 9)
    {
        DapperColumnMapper.Initialize();
        var actor = Script.Rows(Script.Row(("КодСотрудника", 4)));
        var roles = Script.Rows(Script.Row(
            ("RoleId", (int)Clayzor.Lib.Entities.Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)));
        var row = Script.Rows(Script.Row(("КодОборудования", equipment.Id), ("Изменено", equipment.ChangedAtUtc)));
        var type = Script.Rows(Script.Row(("КодТипаОборудования", 1), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0)));
        var entries = new Queue<ScriptEntry>([actor, roles, row, type, actor, grantInside ? roles : Script.Rows()]);
        if (grantInside) entries.Enqueue(Script.Scalar(0));
        if (retry)
        {
            var changed = equipment.ChangedAtUtc.AddMinutes(1);
            var fresh = Script.Rows(Script.Row(("КодОборудования", equipment.Id),
                ("КодЛицаВладельца", equipment.OwnerId), ("КодТипаОборудования", equipment.EquipmentTypeId), ("Изменено", changed)));
            var links = Script.Rows(Script.Row(("КодТелефонногоНомера", 12),
                ("КодОборудования", equipment.Id), ("КодОборудованияПодключения", acceptedCcm)));
            // NetJournalConnection handles generic reserve, linked scopes and confirmed receipt in memory.
            // Workflow metadata writes and the native data reads below are scripted.
            foreach (var entry in new[] { fresh, links, Script.Scalar(1),
                Script.Scalar(1), Script.Scalar(1), Script.Scalar(1), Script.Scalar(1), Script.Scalar(1),
                actor, roles, fresh, actor, roles, fresh })
                entries.Enqueue(entry);
        }
        var connection = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(connection);
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => journal),
            new TestAuthenticationStateProvider());
        return (service, connection, journal);
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
