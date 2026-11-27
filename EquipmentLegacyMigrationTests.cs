using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет авторитетные области переноса прежних операций с моделью оборудования.</summary>
public sealed class EquipmentLegacyMigrationTests
{
    /// <summary>Оба редактора используют канонический ключ модели, даже при разных командах.</summary>
    [Theory]
    [InlineData(false, 0, "model/new", "equipment-model.save")]
    [InlineData(false, 7, "model/7", "equipment-model.save")]
    [InlineData(true, 7, "model/7", "equipment-phone-template.save")]
    public async Task ImportRecordsUnresolvedEvidenceWithoutChangingModel(
        bool template, int modelId, string scope, string kind)
    {
        DapperColumnMapper.Initialize();
        var role = template ? Роли.Дежурный_инженер : Роли.Специалист_по_инвентаризации_оборудования;
        var rows = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(Script.Row(("RoleId", (int)role), ("PersonId", 0))),
        ]);
        if (modelId > 0)
            rows.Enqueue(Script.Rows(Script.Row(("КодМоделиОборудования", modelId),
                ("КодТипаОборудования", (int)ТипыОборудования.IP_телефон))));
        rows.Enqueue(Script.Rows());
        rows.Enqueue(Script.Rows(Script.Row(("Owned", true))));

        var script = new ScriptedConnection(rows, emulateEquipmentIdentity: false);
        var connection = new EquipmentAccountTransactionConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var id = Guid.NewGuid();

        if (template)
            await EquipmentModel.ImportLegacyPhoneTemplateOperationsAsync(db, "fixture", 4, modelId, [id], default);
        else
            await EquipmentModel.ImportLegacySaveOperationsAsync(db, "fixture", 4, modelId, [id], default);

        Assert.Equal(new[] { "begin", "commit" }, connection.TransactionLog);
        Assert.Equal(SQLQueries.INSERT_ClayzorLegacyOperationEvidence, script.CommandLog[^1]);
        Assert.Equal(id, script.ParameterLog[^1]["AttemptId"]);
        Assert.Equal(scope, script.ParameterLog[^1]["ConflictKey"]);
        Assert.Equal(kind, script.ParameterLog[^1]["CommandKind"]);
        Assert.DoesNotContain(SQLQueries.INSERT_ClayzorOperationJournalAttempt, script.CommandLog);
    }
}
