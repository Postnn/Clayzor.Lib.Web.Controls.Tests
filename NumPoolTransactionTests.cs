using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
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

    private sealed class TransactionProbe(string failedView, bool existing = false) : DbConnection
    {
        public bool Existing => existing;
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

    private sealed class TestTransaction(DbConnection connection) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Committed = true;
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
        public override object ExecuteScalar() => throw new NotSupportedException();

        public override int ExecuteNonQuery()
        {
            Assert.NotNull(owner.Transaction);
            Assert.Same(owner.Transaction, DbTransaction);
            owner.Writes.Add(CommandText);
            if (CommandText.StartsWith("INSERT dbo." + failedView + " ", StringComparison.Ordinal) ||
                CommandText.StartsWith("DELETE dbo." + failedView + " WHERE", StringComparison.Ordinal))
                throw new DataException("injected child failure");
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            if (CommandText.Contains("SUSER_SID()"))
            {
                table.Columns.Add("Id", typeof(int));
                table.Rows.Add(4);
            }
            else if (CommandText.Contains("HAS_PERMS_BY_NAME"))
            {
                foreach (var name in new[] { "CanRead", "CanInsert", "CanUpdate", "CanDelete" }) table.Columns.Add(name, typeof(bool));
                table.Rows.Add(true, true, true, true);
            }
            else if (CommandText.Contains("UPDLOCK"))
            {
                Assert.Same(owner.Transaction, DbTransaction);
                table.Columns.Add("Number", typeof(string));
                table.Columns.Add("CapacityId", typeof(int));
                if (owner.Existing) table.Rows.Add("00023", 1);
                var links = new DataTable();
                links.Columns.Add("Id", typeof(int));
                links.Columns.Add("Name", typeof(string));
                links.Columns.Add("Private", typeof(byte));
                links.Columns.Add("ChangedBy", typeof(int));
                links.Columns.Add("ChangedByName", typeof(string));
                links.Columns.Add("ChangedAt", typeof(DateTime));
                return new DataTableReader([table, links, links.Clone(), links.Clone(), links.Clone()]);
            }
            else if (CommandText.Contains("WHERE Id IN"))
            {
                Assert.Same(owner.Transaction, DbTransaction);
                table.Columns.Add("Id", typeof(int));
                table.Rows.Add(1);
            }
            else throw new InvalidOperationException("Unexpected read: " + CommandText);
            return table.CreateDataReader();
        }
    }
}
