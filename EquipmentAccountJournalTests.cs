using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;
using static Clayzor.Lib.Web.Controls.Tests.EquipmentAccountCasTests;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет привязку серверного исхода и защиту реквизитов учётной записи.</summary>
public sealed class EquipmentAccountJournalTests
{
    private const int EquipmentId = 7;
    private const int EmployeeId = 4;
    private const string Login = "fixture-private-login";
    private const string Password = "fixture-private-password";
    private const string Notes = "fixture-private-notes";

    [Fact]
    public async Task LostInsertCommitReadsExactReceiptAndWillNotInsertAgain()
    {
        var account = Account();
        using var fixture = Create([.. Access(), .. Access(), Script.Rows(), Script.Scalar(1), SnapshotRow(account),
            .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        var nonce = Nonce();
        var binding = EquipmentAccount.AttemptBinding(EquipmentId, Login, nonce);
        fixture.Journal.LoseNextConfirmedCommitReply = true;

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddAccountAsync(account, EmployeeId, attemptId, nonce, default));
        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        Assert.Equal(InventoryOperationState.Confirmed, fixture.Journal.StateFor(attemptId));

        var receipt = await fixture.Service.ReadAccountWriteReceiptAsync(EquipmentId, EquipmentFormService.InsertAccountCommand,
            binding, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);
        await fixture.Service.AddAccountAsync(account, EmployeeId, attemptId, nonce, default);
        var sql = Assert.Single(fixture.Script.CommandLog, IsAccountWrite);
        Assert.StartsWith(SQLQueries.INSERT_ОборудованиеУчётныеЗаписи, sql);
        Assert.Contains("SELECT @@ROWCOUNT", sql);
        AssertProtectedBinding(fixture, attemptId, binding);
    }

    [Fact]
    public async Task ExactReceiptCannotBeReadThroughAnotherLoginBinding()
    {
        var account = Account();
        using var fixture = Create([.. Access(), .. Access(), Script.Rows(), Script.Scalar(1), SnapshotRow(account),
            .. Access(), .. Access()]);
        var attemptId = Guid.NewGuid();
        var nonce = Nonce();
        await fixture.Service.AddAccountAsync(account, EmployeeId, attemptId, nonce, default);
        var incorrect = EquipmentAccount.AttemptBinding(EquipmentId, "fixture-unrelated-login", nonce);
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            fixture.Service.ReadAccountWriteReceiptAsync(EquipmentId, EquipmentFormService.InsertAccountCommand,
                incorrect, EmployeeId, attemptId, default));

        var original = EquipmentAccount.AttemptBinding(EquipmentId, Login, nonce);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId),
            await fixture.Service.ReadAccountWriteReceiptAsync(EquipmentId, EquipmentFormService.InsertAccountCommand,
                original, EmployeeId, attemptId, default));
        Assert.Single(fixture.Script.CommandLog, IsAccountWrite);
    }

    [Fact]
    public async Task StaleAccountVersionRejectsBeforeUpdate()
    {
        var submitted = Account(password: "fixture-proposed-password");
        submitted.ExpectedVersion = Account().CaptureVersion();
        using var fixture = Create([.. Access(), .. Access(), SnapshotRow(Account(password: "fixture-changed-password"))]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.UpdateAccountAsync(submitted, EmployeeId, attemptId, Nonce(), default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.Single(fixture.Script.CommandLog, sql => sql.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsAccountWrite);
    }

    [Fact]
    public async Task UpdateConfirmsOnlyAfterReadingTheSubmittedRow()
    {
        var original = Account();
        var submitted = Account(password: "fixture-new-password");
        submitted.ExpectedVersion = original.CaptureVersion();
        using var fixture = Create([.. Access(), .. Access(), SnapshotRow(original), Script.Scalar(1),
            SnapshotRow(submitted), .. Access()]);
        var nonce = Nonce();
        var attemptId = Guid.NewGuid();

        await fixture.Service.UpdateAccountAsync(submitted, EmployeeId, attemptId, nonce, default);

        var binding = EquipmentAccount.AttemptBinding(EquipmentId, Login, nonce);
        var receipt = await fixture.Service.ReadAccountWriteReceiptAsync(EquipmentId, EquipmentFormService.UpdateAccountCommand,
            binding, EmployeeId, attemptId, default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId), receipt);
        Assert.Equal(SQLQueries.UPDATE_ОборудованиеУчётныеЗаписи, Assert.Single(fixture.Script.CommandLog, IsAccountWrite));
        AssertProtectedBinding(fixture, attemptId, binding);
    }

    [Fact]
    public async Task DuplicateAccountIsRejectedBeforeInsert()
    {
        using var fixture = Create([.. Access(), .. Access(), SnapshotRow(Account())]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddAccountAsync(Account(), EmployeeId, attemptId, Nonce(), default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsAccountWrite);
    }

    [Fact]
    public async Task DeleteConfirmsOnlyAfterExactKeyIsAbsent()
    {
        var account = Account();
        account.ExpectedVersion = account.CaptureVersion();
        using var fixture = Create([.. Access(), .. Access(), SnapshotRow(account), Script.Scalar(1), Script.Rows(), .. Access()]);
        var attemptId = Guid.NewGuid();
        var nonce = Nonce();

        await fixture.Service.DeleteAccountAsync(account, EmployeeId, attemptId, nonce, default);

        var binding = EquipmentAccount.AttemptBinding(EquipmentId, Login, nonce);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, EquipmentId),
            await fixture.Service.ReadAccountWriteReceiptAsync(EquipmentId, EquipmentFormService.DeleteAccountCommand,
                binding, EmployeeId, attemptId, default));
        Assert.Equal(SQLQueries.DELETE_ОборудованиеУчётныеЗаписи, Assert.Single(fixture.Script.CommandLog, IsAccountWrite));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RoleOrActualTypeRevokedInTransactionPreventsDml(bool globalEngineer, bool macType)
    {
        using var fixture = Create([.. Access(), .. Access(globalEngineer, macType)]);
        var attemptId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddAccountAsync(Account(), EmployeeId, attemptId, Nonce(), default));

        Assert.Equal(EquipmentWritePhase.Rejected, error.Phase);
        Assert.Equal(InventoryOperationState.Rejected, fixture.Journal.StateFor(attemptId));
        Assert.DoesNotContain(fixture.Script.CommandLog, IsAccountWrite);
    }

    [Fact]
    public async Task ProviderErrorDoesNotExposePasswordsInDiagnosticsOrExceptions()
    {
        using var fixture = Create([.. Access(), .. Access(), Script.Rows(),
            Script.Error(SqlExceptionFactory.Create(-2, $"Тестовая ошибка SQL: {Login} {Password} {Notes}"))]);
        var attemptId = Guid.NewGuid();
        var nonce = Nonce();

        var error = await Assert.ThrowsAsync<EquipmentOperationException>(() =>
            fixture.Service.AddAccountAsync(Account(), EmployeeId, attemptId, nonce, default));

        Assert.Equal(EquipmentWritePhase.Unknown, error.Phase);
        var diagnostics = string.Join("\n", fixture.Errors.Entries.Prepend(error.ToString()));
        Assert.DoesNotContain(Login, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(Notes, diagnostics, StringComparison.Ordinal);
        Assert.Single(fixture.Script.CommandLog, IsAccountWrite);
        AssertProtectedBinding(fixture, attemptId, EquipmentAccount.AttemptBinding(EquipmentId, Login, nonce));
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("dispose")]
    public async Task ProtectedJournalRedactsErrorsBeforeTheSqlHandlerAcrossTheWholeTransaction(string phase)
    {
        var raw = SqlExceptionFactory.Create(-2, $"Ошибка на границе транзакции {Password}");
        var script = new Queue<ScriptEntry>([Script.Rows(), Script.Rows(Script.Row(("Value", false))), Script.NonQuery()]);
        if (phase != "rollback")
            script.Enqueue(phase == "transition" ? Script.Error(raw) : Script.Scalar(1));
        using var connection = new FaultConnection(new ScriptedConnection(script, emulateEquipmentIdentity: false), phase, raw);
        var errors = new RecordingErrors();
        using var db = new DbManager("Server=fixture", errors, () => connection);
        var attempt = InventoryOperationJournal.ForEmployee(new Employee { Id = EmployeeId }, Guid.NewGuid(),
            "equipment/7", EquipmentFormService.InsertAccountCommand);

        var error = await Assert.ThrowsAsync<EquipmentAccountStorageException>(() =>
            InventoryOperationJournal.RunProtectedSqlAsync<EquipmentAccountWriteRejectedException>(db, attempt,
                (_, _, _) => phase == "rollback"
                    ? Task.FromException<int?>(new EquipmentAccountWriteRejectedException("Изменились исходные данные."))
                    : Task.FromResult<int?>(EquipmentId),
                static () => new EquipmentAccountStorageException(true, true), default));

        Assert.True(error.MayHaveWritten);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.Entries);
        Assert.Equal(2, connection.TransactionCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rollback")]
    [InlineData("settlement")]
    public async Task ProtectedPreparationNeedsRollbackAndSettlementBeforeReportingNotSent(string? failure)
    {
        var raw = SqlExceptionFactory.Create(-2, $"Ошибка проверки {Password}");
        var freeScope = Script.Rows(Script.Row(("Value", false)));
        var script = new Queue<ScriptEntry>([Script.Rows(), freeScope, freeScope, Script.NonQuery(), Script.Scalar(1)]);
        if (failure != "rollback")
        {
            script.Enqueue(Script.Scalar(1));
            script.Enqueue(Script.Scalar(failure == "settlement" ? 0 : 1));
        }
        using var connection = new FaultConnection(new ScriptedConnection(script, emulateEquipmentIdentity: false), failure ?? "", raw);
        var errors = new RecordingErrors();
        using var db = new DbManager("Server=fixture", errors, () => connection);
        var attempt = InventoryOperationJournal.ForEmployee(new Employee { Id = EmployeeId }, Guid.NewGuid(),
            "equipment/7", EquipmentFormService.InsertAccountCommand, ["equipment/account-attempt/fixture"]);
        var dispatched = false;

        var exception = await Record.ExceptionAsync(() =>
            InventoryOperationJournal.RunProtectedSqlAsync<EquipmentAccountWriteRejectedException>(db, attempt,
                (_, _, _) =>
                {
                    dispatched = true;
                    return Task.FromResult<int?>(EquipmentId);
                }, static () => new EquipmentAccountStorageException(true, true), default,
                prepare: (_, _, _) => Task.FromException(raw)));

        if (failure is null) Assert.IsType<InventoryOperationPreparationException>(exception);
        else Assert.IsNotType<InventoryOperationPreparationException>(Assert.IsAssignableFrom<InvalidOperationException>(exception));
        Assert.False(dispatched);
        Assert.Equal(failure == "rollback" ? 2 : 3, connection.TransactionCount);
        Assert.Empty(script);
        Assert.DoesNotContain(Password, exception!.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.Entries);
    }

    private static void AssertProtectedBinding(Fixture fixture, Guid attemptId, string binding)
    {
        var scope = Assert.Single(fixture.Journal.ScopesFor(attemptId));
        Assert.EndsWith(binding, scope, StringComparison.Ordinal);
        Assert.DoesNotContain(Login, scope, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, scope, StringComparison.Ordinal);
        Assert.DoesNotContain(Notes, scope, StringComparison.Ordinal);
    }

    private static bool IsAccountWrite(string sql) =>
        sql.StartsWith(SQLQueries.INSERT_ОборудованиеУчётныеЗаписи, StringComparison.Ordinal) ||
        sql == SQLQueries.UPDATE_ОборудованиеУчётныеЗаписи || sql == SQLQueries.DELETE_ОборудованиеУчётныеЗаписи;

    private static byte[] Nonce() => Enumerable.Repeat((byte)0x5b, 32).ToArray();

    private static EquipmentAccount Account(string password = Password) => new()
    {
        EquipmentId = EquipmentId, Login = Login, Password = password, LimitedRights = 1, Notes = Notes,
    };

    private static ScriptEntry[] Access(bool globalEngineer = true, bool macType = true) =>
    [
        Script.Rows(Script.Row(("КодСотрудника", EmployeeId))),
        Script.Rows(Script.Row(("RoleId", (int)Роли.Дежурный_инженер), ("PersonId", globalEngineer ? 0 : 17)),
            Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        Script.Rows(Script.Row(("ЕстьMACадрес", (byte)(macType ? 1 : 0)))),
    ];

    private static Fixture Create(ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        var script = new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
        var journal = new NetFormTests.NetJournalConnection(script);
        var errors = new RecordingErrors();
        var db = new DbManager("Server=fixture", errors, () => journal);
        return new Fixture(new EquipmentFormService(db, new TestAuthenticationStateProvider()), script, journal, errors, db);
    }

    private sealed class RecordingErrors : ISqlErrorHandler
    {
        internal List<string> Entries { get; } = [];

        public void HandleSqlError(SqlException exception, string connectionString, string commandText,
            IReadOnlyList<(string Name, object? Value)> parameters) =>
            Entries.Add(exception.ToString() + commandText + string.Join(";", parameters.Select(parameter => parameter.Value)));
    }

    private sealed class FaultConnection(ScriptedConnection script, string phase, SqlException raw) : DbConnection
    {
        internal int TransactionCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => script.ConnectionString; set => script.ConnectionString = value ?? ""; }
        public override string Database => script.Database;
        public override string DataSource => script.DataSource;
        public override string ServerVersion => script.ServerVersion;
        public override ConnectionState State => script.State;
        public override void Open() => script.Open();
        public override void Close() => script.Close();
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => script.CreateCommand();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new FaultTransaction(this, isolationLevel, ++TransactionCount == 2 ? phase : null, raw);

        private sealed class FaultTransaction(FaultConnection connection, IsolationLevel isolation, string? phase,
            SqlException raw) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => isolation;
            protected override DbConnection DbConnection => connection;
            public override void Commit() { if (phase == "commit") throw raw; }
            public override void Rollback() { if (phase == "rollback") throw raw; }

            protected override void Dispose(bool disposing)
            {
                if (disposing && phase == "dispose") throw raw;
                base.Dispose(disposing);
            }
        }
    }

    private sealed record Fixture(EquipmentFormService Service, ScriptedConnection Script,
        NetFormTests.NetJournalConnection Journal, RecordingErrors Errors, DbManager Db) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }
}
