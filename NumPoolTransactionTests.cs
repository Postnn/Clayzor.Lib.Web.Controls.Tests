using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class NumPoolTransactionTests
{
    [Theory]
    [InlineData("vwПодключенияТелефонииНомераТфОП", 2)]
    [InlineData("vwНомераТфОП_Сотрудники", 3)]
    [InlineData("vwНомераТфОП_Лица", 4)]
    [InlineData("vwНомераТфОП_Телефоны", 5)]
    public async Task FailureInAnyUsageSetLeavesNoCommittedHeaderOrLinks(string failedView, int attempted)
    {
        var connection = new TransactionProbe(failedView);
        var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
        var link = new NumPoolLink(1, "fixture", 0, 4, "actor", DateTime.UnixEpoch);
        var draft = new NumPoolSnapshot(new NumPoolHeader { Number = "00023", CapacityId = 1, Control = 0 },
            [link], [link], [link], [link]);
        var error = await Assert.ThrowsAsync<DataException>(() => NumPoolData.SaveAsync(db, "DOMAIN\\fixture", null, draft, default));
        Assert.Equal("injected child failure", error.Message);
        Assert.Equal(attempted, connection.Writes.Count);
        Assert.Contains("INSERT dbo.vwНомераТфОП ", connection.Writes[0]);
        Assert.True(connection.Transaction!.RolledBack);
        Assert.False(connection.Transaction.Committed);
    }

    [Theory]
    [InlineData("vwПодключенияТелефонииНомераТфОП", 1)]
    [InlineData("vwНомераТфОП_Сотрудники", 2)]
    [InlineData("vwНомераТфОП_Лица", 3)]
    [InlineData("vwНомераТфОП_Телефоны", 4)]
    [InlineData("vwНомераТфОП", 5)]
    public async Task DeleteStopsAtTheFailedViewAndDoesNotCommit(string failedView, int expectedCount)
    {
        var connection = new TransactionProbe(failedView, existing: true);
        var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
        var original = NumPoolSnapshot.Empty with { Header = new NumPoolHeader { Number = "00023", CapacityId = 1 } };
        await Assert.ThrowsAsync<DataException>(() => NumPoolData.DeleteAsync(db, "fixture", original, default));
        Assert.Equal(expectedCount, connection.Writes.Count);
        Assert.StartsWith("DELETE dbo." + failedView + " WHERE", connection.Writes[^1]);
        Assert.True(connection.Transaction!.RolledBack);
        Assert.False(connection.Transaction.Committed);
    }

    [Fact]
    public async Task JournaledDeleteRecoversLostCommitWithoutAnotherWrite()
    {
        using var connection = new TransactionProbe("none", existing: true)
        {
            PhoneLinked = true, LoseConfirmedCommitReply = true,
        };
        using var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
        var original = NumPoolSnapshot.Empty with
        {
            Header = new NumPoolHeader { Number = "00023", CapacityId = 1 },
            Phones = [new(42, "Телефон", 0, 4, null, null)],
        };
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<NumPoolUnknownException>(() => NumPoolData.DeleteJournaledAsync(db, "fixture", 4, original, id, default));
        Assert.False(connection.Existing);
        Assert.Equal(5, connection.Writes.Count);
        Assert.Contains("tel/42", connection.RelatedScopes);
        Assert.Contains(connection.RelatedScopes, value => value.StartsWith("numpool-number/", StringComparison.Ordinal));
        Assert.DoesNotContain("00023", connection.RelatedScopes);
        var receipt = await NumPoolData.ReadWriteReceiptAsync(db, "fixture", 4, "00023", true, id, default);
        Assert.Equal(InventoryOperationState.Confirmed, receipt?.State);
        Assert.Null(await NumPoolData.ReadWriteReceiptAsync(db, "fixture", 4, "23", true, id, default));

        await NumPoolData.DeleteJournaledAsync(db, "fixture", 4, original, id, default);
        Assert.Equal(5, connection.Writes.Count);
    }

    [Fact]
    public async Task JournaledDeleteRechecksSqlGrantBeforeAnyDml()
    {
        using var connection = new TransactionProbe("none", existing: true) { RevokeWriteInsideTransaction = true };
        using var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
        var original = NumPoolSnapshot.Empty with { Header = new NumPoolHeader { Number = "00023", CapacityId = 1 } };
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<NumPoolConflict>(() => NumPoolData.DeleteJournaledAsync(db, "fixture", 4, original, id, default));

        Assert.True(connection.Existing);
        Assert.Empty(connection.Writes);
        var receipt = await NumPoolData.ReadWriteReceiptAsync(db, "fixture", 4, "00023", true, id, default);
        Assert.Equal(InventoryOperationState.Rejected, receipt?.State);
    }

    /// <summary>Не принимает клиентское свидетельство с лишней связью за прежнюю подтверждённую запись.</summary>
    [Fact]
    public async Task LegacyEvidenceWithUnrelatedPhoneCannotMatchConfirmedReceipt()
    {
        using var connection = new TransactionProbe("none", existing: true);
        using var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
        var original = NumPoolSnapshot.Empty with { Header = new NumPoolHeader { Number = "00023", CapacityId = 1 } };
        var id = Guid.NewGuid();
        await NumPoolData.DeleteJournaledAsync(db, "fixture", 4, original, id, default);

        await Assert.ThrowsAsync<InventoryOperationPendingException>(() => NumPoolData.ImportLegacyOperationsAsync(
            db, "fixture", 4, "00023", [(id, "numpool.delete", "00023", new[] { 1 })], default));

        Assert.Equal(5, connection.Writes.Count);
        Assert.DoesNotContain("tel/1", connection.RelatedScopes);
        Assert.Equal(InventoryOperationState.Confirmed,
            (await NumPoolData.ReadWriteReceiptAsync(db, "fixture", 4, "00023", true, id, default))?.State);
    }

    private sealed class TransactionProbe(string failedView, bool existing = false) : DbConnection
    {
        public bool Existing { get; set; } = existing;
        public bool PhoneLinked { get; init; }
        public bool LoseConfirmedCommitReply { get; set; }
        public bool RevokeWriteInsideTransaction { get; init; }
        public JournalRow? StoredAttempt { get; set; }
        public HashSet<string> RelatedScopes { get; } = [];
        public List<string> Writes { get; } = [];
        public TestTransaction? Transaction { get; private set; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return Transaction = new TestTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new ProbeCommand(this, failedView);
    }

    private sealed record JournalRow(Guid Id, int EmployeeId, string Scope, string Kind, InventoryOperationState State);

    private sealed class TestTransaction(TransactionProbe connection) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public bool HasHeader { get; set; } = connection.Existing;
        public JournalRow? StagedAttempt { get; set; }
        public List<string> StagedScopes { get; } = [];
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;
        public override void Commit()
        {
            Committed = true;
            connection.Existing = HasHeader;
            if (StagedAttempt is { } row) connection.StoredAttempt = row;
            connection.RelatedScopes.UnionWith(StagedScopes);
            if (connection.LoseConfirmedCommitReply && StagedAttempt is { State: InventoryOperationState.Confirmed })
            {
                connection.LoseConfirmedCommitReply = false;
                throw new TimeoutException("Потерян ответ COMMIT.");
            }
        }
        public override void Rollback() => RolledBack = true;
        protected override void Dispose(bool disposing) { if (disposing && !Committed) Rollback(); base.Dispose(disposing); }
    }

    private sealed class ProbeCommand(TransactionProbe owner, string failedView) : DbCommand
    {
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        public override void Prepare() { }
        public override void Cancel() { }
        public override object ExecuteScalar()
        {
            if (CommandText == SQLQueries.INSERT_ClayzorOperationScope)
            {
                Assert.IsType<TestTransaction>(DbTransaction).StagedScopes.Add(Parameter<string>("ConflictKey"));
                return 1;
            }
            if (CommandText == SQLQueries.UPDATE_ClayzorOperationScope)
                return owner.RelatedScopes.Count;
            Assert.Equal(SQLQueries.UPDATE_ClayzorOperationJournalState, CommandText);
            var row = owner.StoredAttempt;
            if (row is null || row.Id != Parameter<Guid>("AttemptId") || (byte)row.State != Parameter<byte>("ExpectedState"))
                return 0;
            var next = row with { State = (InventoryOperationState)Parameter<byte>("NextState") };
            if (DbTransaction is TestTransaction tx) tx.StagedAttempt = next;
            else owner.StoredAttempt = next;
            return 1;
        }

        public override int ExecuteNonQuery()
        {
            if (CommandText == SQLQueries.INSERT_ClayzorOperationJournalAttempt)
            {
                Assert.IsType<TestTransaction>(DbTransaction).StagedAttempt = new(
                    Parameter<Guid>("AttemptId"), Parameter<int>("EmployeeId"), Parameter<string>("ConflictKey"),
                    Parameter<string>("CommandKind"), InventoryOperationState.Reserved);
                return 1;
            }
            Assert.NotNull(owner.Transaction);
            Assert.Same(owner.Transaction, DbTransaction);
            owner.Writes.Add(CommandText);
            if (CommandText.StartsWith("INSERT dbo." + failedView + " ", StringComparison.Ordinal) ||
                CommandText.StartsWith("DELETE dbo." + failedView + " WHERE", StringComparison.Ordinal))
                throw new DataException("injected child failure");
            if (CommandText == SQLQueries.DELETE_NumPoolHeader)
                Assert.IsType<TestTransaction>(DbTransaction).HasHeader = false;
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalAttemptForUpdate)
            {
                table.Columns.Add("AttemptId", typeof(Guid));
                table.Columns.Add("EmployeeId", typeof(int));
                table.Columns.Add("ConflictKey", typeof(string));
                table.Columns.Add("CommandKind", typeof(string));
                table.Columns.Add("State", typeof(byte));
                table.Columns.Add("ResultEntityId", typeof(int));
                if (owner.StoredAttempt is { } row && row.Id == Parameter<Guid>("AttemptId"))
                    table.Rows.Add(row.Id, row.EmployeeId, row.Scope, row.Kind, (byte)row.State, DBNull.Value);
            }
            else if (CommandText == SQLQueries.SELECT_ClayzorOperationScope)
            {
                table.Columns.Add("ConflictKey", typeof(string));
                foreach (var scope in owner.RelatedScopes) table.Rows.Add(scope);
            }
            else if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlotForUpdate)
            {
                table.Columns.Add("Pending", typeof(bool));
                table.Rows.Add(false);
            }
            else if (CommandText.Contains("SUSER_SID()"))
            {
                table.Columns.Add("Id", typeof(int));
                table.Rows.Add(4);
            }
            else if (CommandText.Contains("HAS_PERMS_BY_NAME"))
            {
                foreach (var name in new[] { "CanRead", "CanInsert", "CanUpdate", "CanDelete" }) table.Columns.Add(name, typeof(bool));
                var canWrite = !owner.RevokeWriteInsideTransaction || DbTransaction is null;
                table.Rows.Add(true, canWrite, canWrite, canWrite);
            }
            else if (CommandText.Contains("vwНомераТфОП n", StringComparison.Ordinal))
            {
                if (DbTransaction is not null) Assert.Same(owner.Transaction, DbTransaction);
                table.Columns.Add("Number", typeof(string));
                table.Columns.Add("CapacityId", typeof(int));
                var exists = DbTransaction is TestTransaction tx ? tx.HasHeader : owner.Existing;
                if (exists) table.Rows.Add("00023", 1);
                var links = new DataTable();
                links.Columns.Add("Id", typeof(int));
                links.Columns.Add("Name", typeof(string));
                links.Columns.Add("Private", typeof(byte));
                links.Columns.Add("ChangedBy", typeof(int));
                links.Columns.Add("ChangedByName", typeof(string));
                links.Columns.Add("ChangedAt", typeof(DateTime));
                var phones = links.Clone();
                if (exists && owner.PhoneLinked) phones.Rows.Add(42, "Телефон", (byte)0, 4, DBNull.Value, DBNull.Value);
                return new DataTableReader([table, links, links.Clone(), links.Clone(), phones]);
            }
            else if (CommandText.Contains("WHERE Id IN"))
            {
                if (DbTransaction is not null) Assert.Same(owner.Transaction, DbTransaction);
                table.Columns.Add("Id", typeof(int));
                table.Rows.Add(1);
            }
            else throw new InvalidOperationException("Unexpected read: " + CommandText);
            return table.CreateDataReader();
        }

        private T Parameter<T>(string name) => Assert.IsType<T>(Parameters[name].Value);
    }
}
