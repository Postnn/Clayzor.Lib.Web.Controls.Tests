using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет подтверждение идентификатора процедуры в серверном журнале создания.</summary>
public sealed class EquipmentCreationJournalTests
{
    /// <summary>Импорт прежней попытки блокирует создание, не подтверждая выдачу номера оборудования.</summary>
    [Fact]
    public async Task LegacyCreationEvidenceBlocksScopeWithoutConfirmingInsert()
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>([.. Actor(), .. Actor(), .. Actor()]),
            emulateEquipmentIdentity: false);
        var connection = new NetFormTests.NetJournalConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var attempt = Guid.NewGuid();

        await Equipment.ImportLegacyCreationOperationsAsync(db, "fixture", 4, [attempt], default);

        Assert.True(connection.HasLegacyEvidence(attempt));
        Assert.True(await Equipment.ReadCreationPendingAsync(db, "fixture", 4, default));
        Assert.Null(await Equipment.ReadCreationReceiptAsync(db, "fixture", 4, attempt, default));
        Assert.DoesNotContain(SQLQueries.SP_Оборудование_Ins, script.CommandLog);
    }

    /// <summary>Потерянный ответ на COMMIT не мешает прочитать точный идентификатор без повторной вставки.</summary>
    [Fact]
    public async Task LostCreationCommitReturnsOutputIdWithoutCallingProcedureAgain()
    {
        var entries = new Queue<ScriptEntry>([
            .. Actor(),
            .. ReferenceRows(),
            .. Actor(),
            .. ReferenceRows(),
            new ScriptEntry
            {
                SetNonQueryOutputs = parameters =>
                    parameters.Cast<DbParameter>().Single(parameter => parameter.Direction == ParameterDirection.Output).Value = 42,
            },
            .. Actor(),
            .. Actor(),
        ]);
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(entries, emulateEquipmentIdentity: false);
        var connection = new NetFormTests.NetJournalConnection(script) { LoseNextConfirmedCommitReply = true };
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var attempt = Guid.NewGuid();
        var equipment = Draft();

        await Assert.ThrowsAsync<TimeoutException>(() => equipment.InsertJournaledAsync(db, "fixture", 4, attempt, default));
        Assert.Equal(0, equipment.Id);
        var receipt = await Equipment.ReadCreationReceiptAsync(db, "fixture", 4, attempt, default);
        Assert.Equal(new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 42), receipt);
        Assert.Equal(42, await equipment.InsertJournaledAsync(db, "fixture", 4, attempt, default));
        Assert.Single(script.CommandLog, sql => sql == SQLQueries.SP_Оборудование_Ins);
    }

    /// <summary>Отзыв роли внутри транзакции не передаёт управление процедуре.</summary>
    [Fact]
    public async Task RoleRevocationAfterReservationRejectsWithoutCreatingEquipment()
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>([
            .. Actor(), .. ReferenceRows(), .. Actor(allowCreate: false), .. Actor(),
        ]), emulateEquipmentIdentity: false);
        var connection = new NetFormTests.NetJournalConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var attempt = Guid.NewGuid();

        await Assert.ThrowsAsync<EquipmentCreationRejectedException>(() =>
            Draft().InsertJournaledAsync(db, "fixture", 4, attempt, default));

        var receipt = await Equipment.ReadCreationReceiptAsync(db, "fixture", 4, attempt, default);
        Assert.Equal(new InventoryOperationReceipt(attempt, InventoryOperationState.Rejected, null), receipt);
        Assert.DoesNotContain(SQLQueries.SP_Оборудование_Ins, script.CommandLog);
    }

    private static ScriptEntry[] Actor(bool allowCreate = true) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", 4))),
        allowCreate ? Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0)))
            : Script.Rows(),
    ];

    private static ScriptEntry[] ReferenceRows() =>
    [
        Script.Rows(Script.Row(("КодТипаОборудования", 2), ("ТребуетсяСерийныйНомер", (byte)1))),
        Script.Rows(Script.Row(("КодМоделиОборудования", 3), ("КодТипаОборудования", 2))),
    ];

    private static Equipment Draft() => new()
    {
        EquipmentTypeId = 2,
        ModelId = 3,
        OwnerId = 4,
        LocationId = 6,
        SerialNumber = "serial",
        ReceivedAtUtc = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc),
    };
}
