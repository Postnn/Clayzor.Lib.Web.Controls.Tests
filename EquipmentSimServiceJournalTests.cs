using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет серверное подтверждение записи дополнительной услуги SIM.</summary>
public sealed class EquipmentSimServiceJournalTests
{
    private const int EmployeeId = 4;
    private const int EquipmentId = 7;
    private const string PhoneNumber = "79000000001";
    private static readonly DateTime BusinessDay = new(2026, 11, 27);
    private static readonly DateTime ChangedAt = new(2026, 11, 25, 9, 30, 0, DateTimeKind.Unspecified);
    private static string WriteSql => SQLQueries.UPDATE_SIMКартыДопУслугиСотОператораEnlisted;

    /// <summary>INSERT подтверждает выданный SQL ID и сохраняет эксклюзивную дату окончания.</summary>
    [Fact]
    public async Task InsertConfirmsExactServiceIdAndUsesExclusiveEnd()
    {
        var (service, script, journal, db) = Create([.. Access(), .. Access(), Proof(0, 42), .. Access()]);
        using var lifetime = db;
        var attempt = Guid.NewGuid();
        var draft = Draft(0);

        await service.SaveSimServiceAsync(draft, BusinessDay, EmployeeId, attempt, default);

        var receipt = await service.ReadSimServiceWriteReceiptAsync(EquipmentId, 0, EmployeeId, attempt, default);
        Assert.Equal(new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 42), receipt);
        Assert.Equal(0, draft.Id);
        Assert.Empty(journal.ScopesFor(attempt));
        var write = Assert.Single(ParametersFor(script, WriteSql));
        Assert.Equal(EquipmentId, write["КодОборудования"]);
        Assert.Equal(PhoneNumber, write["НомерТелефона"]);
        Assert.Equal(DBNull.Value, write["КодSIMКартыДопУслугиСотОператора"]);
        Assert.Equal(DBNull.Value, write["ИсходноеИзменено"]);
        Assert.Equal(draft.DateFrom!.Value.Date, write["От"]);
        Assert.Equal(draft.DateTo!.Value.Date.AddDays(1), write["До"]);
        Assert.Equal(draft.ActivationCost, write["СтоимостьВключения"]);
        Assert.Equal(draft.DailyCost, write["СтоимостьВдень"]);
        Assert.Equal(draft.MonthlyCost, write["СтоимостьВмесяц"]);
    }

    /// <summary>Потерянный ответ COMMIT не позволяет повторить запись или принять квитанцию другой услуги.</summary>
    [Fact]
    public async Task LostUpdateCommitCanBeReadAndRetriedWithoutAnotherDml()
    {
        const int serviceId = 15;
        var (service, script, journal, db) = Create([
            .. Access(), .. Access(), Proof(0, serviceId), .. Access(), .. Access(), .. Access(),
        ]);
        using var lifetime = db;
        journal.LoseNextConfirmedCommitReply = true;
        var attempt = Guid.NewGuid();
        var draft = Draft(serviceId);

        var lost = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveSimServiceAsync(draft, BusinessDay, EmployeeId, attempt, default));
        Assert.Equal(EquipmentWritePhase.Unknown, lost.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, journal.StateFor(attempt));
        Assert.Equal(["equipment/sim-service/15"], journal.ScopesFor(attempt));
        Assert.Equal(new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, serviceId),
            await service.ReadSimServiceWriteReceiptAsync(EquipmentId, serviceId, EmployeeId, attempt, default));
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            service.ReadSimServiceWriteReceiptAsync(EquipmentId, serviceId + 1, EmployeeId, attempt, default));

        await service.SaveSimServiceAsync(draft, BusinessDay, EmployeeId, attempt, default);

        var write = Assert.Single(ParametersFor(script, WriteSql));
        Assert.Equal(serviceId, write["КодSIMКартыДопУслугиСотОператора"]);
        Assert.Equal(ChangedAt, write["ИсходноеИзменено"]);
    }

    /// <summary>Серверный CAS даёт отказ, а неверный ID ответа оставляет неопределённый исход.</summary>
    [Theory]
    [InlineData(3, null, EquipmentWritePhase.Rejected, InventoryOperationState.Rejected)]
    [InlineData(0, 16, EquipmentWritePhase.Unknown, InventoryOperationState.Reserved)]
    public async Task UpdateRequiresExactProof(int status, int? returnedId, EquipmentWritePhase phase, InventoryOperationState state)
    {
        var (service, script, journal, db) = Create([.. Access(), .. Access(), Proof(status, returnedId)]);
        using var lifetime = db;
        var attempt = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveSimServiceAsync(Draft(15), BusinessDay, EmployeeId, attempt, default));

        Assert.Equal(phase, error.Phase);
        Assert.Equal(state, journal.StateFor(attempt));
        Assert.Single(ParametersFor(script, WriteSql));
    }

    /// <summary>Свежая проверка роли и типа внутри транзакции блокирует DML после резервирования.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedRoleOrChangedTypeRejectsBeforeWriting(bool roleRevoked)
    {
        var (service, script, journal, db) = Create([
            .. Access(), .. Access(hasRole: !roleRevoked, hasSimType: false),
        ]);
        using var lifetime = db;
        var attempt = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveSimServiceAsync(Draft(0), BusinessDay, EmployeeId, attempt, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, journal.StateFor(attempt));
        Assert.Empty(ParametersFor(script, WriteSql));
    }

    /// <summary>Отсутствующая бизнес-дата и фактические даты в будущем отклоняются до SQL.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBusinessDayRejectsWithoutSql(bool missing)
    {
        var (service, script, journal, db) = Create([]);
        using var lifetime = db;
        var attempt = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveSimServiceAsync(Draft(0), missing ? null : BusinessDay.AddDays(-2), EmployeeId, attempt, default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Null(journal.StateFor(attempt));
        Assert.Empty(script.CommandLog);
    }

    private static EquipmentSimService Draft(int serviceId) => new()
    {
        Id = serviceId, EquipmentId = EquipmentId, PhoneNumber = PhoneNumber,
        ServiceTypeId = "_TEST", ServiceName = "Услуга", ChangedAt = ChangedAt,
        DateFrom = BusinessDay.AddDays(-1).AddHours(9), DateTo = BusinessDay.AddHours(18),
        ActivationCost = 10.25m, DailyCost = 1.5m, MonthlyCost = 18.5m,
    };

    private static ScriptEntry[] Access(bool hasRole = true, bool hasSimType = true)
    {
        var actor = Script.Rows(Script.Row(("КодСотрудника", EmployeeId)));
        if (!hasRole)
            return [actor, Script.Rows()];
        return
        [
            actor,
            Script.Rows(Script.Row(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0))),
            Script.Rows(Script.Row(("ЕстьХарактеристикиSIM", hasSimType ? (byte)1 : (byte)0))),
        ];
    }

    private static ScriptEntry Proof(int status, int? id) => new()
    {
        ReaderFactory = () =>
        {
            var result = new DataTable();
            result.Columns.Add("Status", typeof(int));
            result.Columns.Add("ConfirmedServiceId", typeof(int));
            result.Rows.Add(status, (object?)id ?? DBNull.Value);
            return result.CreateDataReader();
        },
    };

    private static IEnumerable<Dictionary<string, object?>> ParametersFor(ScriptedConnection script, string sql) =>
        script.CommandLog.Zip(script.ParameterLog).Where(entry => entry.First == sql).Select(entry => entry.Second);

    private static (EquipmentFormService Service, ScriptedConnection Script, NetFormTests.NetJournalConnection Journal, DbManager Db)
        Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => journal);
        return (new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, db);
    }
}
