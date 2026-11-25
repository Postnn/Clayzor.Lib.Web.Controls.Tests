using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.TerritorialEntity;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class PhoneCodeWriteContractProposalTests
{
    [Fact]
    public async Task InsertReadbackThenUpdatePreservesStringKeyAndDoesNotInsertAgain()
    {
        using var connection = new PhoneCodeConnection();
        using var db = CreateDb(connection);
        var draft = NewRow();
        var created = await TerritorialEntityData.SavePhoneAsync(db, "fixture", null, draft);
        var updated = await TerritorialEntityData.SavePhoneAsync(db, "fixture", created, created with { Comment = "second" });

        Assert.Equal("00127", created.Code);
        Assert.Equal("00127", updated.Code);
        Assert.Equal("second", updated.Comment);
        Assert.Equal(2, connection.Commits);
        Assert.Equal(new[] { "INSERT", "UPDATE" }, connection.Writes);
        Assert.Equal(updated, connection.Row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentOriginalOrMissingReadbackLeavesNoCommittedWrite(bool failReadback)
    {
        using var connection = new PhoneCodeConnection();
        using var db = CreateDb(connection);
        connection.Row = NewRow();
        connection.FailReadback = failReadback;
        var original = failReadback ? connection.Row : connection.Row with { ChangedBy = 99 };
        await Assert.ThrowsAsync<TerritorialEntityRuleException>(() =>
            TerritorialEntityData.SavePhoneAsync(db, "fixture", original, original with { Comment = "second" }));

        Assert.Equal(0, connection.Commits);
        Assert.Equal(1, connection.Rollbacks);
        Assert.Equal(NewRow(), connection.Row);
        Assert.Equal(failReadback ? new[] { "UPDATE" } : Array.Empty<string>(), connection.Writes);
    }

    [Fact]
    public async Task DeleteReferenceFailureDoesNotCommitAndPreservesTheOriginal()
    {
        using var connection = new PhoneCodeConnection { Row = NewRow(), RejectDelete = true };
        using var db = CreateDb(connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => TerritorialEntityData.DeletePhoneAsync(db, "fixture", connection.Row!));
        Assert.Equal(547, error.Number);
        Assert.Equal(0, connection.Commits);
        Assert.Equal(1, connection.Rollbacks);
        Assert.Equal(NewRow(), connection.Row);
        Assert.Equal(new[] { "DELETE" }, connection.Writes);
    }

    private static DbManager CreateDb(PhoneCodeConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);
    }

    private static PhoneCodeEntityRecord NewRow() => new()
    {
        Code = "00127", TerritoryId = 2, Length = 3, TypeId = 0, Comment = "first",
        ChangedBy = 4, ChangedByName = "actor", ChangedAt = DateTime.UnixEpoch,
    };

    private sealed class PhoneCodeConnection : DbConnection
    {
        public PhoneCodeEntityRecord? Row { get; set; }
        public bool FailReadback { get; set; }
        public bool RejectDelete { get; init; }
        public List<string> Writes { get; } = [];
        public int Commits { get; set; }
        public int Rollbacks { get; set; }

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => new PhoneCodeTransaction(this, level);
        protected override DbCommand CreateDbCommand() => new PhoneCodeCommand(this);
    }

    private sealed class PhoneCodeTransaction(PhoneCodeConnection connection, IsolationLevel level) : DbTransaction
    {
        internal PhoneCodeEntityRecord? Row { get; set; } = connection.Row;
        private bool _completed;
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection DbConnection => connection;
        public override void Commit()
        {
            Assert.False(_completed);
            connection.Row = Row;
            connection.Commits++;
            _completed = true;
        }
        public override void Rollback()
        {
            if (!_completed) connection.Rollbacks++;
            _completed = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Rollback();
            base.Dispose(disposing);
        }
    }

    private sealed class PhoneCodeCommand(PhoneCodeConnection owner) : DbCommand
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
            var transaction = Assert.IsType<PhoneCodeTransaction>(DbTransaction);
            var kind = CommandText[..CommandText.IndexOf(' ')];
            owner.Writes.Add(kind);
            var key = Key();
            if (kind == "DELETE")
            {
                Assert.Equal(transaction.Row?.Code, key);
                if (owner.RejectDelete) throw SqlExceptionFactory.Create(547, "fixture foreign key");
                transaction.Row = null;
                return 1;
            }

            Assert.Contains(kind, new[] { "INSERT", "UPDATE" });
            Assert.Equal(kind == "INSERT" ? null : key, transaction.Row?.Code);
            transaction.Row = new PhoneCodeEntityRecord
            {
                Code = key,
                TerritoryId = (int)Parameter("TerritoryId").Value!,
                Length = (byte)Parameter("Length").Value!,
                TypeId = (int)Parameter("TypeId").Value!,
                Comment = (string)Parameter("Comment").Value!,
                ChangedBy = 4, ChangedByName = "actor", ChangedAt = DateTime.UnixEpoch.AddSeconds(owner.Writes.Count)
            };
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("HAS_PERMS_BY_NAME"))
                return Values(("TerritoryUpdate", false), ("PhoneInsert", true), ("PhoneUpdate", true), ("PhoneDelete", true));
            if (CommandText.Contains("fn_ТекущиеРоли"))
                return Values(("RoleId", (int)Роли.Администратор_тарифов_связи), ("PersonId", 0));
            if (CommandText.Contains("SUSER_SID()"))
                return Values(("КодСотрудника", 4), ("Сотрудник", "actor"));
            var transaction = Assert.IsType<PhoneCodeTransaction>(DbTransaction);
            if (CommandText.Contains("SELECT COUNT(*)")) return Values(("Value", 1));
            Assert.Contains("FROM dbo.ТелефонныеКоды", CommandText);
            Assert.Equal("00127", Key());
            var read = owner.FailReadback && !CommandText.Contains("UPDLOCK") ? null : transaction.Row;
            return read is null ? EmptyRow() : Values(("Code", read.Code), ("TerritoryId", read.TerritoryId), ("Length", read.Length),
                ("TypeId", read.TypeId), ("Comment", read.Comment), ("ChangedBy", read.ChangedBy),
                ("ChangedByName", read.ChangedByName ?? ""), ("ChangedAt", read.ChangedAt));
        }

        private DbParameter Parameter(string key) => Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == key);

        private string Key()
        {
            var param = Parameter("Code");
            Assert.Equal(DbType.AnsiString, param.DbType);
            Assert.Equal(20, param.Size);
            return Assert.IsType<string>(param.Value);
        }

        private static DbDataReader Values(params (string Name, object Value)[] values)
        {
            var result = new DataTable();
            foreach (var (name, value) in values) result.Columns.Add(name, value.GetType());
            result.Rows.Add(values.Select(pair => pair.Value).ToArray());
            return result.CreateDataReader();
        }

        private static DbDataReader EmptyRow()
        {
            var result = new DataTable();
            result.Columns.Add("Code", typeof(string));
            return result.CreateDataReader();
        }
    }
}
