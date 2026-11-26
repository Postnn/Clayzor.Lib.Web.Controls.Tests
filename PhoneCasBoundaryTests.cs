using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки транзакционного контракта версии Tel на построенном SQL и имитации DbCommand.</summary>
public sealed class PhoneCasBoundaryTests
{
    private static readonly DateTime Stamp = new DateTime(2026, 11, 26, 8, 9, 10, DateTimeKind.Unspecified).AddMilliseconds(123);
    private const string Principal = @"DOMAIN\fixture";

    /// <summary>Исходная версия связывается без часового сдвига, а PBX diff сохраняет прежнее место.</summary>
    [Fact]
    public async Task UnchangedVersion_UsesOriginalStampAndPreservesTransactionOrder()
    {
        var (service, connection) = Create(Script.Scalar(5));
        var request = Request(5);

        var result = await service.SaveAsync(request, Principal, 4);

        Assert.True(result.Saved);
        Assert.False(result.VersionConflict);
        var sql = connection.CommandLog.Last();
        var parameters = connection.ParameterLog.Last();
        var timestamp = Assert.IsType<DateTime>(parameters["ExpectedChangedAt"]);
        Assert.Equal(Stamp.Ticks, timestamp.Ticks);
        Assert.Equal(Stamp.Kind, timestamp.Kind);
        Assert.Contains("Изменено = @ExpectedChangedAt", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.True(sql.IndexOf("BEGIN TRANSACTION", StringComparison.Ordinal) < sql.IndexOf("IF NOT EXISTS", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("IF NOT EXISTS", StringComparison.Ordinal) < sql.IndexOf("INSERT INTO ТелефонныеНомераГруппы", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("INSERT INTO ТелефонныеНомераГруппы", StringComparison.Ordinal) < sql.IndexOf("UPDATE vwТелефонныеНомераИзменение", StringComparison.Ordinal));
        Assert.Matches(@"WHERE КодТелефонногоНомера = @Id\s*;\s*IF @@ROWCOUNT <> 1", sql);
    }

    /// <summary>Конфликт до изменения связей и ноль строк после сравнения возвращают только подтверждённый откат.</summary>
    [Theory]
    [InlineData("stale or missing")]
    [InlineData("update zero")]
    public async Task ReturnedRollbackCode_IsConflict(string source)
    {
        var (service, connection) = Create(Script.Scalar(-1));
        var result = await service.SaveAsync(Request(5), Principal, 4);
        var sql = connection.CommandLog.Last();
        var guard = sql.IndexOf(source == "update zero" ? "IF @@ROWCOUNT <> 1" : "IF NOT EXISTS", StringComparison.Ordinal);
        var rollback = sql.IndexOf("ROLLBACK TRANSACTION", guard, StringComparison.Ordinal);
        var status = sql.IndexOf("SELECT @VersionConflictStatus", rollback, StringComparison.Ordinal);
        var exit = sql.IndexOf("RETURN;", status, StringComparison.Ordinal);

        Assert.True(guard >= 0 && guard < rollback && rollback < status && status < exit);
        if (source == "stale or missing")
            Assert.True(exit < sql.IndexOf("INSERT INTO ТелефонныеНомераГруппы", StringComparison.Ordinal));
        else
            Assert.True(exit < sql.IndexOf("INSERT INTO vwТелефонныеНомераПараллельныеВызовы", StringComparison.Ordinal));
        Assert.False(result.Saved);
        Assert.True(result.VersionConflict);
        Assert.Equal(5, result.PhoneId);
        Assert.Equal(-1, connection.ParameterLog.Last()["VersionConflictStatus"]);
    }

    /// <summary>Не полученный ответ не подменяется доказанным CAS отклонением.</summary>
    [Fact]
    public async Task LostResponse_IsNotConfirmedRollback()
    {
        var (service, _) = Create(Script.Error(SqlExceptionFactory.Create(53, "fixture disconnected")));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => service.SaveAsync(Request(5), Principal, 4));
    }

    /// <summary>Новая запись не требует исходной версии и сохраняет legacy @@IDENTITY.</summary>
    [Fact]
    public async Task Insert_PreservesIdentityAndDoesNotBindOriginalVersion()
    {
        var (service, connection) = Create(Script.Scalar(42));
        var result = await service.SaveAsync(Request(0), Principal, 4);

        Assert.True(result.Saved);
        Assert.False(result.VersionConflict);
        Assert.Equal(42, result.PhoneId);
        Assert.Contains("SET @PhoneId = CONVERT(int, @@IDENTITY)", connection.CommandLog.Last());
        Assert.DoesNotContain("ExpectedChangedAt", connection.ParameterLog.Last().Keys);
    }

    /// <summary>Связи редактируются лишь у существующего номера исходной версии даже без UPDATE основной строки.</summary>
    [Fact]
    public void RelationsOnlySave_StillRequiresMatchingOriginalVersion()
    {
        var request = Request(5, mainChanged: false);
        var command = PhoneSaveBuilder.Build(request);

        Assert.Contains("Изменено = @ExpectedChangedAt", command.Sql);
        Assert.DoesNotContain("UPDATE vwТелефонныеНомераИзменение", command.Sql);
        Assert.Contains("INSERT INTO ТелефонныеНомераГруппы", command.Sql);
    }

    /// <summary>Пустая версия и подмена ID не формируют изменяющий batch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrForeignBaselineIsRejected(bool differentId)
    {
        var request = Request(5);
        if (differentId)
            request.OriginalPhone.Id = 18;
        else
            request.OriginalPhone.ChangedAtUtc = default;

        Assert.Throws<InvalidOperationException>(() => PhoneSaveBuilder.Build(request));
    }

    private static PhoneSaveRequest Request(int id, bool mainChanged = true)
    {
        var original = new Phone
        {
            Id = id, ChangedAtUtc = Stamp, Number = "101", FullNumber = "101", Notes = "Original",
            PhoneTypeId = 1, StationId = 1, CustomerId = 1, Connected = "Room", AtsDescription = "Room", ServicesKind = 1, ViewInDirectory = 1,
        };
        var phone = original.CopyForNew();
        phone.Id = id;
        phone.ChangedAtUtc = Stamp.AddDays(1);
        if (mainChanged) phone.Notes = "Edited";
        return new PhoneSaveRequest
        {
            OriginalPhone = original,
            Phone = phone,
            Relations = new PhoneRelationsSnapshot
            {
                GroupMembers = [new PhoneGroupMember { PhoneId = 8 }],
                ParallelCalls = [new PhoneParallelCall { CallNumber = "102" }],
            },
        };
    }

    private static (PhoneFormService Service, ScriptedConnection Connection) Create(ScriptEntry result)
    {
        DapperColumnMapper.Initialize();
        var script = new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
            result,
        ]);
        var connection = new ScriptedConnection(script, emulateEquipmentIdentity: false);
        return (new CasPhoneService(new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection)), connection);
    }

    private sealed class CasPhoneService(DbManager db) : PhoneFormService(db)
    {
        public override Task<IReadOnlyList<PhoneValidationIssue>> ValidateServerAsync(PhoneSaveRequest request) =>
            Task.FromResult<IReadOnlyList<PhoneValidationIssue>>([]);
    }
}
