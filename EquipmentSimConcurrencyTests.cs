using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки границы Add/Edit и конкурентных изменений защищённой SIM.</summary>
public sealed class EquipmentSimConcurrencyTests
{
    /// <summary>Пропавшая или изменённая SIM блокирует запись до DML.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("contract")]
    [InlineData("person")]
    [InlineData("pin")]
    [InlineData("puk")]
    [InlineData("audit")]
    public async Task EditCannotWriteWhenProtectedOriginalHasChanged(string changed)
    {
        var original = Source();
        var current = Source();
        if (changed == "contract") current.ContractId++;
        if (changed == "person") current.PersonalDataPersonId = 9;
        if (changed == "pin") current.Secrets.Pin = "9876";
        if (changed == "puk") current.Secrets.Puk = "11223344";
        if (changed == "audit") current.ChangedAt = current.ChangedAt.AddMilliseconds(10);

        using var connection = Connection(changed == "missing" ? Script.Rows() : SnapshotRow(current));
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var proposed = Source();
        proposed.ContractId = 11;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            proposed.SaveAsync(db, EquipmentSimWriteMode.Edit, original.CaptureVersion(), 8));

        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("SELECT", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.DoesNotContain("INSERT", sql);
        Assert.DoesNotContain(connection.ParameterLog.Single().Keys, key => key.Contains("PIN") || key.Contains("PUK"));
    }

    /// <summary>Даже после успешного защищённого чтения сохранение требует точного сравнения в DML.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task EditSql_UsesOriginalAndRollsBackOnConflict(int result)
    {
        var original = Source();
        using var connection = Connection(SnapshotRow(original), Script.Scalar(result));
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        Task Save() => Source().SaveAsync(db, EquipmentSimWriteMode.Edit, original.CaptureVersion(), 8);
        if (result == 1)
            await Save();
        else
            Assert.Contains("Сверьте актуальные данные", (await Assert.ThrowsAsync<InvalidOperationException>(Save)).Message);
        var sql = connection.CommandLog[1];
        Assert.Contains("UPDATE vwОборудование", sql);
        Assert.Contains("UPDATE vwSIMКарты", sql);
        Assert.DoesNotContain("INSERT vwSIMКарты", sql);
        Assert.Contains("Изменено = @ИсходноеИзменено", sql);
        Assert.Contains("КодДоговора = @ИсходныйДоговор", sql);
        Assert.Contains("КодЛицаПерсональныхДанных = @ИсходноеЛицоПДн", sql);
        Assert.Contains("@ИсходныйPIN", sql);
        Assert.Contains("@ИсходныйPUK2", sql);
        Assert.Contains("ROLLBACK TRANSACTION; SELECT 0; RETURN;", sql);
        Assert.Contains("ROLLBACK TRANSACTION; SELECT -1; RETURN;", sql);
        Assert.Contains("IF @@ROWCOUNT <> 1", sql);
        Assert.True(sql.IndexOf("UPDATE vwОборудование", StringComparison.Ordinal) <
            sql.IndexOf("COMMIT TRANSACTION", StringComparison.Ordinal));
    }

    /// <summary>Add, Edit и Delete проходят через разные SQL ветки.</summary>
    [Fact]
    public async Task AddDoesNotUpdateAndDeleteKeepsTheExpectedVersion()
    {
        var row = Source();
        using var connection = Connection(Script.Scalar(1), SnapshotRow(row), Script.Scalar(1));
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);

        await row.SaveAsync(db, EquipmentSimWriteMode.Add, null);
        await EquipmentSimCard.DeleteAsync(db, row.EquipmentId, row.CaptureVersion());

        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", connection.CommandLog[0]);
        Assert.Contains("INSERT vwSIMКарты", connection.CommandLog[0]);
        Assert.DoesNotContain("UPDATE vwSIMКарты", connection.CommandLog[0]);
        Assert.Contains("DELETE vwSIMКарты", connection.CommandLog[2]);
        Assert.Contains("@ИсходноеИзменено", connection.CommandLog[2]);
    }

    /// <summary>SQL ошибка с кодами в тексте не попадает наружу и сохраняет фазу.</summary>
    [Theory]
    [InlineData(-2, EquipmentWritePhase.Rejected)]
    [InlineData(50000, EquipmentWritePhase.Rejected)]
    public async Task WriteFailure_DoesNotSendProtectedParametersToDiagnostics(int number, EquipmentWritePhase phase)
    {
        const string sensitiveFixtureText = "fixture-sensitive-code";
        var row = Source();
        using var connection = Connection(
            Script.Scalar(1), Script.Rows(Script.Row((nameof(EquipmentType.HasSimCharacteristics), (byte)1))),
            SnapshotRow(row), Script.Error(SqlExceptionFactory.Create(number, sensitiveFixtureText)));
        var diagnostics = new SimDiagnostics();
        using var db = new DbManager("Server=fixture", diagnostics, () => connection);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());

        var failure = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            service.SaveSimAsync(row, EquipmentSimWriteMode.Edit, row.CaptureVersion(), 8));

        Assert.Equal(phase, failure.Phase);
        Assert.DoesNotContain(sensitiveFixtureText, failure.ToString());
        Assert.False(diagnostics.Called);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", connection.CommandLog[^1]);
    }

    internal static EquipmentSimCard Source(int? personId = null) => new()
    {
        EquipmentId = 7, PhoneNumber = "79000000001", ActivatedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0),
        ContractId = 10, PersonalDataPersonId = personId, ChangedById = 17,
        ChangedAt = new DateTime(2026, 11, 18, 8, 0, 0).AddMilliseconds(500),
        Secrets = new EquipmentSimSecrets { Pin = "1234", Pin2 = "12345", Puk = "12345678", Puk2 = "87654321" },
    };

    internal static ScriptEntry SnapshotRow(EquipmentSimCard row) => Script.Rows(Script.Row(
        ("EquipmentId", row.EquipmentId), ("PhoneNumber", row.PhoneNumber), ("ActivatedAtUtc", row.ActivatedAtUtc),
        ("ContractId", row.ContractId), ("PersonalDataPersonId", row.PersonalDataPersonId),
        ("ChangedById", row.ChangedById), ("ChangedAt", row.ChangedAt),
        ("Pin", row.Secrets.Pin), ("Pin2", row.Secrets.Pin2), ("Puk", row.Secrets.Puk), ("Puk2", row.Secrets.Puk2)));

    private static ScriptedConnection Connection(params ScriptEntry[] entries) => new(new Queue<ScriptEntry>(entries));

    private sealed class SimDiagnostics : ISqlErrorHandler
    {
        internal bool Called { get; private set; }

        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) => Called = true;
    }
}
