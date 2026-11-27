using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using static Clayzor.Lib.Web.Controls.Tests.ModelEquipmentWriteBoundaryTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет SQL-контракт независимых писателей модели и шаблона на фикстурах.</summary>
public sealed class ModelEquipmentConcurrencyTests
{
    private const string Principal = @"DOMAIN\fixture";
    private static readonly DateTime OriginalVersion = new(2026, 11, 17, 12, 30, 0);
    private const int IpPhone = (int)ТипыОборудования.IP_телефон;

    /// <summary>Обычная правка модели не включает шаблон даже при наличии устаревшего текста в объекте.</summary>
    [Fact]
    public async Task NormalWriterMustNotBindOrOverwriteTemplate()
    {
        var (db, connection) = Create([
            WriterRole(), Script.Rows(), Type(IpPhone), StoredPhone("новый шаблон"), Script.Scalar(0),
            Script.Rows(), NoPending(), Script.NonQuery(),
            WriterRole(), Type(IpPhone), StoredPhone("новый шаблон"), Script.Scalar(0), Script.Scalar(1), Script.Scalar(1),
        ]);
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = IpPhone, Name = "Rename", PhoneTemplate = "старый шаблон" };
        var baseline = new EquipmentModelWriteBaseline(7, IpPhone, OriginalVersion, "старый шаблон");

        Assert.Equal(7, await model.SaveAsync(db, Principal, 4, Guid.NewGuid(), baseline));

        var sql = Assert.Single(connection.CommandLog, IsWrite);
        Assert.Equal(SQLQueries.UPDATE_МоделиОборудования, sql);
        Assert.DoesNotContain("ШаблонТелефона", sql);
        Assert.Contains("Изменено = @ExpectedChangedAt", sql);
        Assert.Contains("SELECT @@ROWCOUNT", sql);
        var parameters = connection.ParameterLog[connection.CommandLog.IndexOf(sql)];
        Assert.DoesNotContain("PhoneTemplate", parameters.Keys);
        Assert.DoesNotContain("ExpectedPhoneTemplate", parameters.Keys);
        Assert.Equal(OriginalVersion, parameters["ExpectedChangedAt"]);
    }

    /// <summary>Очистка при смене типа использует тот шаблон, который видел пользователь.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeTransition_UsesOriginalTemplateAndRejectsConcurrentWrite(bool staleTemplate)
    {
        var (db, connection) = Create(staleTemplate
            ? [WriterRole(), Script.Rows(), Type(5), StoredPhone("новый шаблон")]
            : [WriterRole(), Script.Rows(), Type(5), StoredPhone("старый шаблон"), Script.Scalar(0),
                Script.Rows(), NoPending(), Script.NonQuery(),
                WriterRole(), Type(5), StoredPhone("старый шаблон"), Script.Scalar(0), Script.Scalar(1), Script.Scalar(1)]);
        var model = new EquipmentModel { Id = 7, EquipmentTypeId = 5, Name = "Local name" };
        var baseline = new EquipmentModelWriteBaseline(7, IpPhone, OriginalVersion, "старый шаблон");
        var attemptId = Guid.NewGuid();

        if (staleTemplate)
        {
            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                model.SaveAsync(db, Principal, 4, attemptId, baseline, confirmedTypeId: 5));
            Assert.Contains("были изменены либо удалены", conflict.Message);
            Assert.DoesNotContain(connection.CommandLog, IsWrite);
            return;
        }
        else
        {
            Assert.Equal(7, await model.SaveAsync(db, Principal, 4, attemptId, baseline, confirmedTypeId: 5));
        }

        Assert.Equal("Local name", model.Name);
        var sql = Assert.Single(connection.CommandLog, IsWrite);
        Assert.Equal(SQLQueries.UPDATE_МоделиОборудования_ОчиститьШаблон, sql);
        Assert.Contains("ШаблонТелефона = N''", sql);
        Assert.Contains("КодТипаОборудования <> @EquipmentTypeId", sql);
        Assert.Contains("CONVERT(varbinary(max), @ExpectedPhoneTemplate)", sql);
        var parameters = connection.ParameterLog[connection.CommandLog.IndexOf(sql)];
        Assert.Equal("старый шаблон", parameters["ExpectedPhoneTemplate"]);
        Assert.Equal(OriginalVersion, parameters["ExpectedChangedAt"]);
    }

    /// <summary>Устаревший шаблон или исчезнувший после проверки IP-телефон даёт конфликт.</summary>
    [Fact]
    public async Task StaleTemplateUpdate_UsesOriginalValueAndExplicitAffectedRows()
    {
        var (db, connection) = Create([
            StoredPhone("свежий шаблон"), Roles(Роли.Дежурный_инженер), Script.Scalar(0),
        ]);
        var model = new EquipmentModel { Id = 7, PhoneTemplate = "локальные настройки" };
        var baseline = new EquipmentModelWriteBaseline(7, IpPhone, OriginalVersion, "старый шаблон");

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => model.SavePhoneTemplateAsync(db, Principal, 4, baseline));

        Assert.Contains("были изменены либо удалены", conflict.Message);
        Assert.Equal("локальные настройки", model.PhoneTemplate);
        var sql = Assert.Single(connection.CommandLog, IsWrite);
        Assert.Contains("КодТипаОборудования = @PhoneEquipmentTypeId", sql);
        Assert.Contains("DATALENGTH(@ExpectedPhoneTemplate)", sql);
        Assert.Contains("SELECT @@ROWCOUNT", sql);
        Assert.Equal("старый шаблон", connection.ParameterLog.Last()["ExpectedPhoneTemplate"]);
        Assert.Equal("локальные настройки", connection.ParameterLog.Last()["PhoneTemplate"]);
    }

    /// <summary>Исходный и новый текст шаблона не попадают в журнал SQL-параметров даже при сбое связи.</summary>
    [Fact]
    public async Task TemplateConnectivityErrorDoesNotExposeSqlParameters()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            StoredPhone("исходный текст"), Roles(Роли.Дежурный_инженер),
            Script.Error(SqlExceptionFactory.Create(53, "fixture disconnected")),
        ]));
        var errors = new RecordingErrors();
        var db = new DbManager("Server=fixture", errors, () => connection);
        var model = new EquipmentModel { Id = 7, PhoneTemplate = "локальный текст" };
        var baseline = new EquipmentModelWriteBaseline(7, IpPhone, OriginalVersion, "исходный текст");

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            model.SavePhoneTemplateAsync(db, Principal, 4, baseline));

        Assert.Empty(Assert.Single(errors.Parameters));
    }

    /// <summary>Потеря ответа COMMIT не повторяет создание и не заменяет подтверждённый идентификатор.</summary>
    [Fact]
    public async Task LostCommitResponseUsesExactInsertReceiptWithoutAnotherModelWrite()
    {
        DapperColumnMapper.Initialize();
        var attemptId = Guid.NewGuid();
        var receipt = ModelJournalReceipt(attemptId, 0, 42);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            WriterRole(), Script.Rows(), Type(5), Script.Scalar(0),
            Script.Rows(), NoPending(), Script.NonQuery(),
            WriterRole(), Type(5), Script.Scalar(0), Script.Scalar(42), Script.Scalar(1),
            WriterRole(), receipt,
            WriterRole(), receipt,
        ]));
        var commits = 0;
        var transactionConnection = new TerritorialEntityCancelTests.TransactionConnection(connection, () =>
        {
            if (++commits == 2) throw new TimeoutException("Потерян ответ COMMIT.");
        });
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => transactionConnection);
        var model = new EquipmentModel { EquipmentTypeId = 5, Name = "Введённая модель" };

        await Assert.ThrowsAsync<TimeoutException>(() => model.SaveAsync(db, Principal, 4, attemptId));
        var readBack = await EquipmentModel.ReadSaveReceiptAsync(db, Principal, 4, 0, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, 42), readBack);
        Assert.Equal(42, await model.SaveAsync(db, Principal, 4, attemptId));
        Assert.Equal(2, commits);
        Assert.Equal(SQLQueries.INSERT_МоделиОборудования_Журнал, Assert.Single(connection.CommandLog, IsWrite));
        var confirmed = connection.ParameterLog[connection.CommandLog.IndexOf(SQLQueries.UPDATE_ClayzorOperationJournalState)];
        Assert.Equal(42, confirmed["ResultEntityId"]);
        Assert.DoesNotContain("Name", confirmed.Keys);
    }

    private sealed class RecordingErrors : ISqlErrorHandler
    {
        internal List<IReadOnlyList<(string Name, object? Value)>> Parameters { get; } = [];

        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) => Parameters.Add(parameters);
    }

    private static (DbManager Db, ScriptedConnection Connection) Create(IEnumerable<ScriptEntry> entries)
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries));
        return (new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => new EquipmentAccountTransactionConnection(connection)), connection);
    }

    private static ScriptEntry WriterRole() => Roles(Роли.Специалист_по_инвентаризации_оборудования);

    private static ScriptEntry StoredPhone(string template) => Script.Rows(Script.Row(
        ("КодМоделиОборудования", 7), ("КодТипаОборудования", IpPhone), ("Изменено", OriginalVersion),
        ("ШаблонТелефона", template)));
}
