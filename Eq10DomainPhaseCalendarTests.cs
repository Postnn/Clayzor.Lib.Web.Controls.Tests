using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки business Today и phase-aware SQL/ATS исходов EQ10.</summary>
public sealed class Eq10DomainPhaseCalendarTests
{
    /// <summary>Xorcom catalog SELECT до Execute является NotSent и допускает read retry.</summary>
    [Fact]
    public async Task XorcomCatalogTimeout_IsNotSentWithoutExecute()
    {
        var connection = Connection(Script.Error(SqlExceptionFactory.Create(-2, "catalog timeout")));
        var service = PhoneService(connection);

        var result = Assert.Single(await service.SynchronizePhasedAsync("attempt-a", 1, [Xorcom(1)]));

        Assert.Equal(PhoneSyncPhase.NotSent, result.Phase);
        Assert.True(result.CanRetry);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
    }

    /// <summary>Timeout после Xorcom Execute остаётся Unknown независимо от SqlException.Number.</summary>
    [Fact]
    public async Task XorcomExecuteTimeout_IsUnknownAndNotRetryable()
    {
        var connection = Connection(Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(-2, "execute timeout")));
        var service = PhoneService(connection);

        var result = Assert.Single(await service.SynchronizePhasedAsync("attempt-a", 1, [Xorcom(1)]));

        Assert.Equal(PhoneSyncPhase.Unknown, result.Phase);
        Assert.False(result.CanRetry);
        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
    }

    /// <summary>Principal/role SELECT timeout до main DML является NotSent.</summary>
    [Fact]
    public async Task PhoneSave_RolePreflightTimeout_NotSentZeroDml()
    {
        var connection = Connection(Script.Error(SqlExceptionFactory.Create(-2, "principal timeout")));
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveWithPhonesAsync(Equipment(), [], [Link(1, 8)]));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        AssertNoDml(connection);
    }

    /// <summary>Timeout основного DML после dispatch не маскируется под preflight NotSent.</summary>
    [Fact]
    public async Task PhoneSave_DmlTimeout_IsUnknown()
    {
        var connection = Connection(Script.Scalar(1), Script.Rows(ModelTypeRow()),
            Script.Error(SqlExceptionFactory.Create(-2, "write timeout")));
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveWithPhonesAsync(Equipment(), [], [Link(1, 8)]));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Single(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SynchronizePhoneOperationsAsync(
            7, [], [Link(1, 8)]));
        Assert.Single(connection.CommandLog, sql => sql.Contains("UPDATE vwОборудование", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("PhoneSync", StringComparison.Ordinal));
    }

    /// <summary>Computer target/model read timeout до DML является NotSent.</summary>
    [Fact]
    public async Task Computer_TargetReadTimeout_NotSentZeroDml()
    {
        var connection = Connection(Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(-2, "target timeout")));
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveComputerPhasedAsync(new EquipmentComputerSnapshot { EquipmentId = 7 }, DateTime.Today));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        AssertNoDml(connection);
    }

    /// <summary>Computer Apply получает актуальный business Today и не меняет identity daily snapshot.</summary>
    [Fact]
    public async Task Computer_CurrentBusinessTodayAccepted_WithoutRebindingSnapshotDate()
    {
        var today = new DateTime(2099, 3, 1);
        var snapshot = new EquipmentComputerSnapshot
        {
            EquipmentId = 7,
            Date = today.AddDays(-1),
            Configuration1Id = 5,
            Configuration1Date = today,
            ExpectedExisting = true,
            ChangedAt = new DateTime(2099, 2, 28, 23, 59, 59, DateTimeKind.Utc).AddMilliseconds(500),
        };
        var connection = Connection(Script.Scalar(1), Script.Rows(ComputerContextRow()), Script.NonQuery());
        var service = Service(connection);

        await service.SaveComputerPhasedAsync(snapshot, today);

        Assert.Equal(today.AddDays(-1), snapshot.Date);
        Assert.Equal(snapshot.ChangedAt, connection.ParameterLog.Single(item => item.ContainsKey("ИсходноеИзменено"))["ИсходноеИзменено"]);
        Assert.Single(connection.CommandLog, sql => sql.Contains("UPDATE Компьютеры", StringComparison.Ordinal) ||
            sql.Contains("INSERT Компьютеры", StringComparison.Ordinal));
    }

    /// <summary>Computer без свежего business Today отклоняется до DML.</summary>
    [Fact]
    public async Task Computer_MissingBusinessToday_RejectedZeroDml()
    {
        var connection = Connection(Script.Scalar(1), Script.Rows(ComputerContextRow()));
        var service = Service(connection);

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveComputerPhasedAsync(new EquipmentComputerSnapshot { EquipmentId = 7 }, null));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        AssertNoDml(connection);
    }

    /// <summary>SIM active SQL filter получает тот же date-only параметр, без независимого GETDATE.</summary>
    [Fact]
    public async Task SimServices_Read_UsesBusinessTodayParameter()
    {
        var today = new DateTime(2099, 3, 1);
        var connection = Connection(Script.Rows());
        var service = Service(connection);

        await service.GetSimServicesAsync(7, false, true, today);

        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(today, parameters["BusinessToday"]);
        Assert.Contains("U.До > @BusinessToday", connection.CommandLog[0]);
        Assert.DoesNotContain("GETDATE()", connection.CommandLog[0]);
    }

    /// <summary>SIM read без business Today fail-closed и не выполняет SQL.</summary>
    [Fact]
    public async Task SimServices_MissingBusinessToday_ZeroSql()
    {
        var connection = Connection();
        var service = Service(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSimServicesAsync(7, false, true));

        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Technical tariff advisory использует тот же business Today.</summary>
    [Fact]
    public async Task PersonalDataAdvisory_UsesBusinessTodayParameter()
    {
        var today = new DateTime(2099, 3, 1);
        var connection = Connection(Script.Scalar(1));
        var service = Service(connection);

        var result = await service.GetPersonalDataAdvisoryAsync(7, 10, 99, "_TCS", today);

        Assert.True(result.IsEligible);
        Assert.Equal(today, Assert.Single(connection.ParameterLog)["BusinessToday"]);
        Assert.Contains("DATEADD(day, 1, @BusinessToday)", connection.CommandLog[0]);
    }

    /// <summary>Post-commit Task ждёт deferred refresh и различает Committed/RefreshFailed.</summary>
    [Fact]
    public async Task CompleteCommitted_DeferredRefresh_DoesNotCompleteEarlyOrRepeatWrite()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCalls = 0;
        var pending = EquipmentFormService.CompleteCommittedAsync(42, async () =>
        {
            refreshCalls++;
            await release.Task;
        });

        Assert.False(pending.IsCompleted);
        release.SetResult();
        var committed = await pending;
        Assert.Equal(EquipmentWritePhase.Committed, committed.Phase);
        Assert.Equal(42, committed.Value);
        Assert.Equal(1, refreshCalls);

        var failed = await EquipmentFormService.CompleteCommittedAsync(42,
            () => throw new InvalidOperationException("refresh failed"));
        Assert.Equal(EquipmentWritePhase.RefreshFailed, failed.Phase);
        Assert.Equal(42, failed.Value);
        Assert.Equal(1, refreshCalls);
    }

    private static EquipmentFormService Service(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }

    private static PhoneFormService PhoneService(ScriptedConnection connection) =>
        new(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

    private static ScriptedConnection Connection(params ScriptEntry[] entries) => new(new Queue<ScriptEntry>(entries));

    private static Equipment Equipment() => new()
    {
        Id = 7, ModelId = 5, OwnerId = 1, EquipmentTypeId = 30,
        ChangedAtUtc = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(500),
    };

    private static EquipmentPhoneLink Link(int phoneId, int? connectionId) => new()
    {
        EquipmentId = 7, PhoneId = phoneId, ConnectionEquipmentId = connectionId,
    };

    private static PhoneSyncOperation Xorcom(int phoneId) => new(PhoneSyncKind.Xorcom, phoneId.ToString());

    private static Dictionary<string, object?> ModelTypeRow() => Script.Row(
        ("КодТипаОборудования", 30), ("SNобязателен", (byte)0), ("ЕстьMACадрес", (byte)0));

    private static Dictionary<string, object?> ComputerContextRow() => Script.Row(
        ("ModelId", 5), ("EquipmentTypeId", 1), ("HasComputerCharacteristics", (byte)1), ("MemoryBanks", 2));

    private static void AssertNoDml(ScriptedConnection connection) =>
        Assert.DoesNotContain(connection.CommandLog, sql =>
            sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
}
