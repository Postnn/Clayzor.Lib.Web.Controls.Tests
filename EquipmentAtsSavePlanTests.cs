using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет отложенную подготовку АТС по сохранённым телефонным связям.</summary>
public sealed class EquipmentAtsSavePlanTests
{
    private const string Principal = @"DOMAIN\fixture";
    private const int EmployeeId = 4;
    private const int EquipmentId = 17;
    private const int OwnerId = 12;
    private static readonly DateTime ChangedAt = new(2026, 11, 27, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>Подтверждённое снятие связи даёт числовую цель Xorcom. CCM проигрывает при наличии Xorcom.</summary>
    [Fact]
    public async Task DeletedXorcomUsesNumericTargetAndPreparedPlanSurvivesAnotherCircuit()
    {
        const int deletedPhone = 10;
        const int currentPhone = 20;
        const int connectionEquipment = 88;
        var id = Guid.NewGuid();
        var header = Header(id, LinksFingerprint(currentPhone, connectionEquipment));
        var (script, sql, db) = Create([
            .. ReadHeader(header, prepared: false),
            Script.Rows(Link(currentPhone, connectionEquipment)),
            Candidates(new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, deletedPhone, true),
                new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, currentPhone, false),
                new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Ccm, connectionEquipment, false)),
            Script.Rows(),
            Script.Rows(Station(deletedPhone, 2, "pbx-ten")),
            Script.Rows(Station(currentPhone, 0, "")),
            Script.Scalar(1), Script.Scalar(1),
        ]);
        using var lifetime = db;

        var plan = await EquipmentAtsWorkflow.PrepareSavedAsync(db, Principal, EmployeeId, EquipmentId, id, default);

        Assert.True(plan.Prepared);
        Assert.Equal(new EquipmentAtsStep(0, EquipmentAtsStep.Xorcom, deletedPhone, true, EquipmentAtsStep.Planned),
            Assert.Single(plan.Steps));
        var checkedLinks = Assert.Single(ParametersFor(script, SQLQueries.SELECT_ТелефонныйНомерАктуальныеПривязки));
        Assert.Equal(deletedPhone, checkedLinks["PhoneId"]);
        var step = Assert.Single(ParametersFor(script, SQLQueries.INSERT_EquipmentAtsStep));
        Assert.Equal(deletedPhone, step["TargetId"]);
        Assert.True(Assert.IsType<bool>(step["Deleting"]));
        var update = Assert.Single(ParametersFor(script, SQLQueries.UPDATE_EquipmentAtsSavePlanPrepared));
        Assert.Equal(plan.Header.ContextFingerprint, update["ContextFingerprint"]);
        Assert.NotEqual(header.ContextFingerprint, plan.Header.ContextFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", plan.Header.ContextFingerprint);
        var stepIndex = script.CommandLog.IndexOf(SQLQueries.INSERT_EquipmentAtsStep);
        var preparedIndex = script.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsSavePlanPrepared);
        Assert.True(stepIndex >= 0 && stepIndex < preparedIndex);
        Assert.NotNull(sql.Commands[stepIndex].Transaction);
        Assert.Same(sql.Commands[stepIndex].Transaction, sql.Commands[preparedIndex].Transaction);
        Assert.Equal(new[] { "begin", "commit" }, sql.TransactionLog);

        var (reopened, reopenedSql, reopenedDb) = Create([
            .. ReadHeader(plan.Header, prepared: true), Steps(plan.Steps.ToArray()),
        ]);
        using var reopenedLifetime = reopenedDb;
        var stored = await EquipmentAtsWorkflow.PrepareSavedAsync(reopenedDb, Principal, EmployeeId, EquipmentId, id, default);

        Assert.True(stored.Prepared);
        Assert.Equal(plan.Header, stored.Header);
        Assert.Equal(plan.Steps.ToArray(), stored.Steps.ToArray());
        Assert.DoesNotContain(SQLQueries.SELECT_EquipmentAtsSaveCandidates, reopened.CommandLog);
        AssertNoWrites(reopened);
        Assert.Equal(new[] { "begin", "commit" }, reopenedSql.TransactionLog);

        var planned = Assert.Single(plan.Steps);
        var (dispatch, dispatchSql, dispatchDb) = Create([
            Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
            .. ReadHeader(plan.Header, prepared: true), Steps(planned),
            Script.Scalar(1), .. ReadHeader(plan.Header, prepared: true), Steps(planned),
            Candidates(new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, deletedPhone, true),
                new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, currentPhone, false),
                new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Ccm, connectionEquipment, false)),
            Script.Rows(Link(currentPhone, connectionEquipment)), Script.Rows(),
            Script.Rows(Station(deletedPhone, 2, "pbx-ten")), Script.Rows(Station(currentPhone, 0, "")),
            Script.Scalar(1), new ScriptEntry
            {
                SetNonQueryOutputs = parameters => parameters.Cast<DbParameter>()
                    .Single(parameter => parameter.Direction == ParameterDirection.ReturnValue).Value = 0,
            },
            .. ReadHeader(plan.Header, prepared: true), Steps(planned with { State = EquipmentAtsStep.Dispatching }),
            Script.Scalar(1), Script.Scalar(1),
        ]);
        using var dispatchLifetime = dispatchDb;
        var service = new EquipmentFormService(dispatchDb, new TestAuthenticationStateProvider());
        var ticket = new EquipmentPhoneSyncIntent(id.ToString("N"), EquipmentId, EquipmentPhoneSyncOrigin.EquipmentSave,
            default, null, null, [], []) { DurableSave = true };

        var completed = await service.SynchronizePhoneAttemptAsync(ticket);

        Assert.Equal(PhoneSyncPhase.Succeeded, Assert.Single(completed.Operations).Phase);
        var provider = Assert.Single(ParametersFor(dispatch, SQLQueries.SP_PhoneSync_Xorcom));
        Assert.Equal(deletedPhone, provider["КодТелефонногоНомера"]);
        Assert.Equal(true, provider["Удаление"]);
        var sent = dispatch.CommandLog.IndexOf(SQLQueries.SP_PhoneSync_Xorcom);
        var claimed = dispatch.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsStepState);
        Assert.True(claimed >= 0 && claimed < sent);
        Assert.NotNull(dispatchSql.Commands[claimed].Transaction);
        Assert.Null(dispatchSql.Commands[sent].Transaction);
        Assert.DoesNotContain(dispatch.CommandLog, query => query.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
    }

    /// <summary>Недоступная станция не оставляет ни шагов, ни признака готовности.</summary>
    [Fact]
    public async Task MissingStationLeavesSavedPlanUnprepared()
    {
        const int phone = 20;
        const int connectionEquipment = 88;
        var id = Guid.NewGuid();
        var header = Header(id, LinksFingerprint(phone, connectionEquipment));
        var (script, sql, db) = Create([
            .. ReadHeader(header, prepared: false),
            Script.Rows(Link(phone, connectionEquipment)),
            Candidates(new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, phone, false),
                new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Ccm, connectionEquipment, false)),
            Script.Rows(),
        ]);
        using var lifetime = db;

        await Assert.ThrowsAsync<EquipmentAtsPlanningUnavailableException>(() => EquipmentAtsWorkflow.PrepareSavedAsync(
            db, Principal, EmployeeId, EquipmentId, id, default));

        Assert.Equal(phone, Assert.Single(ParametersFor(script, SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору))["Id"]);
        AssertNoWrites(script);
        Assert.DoesNotContain("commit", sql.TransactionLog);
    }

    /// <summary>Удалённая связь не разрешает очистку Xorcom, если номер уже снова подключён.</summary>
    [Fact]
    public async Task ReattachedPhoneRejectsDeletedCandidateBeforeStationLookup()
    {
        const int phone = 10;
        var id = Guid.NewGuid();
        var header = Header(id, LinksFingerprint());
        var (script, sql, db) = Create([
            .. ReadHeader(header, prepared: false),
            Script.Rows(),
            Candidates(new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, phone, true)),
            Script.Rows(Script.Row(("КодОборудования", 31))),
        ]);
        using var lifetime = db;

        await Assert.ThrowsAsync<EquipmentAtsContextChangedException>(() => EquipmentAtsWorkflow.PrepareSavedAsync(
            db, Principal, EmployeeId, EquipmentId, id, default));

        Assert.Equal(phone, Assert.Single(ParametersFor(script, SQLQueries.SELECT_ТелефонныйНомерАктуальныеПривязки))["PhoneId"]);
        Assert.DoesNotContain(SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору, script.CommandLog);
        AssertNoWrites(script);
        Assert.DoesNotContain("commit", sql.TransactionLog);
    }

    /// <summary>Недостоверный номер в сохранённом наборе не добавляется в готовый план.</summary>
    [Fact]
    public async Task NonDeletedCandidateMustBelongToSavedRoot()
    {
        const int linkedPhone = 20;
        const int connectionEquipment = 88;
        var id = Guid.NewGuid();
        var (script, sql, db) = Create([
            .. ReadHeader(Header(id, LinksFingerprint(linkedPhone, connectionEquipment)), prepared: false),
            Script.Rows(Link(linkedPhone, connectionEquipment)),
            Candidates(new EquipmentAtsSaveCandidate(EquipmentAtsSaveCandidate.Phone, 93, false)),
        ]);
        using var lifetime = db;

        await Assert.ThrowsAsync<EquipmentAtsContextChangedException>(() => EquipmentAtsWorkflow.PrepareSavedAsync(
            db, Principal, EmployeeId, EquipmentId, id, default));

        Assert.DoesNotContain(SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору, script.CommandLog);
        AssertNoWrites(script);
        Assert.DoesNotContain("commit", sql.TransactionLog);
    }

    private static EquipmentAtsHeader Header(Guid id, string fingerprint) =>
        new(id, EmployeeId, EquipmentId, (byte)EquipmentPhoneSyncOrigin.EquipmentSave, ChangedAt, OwnerId, fingerprint, 0);

    private static ScriptEntry[] ReadHeader(EquipmentAtsHeader header, bool prepared) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        Script.Rows(Script.Row(("КодОборудования", EquipmentId), ("КодЛицаВладельца", OwnerId),
            ("КодТипаОборудования", (int)ТипыОборудования.IP_телефон), ("Изменено", ChangedAt))),
        TypedRows([("AttemptId", typeof(Guid)), ("EmployeeId", typeof(int)), ("EquipmentId", typeof(int)), ("Origin", typeof(byte)),
            ("ExpectedChangedAtUtc", typeof(DateTime)), ("OwnerId", typeof(int)), ("ContextFingerprint", typeof(string)), ("State", typeof(byte))],
            new object?[] { header.AttemptId, header.EmployeeId, header.EquipmentId, header.Origin, header.ExpectedChangedAtUtc,
                header.OwnerId, header.ContextFingerprint, header.State }),
        Script.Rows(Script.Row(("PlanPrepared", prepared))),
    ];

    private static ScriptEntry Candidates(params EquipmentAtsSaveCandidate[] candidates) =>
        TypedRows([("Kind", typeof(byte)), ("TargetId", typeof(int)), ("Deleting", typeof(bool))],
            candidates.Select(candidate => new object?[] { candidate.Kind, candidate.TargetId, candidate.Deleting }).ToArray());

    private static ScriptEntry Steps(params EquipmentAtsStep[] steps) =>
        TypedRows([("StepIndex", typeof(int)), ("Kind", typeof(byte)), ("TargetId", typeof(int)),
                ("Deleting", typeof(bool)), ("State", typeof(byte))],
            steps.Select(step => new object?[] { step.StepIndex, step.Kind, step.TargetId, step.Deleting, step.State }).ToArray());

    private static ScriptEntry TypedRows((string Name, Type Type)[] columns, params object?[][] values) => new()
    {
        ReaderFactory = () =>
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            foreach (var row in values) table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
            return table.CreateDataReader();
        },
    };

    private static Dictionary<string, object?> Link(int phone, int connection) =>
        Script.Row(("КодТелефонногоНомера", phone), ("КодОборудования", EquipmentId),
            ("КодОборудованияПодключения", connection));

    private static Dictionary<string, object?> Station(int phone, int station, string xorcom) =>
        Script.Row(("КодТелефонногоНомера", phone), ("КодТелефоннойСтанции", station), ("XorcomName", xorcom));

    private static string LinksFingerprint(int? phone = null, int? connection = null)
    {
        EquipmentPhoneLink[] links = phone is int id ? [new() { PhoneId = id, ConnectionEquipmentId = connection }] : [];
        var data = links.Select(link => new
        {
            link.PhoneId, link.ConnectionEquipmentId, link.AutoDialNumber, link.AutoDialDelay, link.IsDefault,
        });
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    }

    private static IEnumerable<Dictionary<string, object?>> ParametersFor(ScriptedConnection script, string sql) =>
        script.CommandLog.Zip(script.ParameterLog).Where(entry => entry.First == sql).Select(entry => entry.Second);

    private static void AssertNoWrites(ScriptedConnection script) =>
        Assert.DoesNotContain(script.CommandLog, sql => sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));

    private static (ScriptedConnection Script, EquipmentAccountTransactionConnection Sql, DbManager Db) Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var sql = new EquipmentAccountTransactionConnection(script);
        return (script, sql, new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => sql));
    }
}
