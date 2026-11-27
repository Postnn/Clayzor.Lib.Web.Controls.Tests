using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет авторизацию и подтверждённые переходы на SQL-границе плана АТС.</summary>
public sealed class TelAtsWorkflowTests
{
    private const string Principal = @"DOMAIN\fixture";
    private static readonly string Fingerprint = new('A', 64);
    private static readonly string NextFingerprint = new('b', 64);

    /// <summary>Отправка разрешена только для нового подтверждённого перехода в Dispatching.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task BeginStepAcceptsOnlyConfirmedProtocolStatus(int sqlStatus, bool allowDispatch)
    {
        var (script, connection, db) = Create([.. Actor(), Script.Scalar(sqlStatus)]);
        using var lifetime = db;
        var attemptId = Guid.NewGuid();

        Assert.Equal(allowDispatch, await TelAtsWorkflow.BeginStepAsync(db, Principal, 4, attemptId,
            17, beforeSql: false, stepIndex: 3, Fingerprint, default));

        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
        var step = script.ParameterLog.Last();
        Assert.Equal(attemptId, step["AttemptId"]);
        Assert.Equal(17, step["PhoneId"]);
        Assert.Equal(false, step["BeforeSql"]);
        Assert.Equal(3, step["StepIndex"]);
        Assert.Equal(Fingerprint.ToLowerInvariant(), step["Fingerprint"]);
        var commands = connection.Commands;
        Assert.Equal(IsolationLevel.Serializable, commands[0].Transaction!.IsolationLevel);
        Assert.All(commands, command => Assert.Same(commands[0].Transaction, command.Transaction));
    }

    /// <summary>Конфликт или неизвестный SQL-статус не дают разрешение на отправку.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task BeginStepFailsClosedWhenStatusDoesNotAuthorizeDispatch(int sqlStatus)
    {
        var (_, connection, db) = Create([.. Actor(), Script.Scalar(sqlStatus)]);
        using var lifetime = db;
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() => TelAtsWorkflow.BeginStepAsync(
            db, Principal, 4, Guid.NewGuid(), 17, true, 0, Fingerprint, default));
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Чужой сотрудник или отозванная роль не достигают перехода АТС.</summary>
    [Theory]
    [InlineData(8, true)]
    [InlineData(4, false)]
    public async Task BeginStepRechecksTheEmployeeAndRole(int resolvedId, bool hasRole)
    {
        var (script, connection, db) = Create(Actor(resolvedId, hasRole));
        using var lifetime = db;
        await Assert.ThrowsAsync<TelSqlRejectedException>(() => TelAtsWorkflow.BeginStepAsync(
            db, Principal, 4, Guid.NewGuid(), 17, true, 0, Fingerprint, default));
        Assert.Equal(2, script.CommandLog.Count);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Область и заранее заданные хэши до SQL проверяются и сохраняются одной транзакцией.</summary>
    [Fact]
    public async Task BeginPersistsOrderedPreparedStepsUnderTheScopeLock()
    {
        var (script, connection, db) = Create([.. Actor(), Script.Rows(Script.Row(("Value", false))),
            Script.Scalar(1), Script.Scalar(1), Script.Scalar(1)]);
        using var lifetime = db;
        var attemptId = Guid.NewGuid();
        await TelAtsWorkflow.BeginAsync(db, Principal, 4, attemptId, 17, deleting: true,
            [Fingerprint, NextFingerprint], default);

        Assert.Equal("tel/17", script.ParameterLog[2]["ConflictKey"]);
        Assert.Equal(attemptId, script.ParameterLog[2]["AttemptId"]);
        Assert.Equal(2, script.ParameterLog[3]["PreStepCount"]);
        Assert.Equal("tel/17", script.ParameterLog[3]["ConflictKey"]);
        Assert.Equal(17, script.ParameterLog[3]["OriginalPhoneId"]);
        var first = script.ParameterLog[4];
        var second = script.ParameterLog[5];
        Assert.Equal(0, first["StepIndex"]);
        Assert.Equal(1, second["StepIndex"]);
        Assert.Equal(true, first["BeforeSql"]);
        Assert.Equal(true, second["BeforeSql"]);
        Assert.Equal(Fingerprint.ToLowerInvariant(), first["Fingerprint"]);
        Assert.Equal(NextFingerprint, second["Fingerprint"]);
        Assert.All(connection.Commands, command => Assert.Same(connection.Commands[0].Transaction, command.Transaction));
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    /// <summary>Несовпадение шага после SQL не коммитит подготовку оставшихся шагов.</summary>
    [Fact]
    public async Task MarkSqlReadyDoesNotCommitIfPreparedStepDoesNotMatch()
    {
        var (script, connection, db) = Create([.. Actor(), Script.Scalar(1), Script.Scalar(0)]);
        using var lifetime = db;
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() => TelAtsWorkflow.MarkSqlReadyAsync(db, Principal, 4,
            Guid.NewGuid(), [Fingerprint], default));
        Assert.Equal(1, script.ParameterLog[2]["PostStepCount"]);
        Assert.Equal(false, script.ParameterLog[3]["BeforeSql"]);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Исход SQL фиксируется внутри уже открытой транзакции записывающего домена.</summary>
    [Fact]
    public async Task ConfirmSqlUsesTheProvidedTransactionAndDoesNotCommitIt()
    {
        var (script, connection, db) = Create([Script.Scalar(1)]);
        using var lifetime = db;
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var attemptId = Guid.NewGuid();

        await TelAtsWorkflow.ConfirmSqlAsync(connection, transaction, attemptId, 4, 0, 17, default);

        Assert.Same(transaction, Assert.Single(connection.Commands).Transaction);
        Assert.Equal(attemptId, script.ParameterLog.Single()["AttemptId"]);
        Assert.Equal(0, script.ParameterLog.Single()["OriginalPhoneId"]);
        Assert.Equal(17, script.ParameterLog.Single()["ResultPhoneId"]);
        Assert.Equal("tel/17", script.ParameterLog.Single()["ResultConflictKey"]);
        Assert.Equal(new[] { "begin" }, connection.TransactionLog);
    }

    /// <summary>Сводка содержит только счётчики собственной открытой попытки и ограничена областью SQL-запроса.</summary>
    [Fact]
    public async Task ReadActiveWorkflowUsesResolvedEmployeeAndExactScope()
    {
        var attempt = Guid.NewGuid();
        var (script, connection, db) = Create([.. Actor(), TypedRow(
            ("AttemptId", attempt), ("SqlConfirmed", true), ("PreStepCount", 2), ("ConfirmedPreSteps", 2),
            ("PostStepCount", 3), ("ConfirmedPostSteps", 1), ("DispatchingSteps", 1))]);
        using var lifetime = db;
        var service = new PhoneFormService(db);

        var actual = await service.ReadActiveWorkflowAsync(Principal, 4, 17, default);

        Assert.Equal(new TelWorkflowSummary(attempt, true, 2, 2, 3, 1, 1), actual);
        Assert.Equal("tel/17", script.ParameterLog.Last()["Scope"]);
        Assert.Equal(4, script.ParameterLog.Last()["EmployeeId"]);
        Assert.Empty(connection.TransactionLog);
    }

    /// <summary>Отозванная роль запрещает чтение счётчиков даже по известной области.</summary>
    [Fact]
    public async Task ReadActiveWorkflowRejectsRevokedRole()
    {
        var (script, _, db) = Create(Actor(hasRole: false));
        using var lifetime = db;
        var service = new PhoneFormService(db);
        await Assert.ThrowsAsync<TelSqlRejectedException>(() => service.ReadActiveWorkflowAsync(Principal, 4, 17, default));
        Assert.Equal(2, script.CommandLog.Count);
    }

    /// <summary>Миграция не меняет уже существующий собственный процесс и не принимает чужой.</summary>
    [Theory]
    [InlineData(4, 17, true, true)]
    [InlineData(8, 17, true, false)]
    [InlineData(4, 18, true, false)]
    [InlineData(4, 17, false, false)]
    public async Task ImportRequiresTheExactDurableWorkflowOwner(int actorId, int phoneId, bool deleting, bool matching)
    {
        var attempt = Guid.NewGuid();
        var (script, connection, db) = Create([.. Actor(), TypedRow(
            ("EmployeeId", actorId), ("OriginalPhoneId", phoneId), ("Deleting", deleting))]);
        using var lifetime = db;
        var service = new PhoneFormService(db);
        var import = () => service.ImportLegacyTelOperationsAsync(Principal, 4, 17, [(attempt, true)], default);

        if (matching)
            await import();
        else
            await Assert.ThrowsAsync<InventoryOperationPendingException>(import);

        Assert.Equal(attempt, script.ParameterLog.Last()["AttemptId"]);
        Assert.Equal(3, script.CommandLog.Count);
        Assert.Equal(matching, connection.TransactionLog.Contains("commit"));
        Assert.All(connection.Commands, command => Assert.Same(connection.Commands[0].Transaction, command.Transaction));
    }


    /// <summary>Подтверждение SQL создания добавляет действительный номер к блокировке старой попытки АТС.</summary>
    [Fact]
    public async Task ImportConfirmedLegacyCreationRetainsUnknownAndBlocksTheAuthoritativeResult()
    {
        var attempt = Guid.NewGuid();
        var sqlReceipt = TypedRow(("AttemptId", attempt), ("EmployeeId", 4), ("ConflictKey", "tel/new"),
            ("CommandKind", "tel.sql.insert"), ("State", (byte)InventoryOperationState.Confirmed), ("ResultEntityId", 17));
        var (script, connection, db) = Create([.. Actor(), Script.Rows(), sqlReceipt, sqlReceipt,
            Script.Rows(Script.Row(("Value", true))), Script.Scalar(1)]);
        using var lifetime = db;
        var service = new PhoneFormService(db);

        await service.ImportLegacyTelOperationsAsync(Principal, 4, 0, [(attempt, false)], default);

        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
        Assert.Equal(SQLQueries.INSERT_ClayzorLegacyOperationEvidence, script.CommandLog[^2]);
        Assert.Equal("tel/new", script.ParameterLog[^2]["ConflictKey"]);
        Assert.Equal("tel.sql.insert", script.ParameterLog[^2]["CommandKind"]);
        Assert.Equal(SQLQueries.INSERT_ClayzorLegacyOperationScope, script.CommandLog[^1]);
        Assert.Equal("tel/17", script.ParameterLog[^1]["ConflictKey"]);
        Assert.Equal(attempt, script.ParameterLog[^1]["AttemptId"]);
        Assert.All(connection.Commands, command => Assert.Same(connection.Commands[0].Transaction, command.Transaction));
    }

    /// <summary>Чужое подтверждение SQL не превращается в свидетельство или блокировку по результату.</summary>
    [Fact]
    public async Task ImportRejectsLegacyCreationWithAnotherEmployeesSqlReceipt()
    {
        var attempt = Guid.NewGuid();
        var (script, connection, db) = Create([.. Actor(), Script.Rows(), TypedRow(
            ("AttemptId", attempt), ("EmployeeId", 8), ("ConflictKey", "tel/new"),
            ("CommandKind", "tel.sql.insert"), ("State", (byte)InventoryOperationState.Confirmed), ("ResultEntityId", 17))]);
        using var lifetime = db;
        var service = new PhoneFormService(db);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            service.ImportLegacyTelOperationsAsync(Principal, 4, 0, [(attempt, false)], default));

        Assert.DoesNotContain(SQLQueries.INSERT_ClayzorLegacyOperationEvidence, script.CommandLog);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }


    private static ScriptEntry[] Actor(int id = 4, bool hasRole = true) =>
        [Script.Rows(Script.Row(("КодСотрудника", id))), hasRole
            ? Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)))
            : Script.Rows()];

    private static ScriptEntry TypedRow(params (string Name, object Value)[] values) => new()
    {
        ReaderFactory = () =>
        {
            var table = new DataTable();
            foreach (var (name, value) in values)
                table.Columns.Add(name, value.GetType());
            table.Rows.Add(values.Select(item => item.Value).ToArray());
            return table.CreateDataReader();
        },
    };

    private static (ScriptedConnection Script, EquipmentAccountTransactionConnection Connection, DbManager Db) Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        return (script, connection, new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
    }
}
