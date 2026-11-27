using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет SQL-хранилище серверного плана настройки АТС оборудования.</summary>
public sealed class EquipmentAtsWorkflowTests
{
    private const string Principal = @"DOMAIN\fixture";
    private const int EmployeeId = 4;
    private const int EquipmentId = 17;
    private const int OwnerId = 12;
    private static readonly DateTime ChangedAt = new(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>Фиксирует только серверные цели и конфликтующие области в одной транзакции.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenConfigureRecordsExactPlanAndScopes(bool xorcom)
    {
        var id = Guid.NewGuid();
        string[] scopes = xorcom ? ["equipment/17", "tel/3", "tel/9"] : ["equipment/17", "equipment/77", "tel/3", "tel/9"];
        var (script, connection, db) = Create([
            .. Access(), Script.Rows(),
            Script.Rows(Link(9, 77), Link(3, 77)),
            Script.Rows(Station(3, xorcom ? 2 : 0, xorcom ? "pbx-three" : "")),
            Script.Rows(Station(9, 0, "")),
            .. scopes.Select(_ => Script.Rows(Script.Row(("Pending", false)))),
            Script.Scalar(1),
            .. scopes.Select(_ => Script.Scalar(1)),
            Script.Scalar(1),
        ]);
        using var lifetime = db;

        var plan = await EquipmentAtsWorkflow.OpenConfigureAsync(db, Principal, EmployeeId, EquipmentId, id, default);

        Assert.Equal(id, plan.Header.AttemptId);
        Assert.Equal(EmployeeId, plan.Header.EmployeeId);
        Assert.Equal(EquipmentId, plan.Header.EquipmentId);
        Assert.Equal((byte)EquipmentPhoneSyncOrigin.ExplicitConfigure, plan.Header.Origin);
        Assert.Equal(ChangedAt, plan.Header.ExpectedChangedAtUtc);
        Assert.Equal(OwnerId, plan.Header.OwnerId);
        Assert.Matches("^[0-9a-f]{64}$", plan.Header.ContextFingerprint);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(new EquipmentAtsStep(0, xorcom ? EquipmentAtsStep.Xorcom : EquipmentAtsStep.Ccm,
            xorcom ? 3 : 77, false, EquipmentAtsStep.Planned), step);

        Assert.Equal(scopes, ParametersFor(script, SQLQueries.SELECT_ClayzorOperationJournalOpenSlotForUpdate)
            .Select(parameters => Assert.IsType<string>(parameters["ConflictKey"])));
        Assert.Equal(scopes, ParametersFor(script, SQLQueries.INSERT_EquipmentAtsScope)
            .Select(parameters => Assert.IsType<string>(parameters["ConflictKey"])));
        Assert.All(ParametersFor(script, SQLQueries.INSERT_EquipmentAtsScope), parameters =>
        {
            Assert.Equal(id, parameters["AttemptId"]);
            Assert.Equal(Guid.Empty, parameters["OpenSlot"]);
        });
        var storedStep = Assert.Single(ParametersFor(script, SQLQueries.INSERT_EquipmentAtsStep));
        Assert.Equal(step.TargetId, storedStep["TargetId"]);
        Assert.Equal(step.StepIndex, storedStep["StepIndex"]);
        Assert.Equal(step.Kind, storedStep["Kind"]);
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    /// <summary>Роль по другому лицу не разрешает открыть план для владельца оборудования.</summary>
    [Fact]
    public async Task OpenConfigureRejectsRoleForAnotherPersonBeforeJournalWrites()
    {
        var (script, connection, db) = Create(Access(personId: OwnerId + 1));
        using var lifetime = db;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EquipmentAtsWorkflow.OpenConfigureAsync(
            db, Principal, EmployeeId, EquipmentId, Guid.NewGuid(), default));

        Assert.Equal(3, script.CommandLog.Count);
        AssertNoWrites(script);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Читает активный план по точной паре сотрудника и оборудования и проверяет владельца ответа.</summary>
    [Theory]
    [InlineData(EmployeeId, EquipmentId, true)]
    [InlineData(EmployeeId + 1, EquipmentId, false)]
    [InlineData(EmployeeId, EquipmentId + 1, false)]
    public async Task ReadActiveConfigureChecksReturnedOwner(int storedEmployeeId, int storedEquipmentId, bool allowed)
    {
        var id = Guid.NewGuid();
        var entries = Access().Append(Header(id, storedEmployeeId, storedEquipmentId));
        if (allowed) entries = entries.Append(Step(EquipmentAtsStep.Planned));
        var (script, connection, db) = Create(entries.ToArray());
        using var lifetime = db;

        if (allowed)
        {
            var loaded = await EquipmentAtsWorkflow.ReadConfigureAsync(db, Principal, EmployeeId, EquipmentId, null, default);
            Assert.NotNull(loaded);
            Assert.Equal(id, loaded.Header.AttemptId);
            Assert.Equal(3, Assert.Single(loaded.Steps).TargetId);
            Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EquipmentAtsWorkflow.ReadConfigureAsync(
                db, Principal, EmployeeId, EquipmentId, null, default));
            Assert.DoesNotContain(SQLQueries.SELECT_EquipmentAtsSteps, script.CommandLog);
            Assert.DoesNotContain("commit", connection.TransactionLog);
        }

        var keys = Assert.Single(ParametersFor(script, SQLQueries.SELECT_EquipmentAtsActiveConfigure));
        Assert.Equal(EmployeeId, keys["EmployeeId"]);
        Assert.Equal(EquipmentId, keys["EquipmentId"]);
        AssertNoWrites(script);
    }

    /// <summary>Повтор незавершённого шага и подмена цели отклоняются до записи фазы отправки.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BeginConfigureDoesNotWriteForDispatchingOrForeignTarget(bool dispatching)
    {
        var id = Guid.NewGuid();
        var (script, connection, db) = Create([
            .. Access(), Header(id), Step(dispatching ? EquipmentAtsStep.Dispatching : EquipmentAtsStep.Planned),
        ]);
        using var lifetime = db;
        var requested = new EquipmentAtsStep(0, EquipmentAtsStep.Xorcom, dispatching ? 3 : 9, false,
            EquipmentAtsStep.Planned);

        if (dispatching)
            await Assert.ThrowsAsync<InventoryOperationPendingException>(() => EquipmentAtsWorkflow.BeginConfigureStepAsync(
                db, Principal, EmployeeId, EquipmentId, id, requested, default));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EquipmentAtsWorkflow.BeginConfigureStepAsync(
                db, Principal, EmployeeId, EquipmentId, id, requested, default));

        Assert.DoesNotContain(SQLQueries.SELECT_ТелефонныеНомераОборудования, script.CommandLog);
        AssertNoWrites(script);
        Assert.DoesNotContain("commit", connection.TransactionLog);
    }

    /// <summary>Изменившаяся станция блокирует отправку даже при неизменном коде телефонного номера.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginRechecksStationAndConfirmsDispatchBeforeSend(bool stationChanged)
    {
        var id = Guid.NewGuid();
        var remembered = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { PhoneId = 3, ConnectionEquipmentId = 77, AutoDialNumber = "", AutoDialDelay = (byte?)null,
                IsDefault = false, StationId = 2, XorcomName = "pbx-three" },
        });
        var hash = Convert.ToHexStringLower(SHA256.HashData(remembered));
        var entries = Access().Concat([Header(id, fingerprint: hash), Step(EquipmentAtsStep.Planned),
            Script.Rows(Link(3, 77)), Script.Rows(Station(3, 2, stationChanged ? "pbx-other" : "pbx-three"))]);
        if (!stationChanged) entries = entries.Append(Script.Scalar(1));
        var (script, connection, db) = Create(entries.ToArray());
        using var lifetime = db;
        var step = new EquipmentAtsStep(0, EquipmentAtsStep.Xorcom, 3, false, EquipmentAtsStep.Planned);

        if (stationChanged)
        {
            await Assert.ThrowsAsync<EquipmentAtsContextChangedException>(() => EquipmentAtsWorkflow.BeginConfigureStepAsync(
                db, Principal, EmployeeId, EquipmentId, id, step, default));
            AssertNoWrites(script);
            Assert.DoesNotContain("commit", connection.TransactionLog);
            return;
        }

        Assert.True(await EquipmentAtsWorkflow.BeginConfigureStepAsync(db, Principal, EmployeeId, EquipmentId, id, step, default));
        var transition = Assert.Single(ParametersFor(script, SQLQueries.UPDATE_EquipmentAtsStepState));
        Assert.Equal(id, transition["AttemptId"]);
        Assert.Equal(EquipmentAtsStep.Planned, transition["ExpectedState"]);
        Assert.Equal(EquipmentAtsStep.Dispatching, transition["NextState"]);
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    /// <summary>Последний шаг подтверждается по CAS и освобождает области. Подтверждённый шаг идемпотентен.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmLastConfigureStepClosesWorkflowOnce(bool alreadyConfirmed)
    {
        var id = Guid.NewGuid();
        var prior = alreadyConfirmed ? EquipmentAtsStep.Confirmed : EquipmentAtsStep.Dispatching;
        var entries = Access().Append(Header(id, state: alreadyConfirmed ? (byte)1 : (byte)0)).Append(Step(prior));
        if (!alreadyConfirmed) entries = entries.Concat([Script.Scalar(1), Script.Scalar(1)]);
        var (script, connection, db) = Create(entries.ToArray());
        using var lifetime = db;
        var requested = new EquipmentAtsStep(0, EquipmentAtsStep.Xorcom, 3, false, EquipmentAtsStep.Planned);

        await EquipmentAtsWorkflow.ConfirmConfigureStepAsync(db, Principal, EmployeeId, EquipmentId, id, requested, default);

        if (alreadyConfirmed)
            AssertNoWrites(script);
        else
        {
            var transitionIndex = script.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsStepState);
            var completedIndex = script.CommandLog.IndexOf(SQLQueries.UPDATE_EquipmentAtsCompleted);
            Assert.True(transitionIndex >= 0 && completedIndex > transitionIndex);
            var transition = Assert.Single(ParametersFor(script, SQLQueries.UPDATE_EquipmentAtsStepState));
            Assert.Equal(id, transition["AttemptId"]);
            Assert.Equal(EquipmentAtsStep.Dispatching, transition["ExpectedState"]);
            Assert.Equal(EquipmentAtsStep.Confirmed, transition["NextState"]);
            Assert.Equal(3, transition["TargetId"]);
            Assert.Equal(id, Assert.Single(ParametersFor(script, SQLQueries.UPDATE_EquipmentAtsCompleted))["AttemptId"]);
            Assert.Contains("UPDATE dbo.ClayzorEquipmentAtsScope SET OpenSlot = @AttemptId", script.CommandLog[completedIndex]);
            var enlisted = connection.Commands[transitionIndex].Transaction;
            Assert.NotNull(enlisted);
            Assert.Equal(IsolationLevel.Serializable, enlisted.IsolationLevel);
            Assert.Same(enlisted, connection.Commands[completedIndex].Transaction);
        }
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    /// <summary>Смена контекста освобождает план только при доказанном отсутствии отправки.</summary>
    [Theory]
    [InlineData(EquipmentAtsStep.Planned, true)]
    [InlineData(EquipmentAtsStep.Dispatching, false)]
    [InlineData(EquipmentAtsStep.Confirmed, false)]
    public async Task AbandonReleasesOnlyNeverDispatchedPlan(byte state, bool abandoned)
    {
        var id = Guid.NewGuid();
        var entries = Access().Append(Header(id)).Append(Step(state));
        if (abandoned) entries = entries.Append(Script.Scalar(1));
        var (script, connection, db) = Create(entries.ToArray());
        using var lifetime = db;

        Assert.Equal(abandoned, await EquipmentAtsWorkflow.TryAbandonUndispatchedConfigureAsync(
            db, Principal, EmployeeId, EquipmentId, id, default));
        Assert.Equal(abandoned ? 1 : 0, script.CommandLog.Count(sql => sql == SQLQueries.UPDATE_EquipmentAtsAbandoned));
        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
    }

    private static ScriptEntry[] Access(int personId = OwnerId) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", personId))),
        Script.Rows(Script.Row(("КодОборудования", EquipmentId), ("КодЛицаВладельца", OwnerId),
            ("КодТипаОборудования", (int)ТипыОборудования.IP_телефон), ("Изменено", ChangedAt))),
    ];

    private static Dictionary<string, object?> Link(int phoneId, int connectionId) =>
        Script.Row(("КодТелефонногоНомера", phoneId), ("КодОборудования", EquipmentId),
            ("КодОборудованияПодключения", connectionId));

    private static Dictionary<string, object?> Station(int phoneId, int stationId, string xorcomName) =>
        Script.Row(("КодТелефонногоНомера", phoneId), ("КодТелефоннойСтанции", stationId), ("XorcomName", xorcomName));

    private static ScriptEntry Header(Guid id, int employeeId = EmployeeId, int equipmentId = EquipmentId,
        byte state = 0, string? fingerprint = null) => TypedRow(
        ("AttemptId", id), ("EmployeeId", employeeId), ("EquipmentId", equipmentId),
        ("Origin", (byte)EquipmentPhoneSyncOrigin.ExplicitConfigure), ("ExpectedChangedAtUtc", ChangedAt),
        ("OwnerId", OwnerId), ("ContextFingerprint", fingerprint ?? new string('a', 64)), ("State", state));

    private static ScriptEntry Step(byte state) => TypedRow(
        ("StepIndex", 0), ("Kind", EquipmentAtsStep.Xorcom), ("TargetId", 3), ("Deleting", false), ("State", state));

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

    private static IEnumerable<Dictionary<string, object?>> ParametersFor(ScriptedConnection script, string sql) =>
        script.CommandLog.Zip(script.ParameterLog).Where(entry => entry.First == sql).Select(entry => entry.Second);

    private static void AssertNoWrites(ScriptedConnection script) =>
        Assert.DoesNotContain(script.CommandLog, sql => sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));

    private static (ScriptedConnection Script, EquipmentAccountTransactionConnection Connection, DbManager Db) Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        return (script, connection, new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection));
    }
}
