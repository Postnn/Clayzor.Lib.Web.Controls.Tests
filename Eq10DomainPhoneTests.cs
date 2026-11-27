using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки trusted phone diff, Configure scope и per-operation retry EQ10.</summary>
public sealed partial class Eq10DomainPhoneTests
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

    /// <summary>При ошибке определения маршрута повтор сначала читает станции, сохраняя приоритет Xorcom.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanningReadFailure_RetriesFromTrustedDiffWithoutFabricatedOperation(bool secondIsXorcom)
    {
        var entries = new List<ScriptEntry>
        {
            Script.Scalar(1), Script.Rows(ModelTypeRow()), Script.NonQuery(),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8)),
            Script.Rows(LookupRow(1, 0, "")), Script.Error(SqlExceptionFactory.Create(-2, "fixture lookup failure")),
            Script.Rows(EquipmentRow()), Script.Scalar(1), Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8)),
            Script.Rows(LookupRow(1, 0, "")), Script.Rows(LookupRow(2, secondIsXorcom ? 1 : 0, secondIsXorcom ? "xorcom" : "")),
        };
        if (secondIsXorcom)
            entries.Add(Script.Scalar(1));
        entries.Add(Script.NonQuery());

        using var connection = Connection(entries.ToArray());
        var service = Service(connection);
        var intent = await service.SaveWithPhonesAsync(Equipment(), [], [Link(1, 8), Link(2, 8)]);
        var initial = await service.SynchronizePhoneAttemptAsync(intent);

        Assert.Empty(initial.Operations);
        Assert.Contains("Команды АТС не отправлены", initial.PlanningError);
        Assert.DoesNotContain(connection.CommandLog, IsAtsDispatch);
        var restored = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(initial))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryPhoneSyncAttemptAsync(restored with { PlanningError = null }));
        var retry = await service.RetryPhoneSyncAttemptAsync(restored);

        Assert.Equal(intent.AttemptId, retry.AttemptId);
        Assert.Null(retry.PlanningError);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryPhoneSyncAttemptAsync(restored));
        var operation = Assert.Single(retry.Operations);
        Assert.Equal(PhoneSyncPhase.Succeeded, operation.Phase);
        Assert.Equal(secondIsXorcom ? PhoneSyncKind.Xorcom : PhoneSyncKind.Ccm, operation.Operation.Kind);
        Assert.Equal(secondIsXorcom ? "2" : "8", operation.Operation.Key);
        Assert.Single(connection.CommandLog, IsAtsDispatch);
        var equipmentWrite = connection.CommandLog[2];
        Assert.Contains("ОжидаемоеКоличествоСвязей", equipmentWrite);
        Assert.Single(connection.CommandLog, sql => sql == equipmentWrite);
    }

    private static bool IsAtsDispatch(string sql) =>
        sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal) ||
        sql.Contains("sp_CCM_PhoneSync", StringComparison.Ordinal);

    /// <summary>Отзыв owner43 запрещает explicit Configure retry даже при наличии общей ANY43 семантики main.</summary>
    [Fact]
    public async Task ConfigureRetry_RevokedOwnerRole_ZeroAdditionalAts()
    {
        var id = Guid.NewGuid();
        var (service, connection) = ConfigureService([
            ConfigureActor(), .. ConfigureRead(id, 1, 1, EquipmentAtsStep.Planned),
            Script.Scalar(1), .. ConfigureRead(id, 1, 1, EquipmentAtsStep.Planned), .. ConfigurePhoneContext(1),
            Script.Scalar(1), new ScriptEntry
            {
                SetNonQueryOutputs = parameters => parameters.Cast<System.Data.Common.DbParameter>()
                    .Single(parameter => parameter.Direction == System.Data.ParameterDirection.ReturnValue).Value = 2,
            },
            ConfigureActor(), .. ConfigureAccess(ownerId: 1, personId: 2)]);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);
        Assert.Equal(PhoneSyncPhase.Unknown, Assert.Single(initial.Operations).Phase);
        var restored = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(initial))!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryPhoneSyncAttemptAsync(restored));

        Assert.Single(connection.CommandLog, IsAtsDispatch);
        Assert.Single(connection.CommandLog, sql => sql == Clayzor.Lib.Entities.SQLQueries.UPDATE_EquipmentAtsStepState);
        Assert.DoesNotContain(Clayzor.Lib.Entities.SQLQueries.UPDATE_EquipmentAtsCompleted, connection.CommandLog);
    }

    /// <summary>Изменение фактического владельца или типа между Configure и retry запрещает dispatch.</summary>
    [Theory]
    [InlineData(1, 29)]
    [InlineData(2, 30)]
    public async Task ConfigureRetry_OwnerOrTypeChanged_ZeroAdditionalAts(int retryOwnerId, int retryTypeId)
    {
        var id = Guid.NewGuid();
        var (service, connection) = ConfigureService([
            ConfigureActor(), .. ConfigureRead(id, 1, 1, EquipmentAtsStep.Planned),
            Script.Error(SqlExceptionFactory.Create(-2, "Тайм-аут проверки процедуры")),
            ConfigureActor(), .. ConfigureAccess(retryOwnerId, personId: 1, typeId: retryTypeId)]);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);
        Assert.Equal(PhoneSyncPhase.NotSent, Assert.Single(initial.Operations).Phase);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryPhoneSyncAttemptAsync(initial));

        Assert.DoesNotContain(connection.CommandLog, IsAtsDispatch);
        Assert.DoesNotContain(Clayzor.Lib.Entities.SQLQueries.UPDATE_EquipmentAtsStepState, connection.CommandLog);
    }

    /// <summary>Карточка без владельца допускает только глобальную роль 43 и продолжает сохранённый Planned.</summary>
    [Fact]
    public async Task ConfigureRetry_NullOwnerRequiresGlobalRoleToRunPersistedPlannedStep()
    {
        var (restricted, deniedConnection) = ConfigureService([ConfigureActor(), .. ConfigureAccess(null, personId: 1)]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.ConfigurePhoneEquipmentAttemptAsync(7));
        Assert.DoesNotContain(deniedConnection.CommandLog, IsAtsDispatch);

        var id = Guid.NewGuid();
        var (service, connection) = ConfigureService([
            .. ConfigureRead(id, null, 0, EquipmentAtsStep.Planned),
            ConfigureActor(), .. ConfigureRead(id, null, 0, EquipmentAtsStep.Planned),
            Script.Scalar(1), .. ConfigureRead(id, null, 0, EquipmentAtsStep.Planned), .. ConfigurePhoneContext(1),
            Script.Scalar(1), Script.NonQuery(),
            .. ConfigureRead(id, null, 0, EquipmentAtsStep.Dispatching), Script.Scalar(1), Script.Scalar(1)]);
        var initial = await service.ReadConfigurePhoneAttemptAsync(7, 4, id, default);
        Assert.NotNull(initial);
        Assert.Equal(PhoneSyncPhase.NotSent, Assert.Single(initial.Operations).Phase);

        var retried = await service.RetryPhoneSyncAttemptAsync(initial);

        Assert.Equal(id.ToString("N"), retried.AttemptId);
        Assert.Equal(PhoneSyncPhase.Succeeded, Assert.Single(retried.Operations).Phase);
        Assert.Single(connection.CommandLog, IsAtsDispatch);
        Assert.Equal(2, connection.CommandLog.Count(sql => sql == Clayzor.Lib.Entities.SQLQueries.UPDATE_EquipmentAtsStepState));
        Assert.Single(connection.CommandLog, sql => sql == Clayzor.Lib.Entities.SQLQueries.UPDATE_EquipmentAtsCompleted);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
    }

    /// <summary>Смешанный план сохраняет подтверждённый, неопределённый и неотправленный шаги без повторной отправки.</summary>
    [Fact]
    public async Task MixedAttempt_DispatchingBlocksRetryAndReconciliationOnlyReads()
    {
        var id = Guid.NewGuid();
        byte[] states = [EquipmentAtsStep.Confirmed, EquipmentAtsStep.Dispatching, EquipmentAtsStep.Planned];
        PhoneSyncPhase[] phases = [PhoneSyncPhase.Succeeded, PhoneSyncPhase.Unknown, PhoneSyncPhase.NotSent];
        var (service, connection) = ConfigureService([
            ConfigureActor(), .. ConfigureRead(id, 1, 1, states),
            ConfigureActor(), .. ConfigureRead(id, 1, 1, states),
            ConfigureActor(), .. ConfigureRead(id, 1, 1, states),
            Script.Rows(EquipmentRow()), ConfigurePhoneLinks(3),
            .. ConfigureRead(id, 1, 1, states), .. ConfigurePhoneContext(3)]);
        var initial = await service.ConfigurePhoneEquipmentAttemptAsync(7);
        Assert.Equal(phases, initial.Operations.Select(item => item.Phase));
        var restored = JsonSerializer.Deserialize<PhoneSyncAttempt>(JsonSerializer.Serialize(initial))!;

        var retried = await service.RetryPhoneSyncAttemptAsync(restored);
        Assert.Equal(phases, retried.Operations.Select(item => item.Phase));

        var reconciliation = await service.ReconcilePhoneSyncAttemptAsync(retried);
        Assert.True(reconciliation.PersistedStateMatches);
        Assert.Equal(phases, reconciliation.Attempt.Operations.Select(item => item.Phase));
        Assert.DoesNotContain(connection.CommandLog, IsAtsDispatch);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase));
    }

    private static ScriptEntry ConfigureActor() => Script.Rows(Script.Row(("КодСотрудника", 4)));

    private static ScriptEntry[] ConfigureAccess(int? ownerId, int personId, int typeId = 30) =>
    [
        ConfigureActor(),
        Script.Rows(Script.Row(("RoleId", (int)Clayzor.Lib.Entities.Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", personId))),
        Script.Rows(Script.Row(("КодОборудования", 7), ("КодЛицаВладельца", ownerId),
            ("КодТипаОборудования", typeId), ("Изменено", Version()))),
    ];

    private static ScriptEntry[] ConfigureRead(Guid id, int? ownerId, int personId, params byte[] states) =>
    [
        .. ConfigureAccess(ownerId, personId),
        ConfigureRows([("AttemptId", typeof(Guid)), ("EmployeeId", typeof(int)), ("EquipmentId", typeof(int)),
            ("Origin", typeof(byte)), ("ExpectedChangedAtUtc", typeof(DateTime)), ("OwnerId", typeof(int)),
            ("ContextFingerprint", typeof(string)), ("State", typeof(byte))],
            new object?[] { id, 4, 7, (byte)EquipmentPhoneSyncOrigin.ExplicitConfigure, Version(), ownerId, ConfigureFingerprint(states.Length), (byte)0 }),
        ConfigureRows([("StepIndex", typeof(int)), ("Kind", typeof(byte)), ("TargetId", typeof(int)),
            ("Deleting", typeof(bool)), ("State", typeof(byte))],
            states.Select((state, i) => new object?[] { i, EquipmentAtsStep.Xorcom, i + 1, false, state }).ToArray()),
    ];

    private static string ConfigureFingerprint(int count) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, count).Select(phoneId => new
        {
            PhoneId = phoneId, ConnectionEquipmentId = 8, AutoDialNumber = "", AutoDialDelay = (byte?)null,
            IsDefault = false, StationId = 1, XorcomName = "xorcom",
        }))));

    private static ScriptEntry ConfigurePhoneLinks(int count) =>
        Script.Rows(Enumerable.Range(1, count).Select(id => Script.Row(
            ("КодТелефонногоНомера", id), ("КодОборудования", 7), ("КодОборудованияПодключения", 8))).ToArray());

    private static ScriptEntry[] ConfigurePhoneContext(int count) =>
    [
        ConfigurePhoneLinks(count),
        .. Enumerable.Range(1, count).Select(id => Script.Rows(LookupRow(id, 1, "xorcom"))),
    ];

    private static ScriptEntry ConfigureRows((string Name, Type Type)[] columns, params object?[][] values) => new()
    {
        ReaderFactory = () =>
        {
            var table = new System.Data.DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            foreach (var row in values) table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
            return table.CreateDataReader();
        },
    };

    private static (EquipmentFormService Service, ScriptedConnection Script) ConfigureService(params ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        return (new EquipmentFormService(new DbManager("Server=fixture", new FakeSqlErrorHandler(),
            () => new EquipmentAccountTransactionConnection(script)), new TestAuthenticationStateProvider()), script);
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
