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

/// <summary>Проверяет границу между серверным шагом настройки оборудования и процедурой АТС.</summary>
public sealed class EquipmentAtsDispatchTests
{
    private const string Principal = @"DOMAIN\fixture";
    private const int EmployeeId = 4;
    private const int EquipmentId = 17;
    private const int OwnerId = 12;
    private const int PhoneId = 3;
    private const int ConnectionEquipmentId = 77;
    private static readonly DateTime ChangedAt = new(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>Ошибка или нулевой CAS не дают разрешения на вызов процедуры.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDurableBeginProofDoesNotCallProvider(bool lostReply)
    {
        var header = Workflow(xorcom: false);
        var step = PlanStep(xorcom: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (script, connection, db) = Create([
            .. Begin(header, step),
            new ScriptEntry
            {
                Scalar = 0, Started = started, Release = release,
                Exception = lostReply ? SqlExceptionFactory.Create(-2, "Тайм-аут CAS") : null,
            },
        ]);
        using var lifetime = db;

        var sending = new PhoneFormService(db).SynchronizeEquipmentWorkflowStepAsync(Principal, header, step, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AssertNoProvider(script);
            Assert.DoesNotContain("commit", connection.TransactionLog);
        }
        finally { release.TrySetResult(); }
        var outcome = await sending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(PhoneSyncPhase.Unknown, outcome.Phase);
        AssertTransition(script, header.AttemptId, EquipmentAtsStep.Planned, EquipmentAtsStep.Dispatching);
        AssertNoProvider(script);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Тайм-аут и ненулевой ответ оставляют отправленный шаг неопределённым для нового circuit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureAfterDurableBeginIsUnknownAndCannotReplay(bool timesOut)
    {
        var header = Workflow(xorcom: true);
        var step = PlanStep(xorcom: true);
        ScriptedConnection? firstScript = null;
        EquipmentAccountTransactionConnection? firstConnection = null;
        var procedureInvoked = false;
        var provider = new ScriptEntry
        {
            SetNonQueryOutputs = parameters =>
            {
                procedureInvoked = true;
                Assert.Equal(new[] { "begin", "commit" }, firstConnection!.TransactionLog);
                var scope = Assert.Single(ParametersFor(firstScript!, SQLQueries.SP_PhoneSync_Xorcom));
                Assert.Equal(PhoneId, scope["КодТелефонногоНомера"]);
                if (timesOut) throw SqlExceptionFactory.Create(-2, "Тайм-аут процедуры АТС");
                parameters.Cast<DbParameter>().Single(parameter => parameter.Direction == ParameterDirection.ReturnValue).Value = 7;
            },
        };
        var (script, connection, db) = Create([.. Begin(header, step), Script.Scalar(1), provider]);
        firstScript = script;
        firstConnection = connection;
        using var lifetime = db;

        var first = await new PhoneFormService(db).SynchronizeEquipmentWorkflowStepAsync(Principal, header, step, default);

        Assert.True(procedureInvoked);
        Assert.Equal(PhoneSyncPhase.Unknown, first.Phase);
        Assert.Equal(header.AttemptId.ToString("N"), first.AttemptId);
        var committedState = AssertTransition(script, header.AttemptId, EquipmentAtsStep.Planned, EquipmentAtsStep.Dispatching);
        Assert.True(script.CommandLog.IndexOf(SQLQueries.SELECT_ПроцедураИмеетПараметр) <
            script.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsStepState));
        Assert.True(script.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsStepState) <
            script.CommandLog.IndexOf(SQLQueries.SP_PhoneSync_Xorcom));
        Assert.Single(script.CommandLog, sql => sql == SQLQueries.SP_PhoneSync_Xorcom);
        Assert.DoesNotContain(SQLQueries.UPDATE_EquipmentAtsCompleted, script.CommandLog);

        // Новый circuit получает фазу, которую сохранил первый circuit.
        var (reopenedScript, reopenedConnection, reopenedDb) = Create(
            ReadStep(header, step with { State = committedState }));
        using var reopenedLifetime = reopenedDb;
        var second = await new PhoneFormService(reopenedDb).SynchronizeEquipmentWorkflowStepAsync(Principal, header, step, default);

        Assert.Equal(PhoneSyncPhase.Unknown, second.Phase);
        AssertNoProvider(reopenedScript);
        Assert.DoesNotContain(SQLQueries.UPDATE_EquipmentAtsStepState, reopenedScript.CommandLog);
        Assert.DoesNotContain("commit", reopenedConnection.TransactionLog);
    }

    /// <summary>Подтверждённый шаг возвращает успех без новой записи фазы и без обращения к АТС.</summary>
    [Fact]
    public async Task ConfirmedStepReturnsSuccessWithoutCallingProvider()
    {
        var header = Workflow(xorcom: false) with { State = 1 };
        var step = PlanStep(xorcom: false);
        var (script, connection, db) = Create(ReadStep(header, step with { State = EquipmentAtsStep.Confirmed }));
        using var lifetime = db;

        var result = await new PhoneFormService(db).SynchronizeEquipmentWorkflowStepAsync(Principal, header, step, default);

        Assert.Equal(PhoneSyncPhase.Succeeded, result.Phase);
        Assert.Equal(header.AttemptId.ToString("N"), result.AttemptId);
        AssertNoProvider(script);
        Assert.DoesNotContain(SQLQueries.UPDATE_EquipmentAtsStepState, script.CommandLog);
        Assert.DoesNotContain(SQLQueries.UPDATE_EquipmentAtsCompleted, script.CommandLog);
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    private static EquipmentAtsHeader Workflow(bool xorcom)
    {
        var context = new[]
        {
            new { PhoneId, ConnectionEquipmentId, AutoDialNumber = "", AutoDialDelay = (byte?)null,
                IsDefault = false, StationId = xorcom ? 2 : 0, XorcomName = xorcom ? "pbx-three" : "" },
        };
        return new(Guid.NewGuid(), EmployeeId, EquipmentId, (byte)EquipmentPhoneSyncOrigin.ExplicitConfigure,
            ChangedAt, OwnerId, Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context))), 0);
    }

    private static EquipmentAtsStep PlanStep(bool xorcom) =>
        new(0, xorcom ? EquipmentAtsStep.Xorcom : EquipmentAtsStep.Ccm, xorcom ? PhoneId : ConnectionEquipmentId,
            false, EquipmentAtsStep.Planned);

    private static ScriptEntry[] Begin(EquipmentAtsHeader header, EquipmentAtsStep step) =>
    [
        .. ReadStep(header, step),
        Script.Rows(Script.Row(("КодТелефонногоНомера", PhoneId), ("КодОборудования", EquipmentId),
            ("КодОборудованияПодключения", ConnectionEquipmentId))),
        Script.Rows(Script.Row(("КодТелефонногоНомера", PhoneId), ("КодТелефоннойСтанции", step.Kind == EquipmentAtsStep.Xorcom ? 2 : 0),
            ("XorcomName", step.Kind == EquipmentAtsStep.Xorcom ? "pbx-three" : ""))),
    ];

    private static ScriptEntry[] ReadStep(EquipmentAtsHeader header, EquipmentAtsStep step) =>
    [
        .. (step.Kind == EquipmentAtsStep.Xorcom ? new[] { Script.Scalar(1) } : Array.Empty<ScriptEntry>()),
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", OwnerId))),
        Script.Rows(Script.Row(("КодОборудования", EquipmentId), ("КодЛицаВладельца", OwnerId),
            ("КодТипаОборудования", (int)ТипыОборудования.IP_телефон), ("Изменено", ChangedAt))),
        TypedRow(("AttemptId", header.AttemptId), ("EmployeeId", header.EmployeeId), ("EquipmentId", header.EquipmentId),
            ("Origin", header.Origin), ("ExpectedChangedAtUtc", header.ExpectedChangedAtUtc), ("OwnerId", header.OwnerId!.Value),
            ("ContextFingerprint", header.ContextFingerprint), ("State", header.State)),
        TypedRow(("StepIndex", step.StepIndex), ("Kind", step.Kind), ("TargetId", step.TargetId),
            ("Deleting", step.Deleting), ("State", step.State)),
    ];

    private static ScriptEntry TypedRow(params (string Name, object Value)[] values) => new()
    {
        ReaderFactory = () =>
        {
            var table = new DataTable();
            foreach (var (name, value) in values) table.Columns.Add(name, value.GetType());
            table.Rows.Add(values.Select(item => item.Value).ToArray());
            return table.CreateDataReader();
        },
    };

    private static byte AssertTransition(ScriptedConnection script, Guid id, byte expected, byte next)
    {
        var transition = Assert.Single(ParametersFor(script, SQLQueries.UPDATE_EquipmentAtsStepState));
        Assert.Equal(id, transition["AttemptId"]);
        Assert.Equal(expected, transition["ExpectedState"]);
        var actual = Assert.IsType<byte>(transition["NextState"]);
        Assert.Equal(next, actual);
        return actual;
    }

    private static IEnumerable<Dictionary<string, object?>> ParametersFor(ScriptedConnection script, string sql) =>
        script.CommandLog.Zip(script.ParameterLog).Where(item => item.First == sql).Select(item => item.Second);

    private static void AssertNoProvider(ScriptedConnection script)
    {
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Xorcom, script.CommandLog);
        Assert.DoesNotContain(SQLQueries.SP_PhoneSync_Ccm, script.CommandLog);
    }

    private static (ScriptedConnection Script, EquipmentAccountTransactionConnection Connection, DbManager Db) Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        return (script, connection, new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
    }
}
