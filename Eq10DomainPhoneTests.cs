using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки trusted phone diff, Configure scope и per-operation retry EQ10.</summary>
public sealed class Eq10DomainPhoneTests
{
    /// <summary>Caller original без Save не создаёт старый CCM recipient.</summary>
    [Fact]
    public async Task Planner_ForgedOriginalWithoutSave_RejectedBeforeReadsAndAts()
    {
        var connection = Connection();
        var service = Service(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SynchronizePhoneOperationsAsync(
            7, [Link(1, 888)], [Link(1, 8)]));

        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Ready-plan не может представиться failed операцией незарегистрированного attempt.</summary>
    [Fact]
    public async Task ReadyPlan_UnregisteredAttempt_RejectedBeforeReadsAndAts()
    {
        var connection = Connection();
        var service = Service(connection);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExecutePhoneSyncOperationsAsync(
            7, [], [Link(1, 8)], [Xorcom(1)]));

        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Changed/deleted SQL получает фактический before и exact CAS, а не caller fingerprint.</summary>
    [Fact]
    public void PhoneSaveBuilder_ChangedAndDeleted_GuardEveryBeforeTarget()
    {
        var version = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(500);
        var command = EquipmentPhoneSaveBuilder.Build(Equipment(version),
            [Link(1, 8), Link(2, 10)], [Link(1, 9)]);

        Assert.Contains("ОжидаемоеКоличествоСвязей", command.Sql);
        Assert.Contains("КодОборудованияПодключения = @before0Connection", command.Sql);
        Assert.Contains("AND Изменено = @ИсходноеИзменено", command.Sql);
        Assert.Contains("SET КодОборудованияПодключения = @update0Connection", command.Sql);
        Assert.Contains("КодОборудованияПодключения = @update0OldConnection", command.Sql);
        Assert.Equal(8, command.Parameters.Get<int>("@before0Connection"));
        Assert.Equal(9, command.Parameters.Get<int>("@update0Connection"));
        Assert.Equal(8, command.Parameters.Get<int>("@update0OldConnection"));
        Assert.Equal(2, command.Parameters.Get<int>("@delPhone0"));
        Assert.Equal(version, command.Parameters.Get<DateTime>("@ИсходноеИзменено"));
        Assert.True(command.Sql.IndexOf("ROLLBACK TRANSACTION", StringComparison.Ordinal) > 0);
    }

    /// <summary>Valid changed CCM выполняет именно trusted old/new recipients после Save.</summary>
    [Fact]
    public async Task ChangedCcm_AfterAtomicSave_SendsOldAndNewTargets()
    {
        var connection = Connection(
            Script.Scalar(1), Script.Rows(ModelTypeRow()), Script.NonQuery(),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(PhoneRow(1, 9)),
            Script.Rows(LookupRow(1, 0, "")), Script.NonQuery(), Script.NonQuery());
        var service = Service(connection);
        var intent = await service.SaveWithPhonesAsync(Equipment(), [Link(1, 8)], [Link(1, 9)]);

        var attempt = await service.SynchronizePhoneAttemptAsync(intent);

        Assert.All(attempt.Operations, item => Assert.Equal(PhoneSyncPhase.Succeeded, item.Phase));
        var targets = connection.ParameterLog.Where(item => HasParameter(item, "КодОборудования"))
            .Select(item => Parameter(item, "КодОборудования")).ToList();
        Assert.Contains(8, targets);
        Assert.Contains(9, targets);
        Assert.Equal(2, connection.CommandLog.Count(sql => sql.Contains("sp_CCM_PhoneSync", StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
    }

    /// <summary>Valid deletion использует подтверждённый старый CCM target и не требует caller before повторно.</summary>
    [Fact]
    public async Task DeletedCcm_AfterAtomicSave_SendsTrustedOldTarget()
    {
        var connection = Connection(
            Script.Scalar(1), Script.Rows(ModelTypeRow()), Script.NonQuery(),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(), Script.Rows(),
            Script.Rows(LookupRow(1, 0, "")), Script.NonQuery());
        var service = Service(connection);
        var intent = await service.SaveWithPhonesAsync(Equipment(), [Link(1, 8)], []);

        var attempt = await service.SynchronizePhoneAttemptAsync(intent);

        var operation = Assert.Single(attempt.Operations);
        Assert.Equal(PhoneSyncPhase.Succeeded, operation.Phase);
        Assert.Equal(PhoneSyncKind.Ccm, operation.Operation.Kind);
        Assert.Equal("8", operation.Operation.Key);
    }

    /// <summary>Metadata-only Save не создаёт ATS operations.</summary>
    [Fact]
    public async Task MetadataOnlySave_DoesNotReachAts()
    {
        var before = Link(1, 8);
        var after = Link(1, 8);
        after.Notes = "only metadata";
        var connection = Connection(
            Script.Scalar(1), Script.Rows(ModelTypeRow()), Script.NonQuery(),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(PhoneRow(1, 8)));
        var service = Service(connection);
        var intent = await service.SaveWithPhonesAsync(Equipment(), [before], [after]);

        var attempt = await service.SynchronizePhoneAttemptAsync(intent);

        Assert.Empty(attempt.Operations);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Xorcom priority не дополняется CCM operations.</summary>
    [Fact]
    public async Task XorcomPriority_DoesNotSendCcm()
    {
        var connection = Connection(
            Script.Scalar(1), Script.Rows(ModelTypeRow()), Script.NonQuery(),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8)),
            Script.Rows(LookupRow(1, 0, "")), Script.Rows(LookupRow(2, 1, "xorcom")),
            Script.Scalar(1), Script.NonQuery());
        var service = Service(connection);
        var intent = await service.SaveWithPhonesAsync(Equipment(), [], [Link(1, 8), Link(2, 8)]);

        var attempt = await service.SynchronizePhoneAttemptAsync(intent);

        Assert.Single(attempt.Operations);
        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("sp_CCM_PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Отзыв owner43 запрещает explicit Configure retry даже при наличии общей ANY43 семантики main.</summary>
    [Fact]
    public async Task ConfigureRetry_RevokedOwnerRole_ZeroAdditionalAts()
    {
        var version = Version();
        var connection = Connection(
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8)), Script.Rows(LookupRow(1, 1, "xorcom")),
            Script.Scalar(1), Script.Error(new InvalidOperationException("Процедура вернула код 2.")),
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(0));
        var service = Service(connection);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);
        Assert.Equal(PhoneSyncPhase.Failed, Assert.Single(initial.Operations).Phase);
        var restored = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(initial))!;

        var revoked = await Assert.ThrowsAsync<EquipmentOperationException>(() => service.RetryPhoneSyncAttemptAsync(restored));
        Assert.Equal(EquipmentWritePhase.Rejected, revoked.Phase);

        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Изменение фактического владельца или типа между Configure и retry запрещает dispatch.</summary>
    [Theory]
    [InlineData(1, 29)]
    [InlineData(2, 30)]
    public async Task ConfigureRetry_OwnerOrTypeChanged_ZeroAdditionalAts(int retryOwnerId, int retryTypeId)
    {
        var version = Version();
        var connection = Connection(
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8)), Script.Rows(LookupRow(1, 1, "xorcom")),
            Script.Scalar(1), Script.Error(new InvalidOperationException("Процедура вернула код 2.")),
            Script.Rows(EquipmentRow(ownerId: retryOwnerId, typeId: retryTypeId, changedAt: version)));
        var service = Service(connection);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);

        var changed = await Assert.ThrowsAsync<EquipmentOperationException>(() => service.RetryPhoneSyncAttemptAsync(initial));
        Assert.Equal(EquipmentWritePhase.Rejected, changed.Phase);

        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
    }

    /// <summary>Null-owner explicit Configure допускает только подтверждённую global43 и повторяет Failed.</summary>
    [Fact]
    public async Task ConfigureRetry_NullOwnerWithGlobalRole_RetriesFailedOnly()
    {
        var version = Version();
        var equipment = Script.Row(("КодОборудования", 7), ("КодЛицаВладельца", null),
            ("КодТипаОборудования", 30), ("Изменено", version));
        var connection = Connection(
            Script.Rows(equipment), Script.Scalar(1), Script.Rows(PhoneRow(1, 8)),
            Script.Rows(LookupRow(1, 1, "xorcom")), Script.Scalar(1),
            Script.Error(new InvalidOperationException("Процедура вернула код 2.")),
            Script.Rows(equipment), Script.Scalar(1), Script.Rows(PhoneRow(1, 8)), Script.Scalar(1), Script.NonQuery());
        var service = Service(connection);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);

        var retried = await service.RetryPhoneSyncAttemptAsync(initial);

        Assert.Equal(PhoneSyncPhase.Succeeded, Assert.Single(retried.Operations).Phase);
        Assert.Equal(2, connection.CommandLog.Count(sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mixed ledger переживает JSON и повторяет только Failed, сохраняя Succeeded и Unknown.</summary>
    [Fact]
    public async Task MixedAttempt_RetryFailedOnly_PreservesUnknownAndDoesNotUpdateMain()
    {
        var version = Version();
        var connection = Connection(
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8), PhoneRow(3, 8)),
            Script.Rows(LookupRow(1, 1, "xorcom")), Script.Rows(LookupRow(2, 1, "xorcom")),
            Script.Rows(LookupRow(3, 1, "xorcom")),
            Script.Scalar(1), Script.NonQuery(),
            Script.Scalar(1), Script.Error(new InvalidOperationException("Процедура вернула код 2.")),
            Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(-2, "timeout")),
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8), PhoneRow(3, 8)), Script.Scalar(1), Script.NonQuery(),
            Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: version)), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8), PhoneRow(3, 8)));
        var service = Service(connection);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);
        Assert.Equal(new[] { PhoneSyncPhase.Succeeded, PhoneSyncPhase.Failed, PhoneSyncPhase.Unknown },
            initial.Operations.Select(item => item.Phase).ToArray());
        var restored = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(initial))!;

        var retried = await service.RetryPhoneSyncAttemptAsync(restored);

        Assert.Equal(new[] { PhoneSyncPhase.Succeeded, PhoneSyncPhase.Succeeded, PhoneSyncPhase.Unknown },
            retried.Operations.Select(item => item.Phase).ToArray());
        Assert.Equal(4, connection.CommandLog.Count(sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal)));
        Assert.Equal(2, connection.ParameterLog.Last()["КодТелефонногоНомера"]);
        var restoredAfterRetry = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(retried))!;
        var reconciliation = await service.ReconcilePhoneSyncAttemptAsync(restoredAfterRetry);
        Assert.True(reconciliation.PersistedStateMatches);
        Assert.Equal(PhoneSyncPhase.Unknown, reconciliation.Attempt.Operations.Last().Phase);
        Assert.Equal(4, connection.CommandLog.Count(sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    private static EquipmentFormService Service(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }

    private static ScriptedConnection Connection(params ScriptEntry[] entries) => new(new Queue<ScriptEntry>(entries));

    private static DateTime Version() => new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(500);

    private static Equipment Equipment(DateTime? changedAt = null) => new()
    {
        Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 30, ChangedAtUtc = changedAt ?? Version(),
    };

    private static EquipmentPhoneLink Link(int phoneId, int? connectionId) => new()
    {
        EquipmentId = 7, PhoneId = phoneId, ConnectionEquipmentId = connectionId,
    };

    private static PhoneSyncOperation Xorcom(int phoneId) => new(PhoneSyncKind.Xorcom, phoneId.ToString());

    private static Dictionary<string, object?> ModelTypeRow() => Script.Row(
        ("КодТипаОборудования", 30), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0));

    private static Dictionary<string, object?> EquipmentRow(int ownerId = 1, int typeId = 30, DateTime? changedAt = null) =>
        Script.Row(("КодОборудования", 7), ("КодЛицаВладельца", ownerId),
            ("КодТипаОборудования", typeId), ("Изменено", changedAt ?? Version()));

    private static Dictionary<string, object?> PhoneRow(int phoneId, int connectionId) => Script.Row(
        ("КодТелефонногоНомера", phoneId), ("КодОборудования", 7),
        ("КодОборудованияПодключения", connectionId), ("Основной", false),
        ("НомерАвтонабора", null), ("ЗадержкаАвтонабора", null));

    private static Dictionary<string, object?> LookupRow(int phoneId, int stationId, string xorcomName) => Script.Row(
        ("КодТелефонногоНомера", phoneId), ("КодТелефоннойСтанции", stationId), ("XorcomName", xorcomName));

    private static bool HasParameter(IReadOnlyDictionary<string, object?> parameters, string name) =>
        parameters.Keys.Any(key => key.TrimStart('@') == name);

    private static object? Parameter(IReadOnlyDictionary<string, object?> parameters, string name) =>
        parameters.Single(item => item.Key.TrimStart('@') == name).Value;
}
