using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
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
        var created = Assert.IsType<PhoneCodeEntityRecord>(
            await TerritorialEntityData.SavePhoneAsync(db, "fixture", 4, Guid.NewGuid(), null, draft));
        var updated = Assert.IsType<PhoneCodeEntityRecord>(
            await TerritorialEntityData.SavePhoneAsync(db, "fixture", 4, Guid.NewGuid(), created, created with { Comment = "second" }));

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
            TerritorialEntityData.SavePhoneAsync(db, "fixture", 4, Guid.NewGuid(), original, original with { Comment = "second" }));

        Assert.Equal(0, connection.Commits);
        Assert.Equal(failReadback ? 1 : 0, connection.Rollbacks);
        Assert.Equal(NewRow(), connection.Row);
        Assert.Equal(failReadback ? new[] { "UPDATE" } : Array.Empty<string>(), connection.Writes);
    }

    [Fact]
    public async Task DeleteReferenceFailureDoesNotCommitAndPreservesTheOriginal()
    {
        using var connection = new PhoneCodeConnection { Row = NewRow(), RejectDelete = true };
        using var db = CreateDb(connection);
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            TerritorialEntityData.DeletePhoneAsync(db, "fixture", 4, Guid.NewGuid(), connection.Row!));
        Assert.Equal(547, error.Number);
        Assert.Equal(0, connection.Commits);
        Assert.Equal(1, connection.Rollbacks);
        Assert.Equal(NewRow(), connection.Row);
        Assert.Equal(new[] { "DELETE" }, connection.Writes);
    }

    [Fact]
    public async Task LostCommitProvesExactStringKeyWithoutSecondInsert()
    {
        using var connection = new PhoneCodeConnection { LoseCommit = true };
        using var db = CreateDb(connection);
        var attemptId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerritorialEntityData.SavePhoneAsync(db, "fixture", 4, attemptId, null, NewRow()));

        var receipt = await TerritorialEntityData.ReadPhoneWriteReceiptAsync(db, "fixture", 4,
            "phone-code.insert", attemptId, null, "00127", default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, null), receipt);
        var latest = await TerritorialEntityData.ReadPhoneWriteStateAsync(db, "fixture", 4, null, default);
        Assert.Equal((false, "00127"), latest);
        await TerritorialEntityData.ImportLegacyPhoneOperationsAsync(db, "fixture", 4, null,
            [(attemptId, "phone-code.insert", null, "00127")], default);
        Assert.Empty(connection.LegacyAttempts);
        var replay = await TerritorialEntityData.SavePhoneAsync(db, "fixture", 4, attemptId, null, NewRow());
        Assert.Equal("00127", replay?.Code);
        Assert.Equal(new[] { "INSERT" }, connection.Writes);
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            TerritorialEntityData.ReadPhoneWriteReceiptAsync(db, "fixture", 4,
                "phone-code.insert", attemptId, null, "127", default));
    }

    [Fact]
    public async Task LostDeleteCommitConfirmsTheDeletedCodeAndNeverReissuesDelete()
    {
        var original = NewRow();
        using var connection = new PhoneCodeConnection { LoseCommit = true, Row = original };
        using var db = CreateDb(connection);
        var attemptId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerritorialEntityData.DeletePhoneAsync(db, "fixture", 4, attemptId, original));

        Assert.Null(connection.Row);
        var receipt = await TerritorialEntityData.ReadPhoneWriteReceiptAsync(db, "fixture", 4,
            "phone-code.delete", attemptId, "00127", "00127", default);
        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, null), receipt);
        await TerritorialEntityData.DeletePhoneAsync(db, "fixture", 4, attemptId, original);
        Assert.Equal(new[] { "DELETE" }, connection.Writes);
    }

    [Fact]
    public async Task LegacyCreateBindsExactTargetWithoutClaimingAnOutcome()
    {
        using var connection = new PhoneCodeConnection();
        using var db = CreateDb(connection);
        var id = Guid.NewGuid();
        await TerritorialEntityData.ImportLegacyPhoneOperationsAsync(db, "fixture", 4, null,
            [(id, "phone-code.insert", null, "00127")], default);

        Assert.Equal("phone-code/new", connection.LegacyAttempts[id].Scope);
        Assert.Equal("phone-code/00127", connection.LegacyScopes[id]);
        Assert.Empty(connection.Attempts);
        Assert.Empty(connection.Results);
        Assert.Empty(connection.Writes);
        var state = await TerritorialEntityData.ReadPhoneWriteStateAsync(db, "fixture", 4, "00127", default);
        Assert.True(state.HasPending);
        Assert.Null(state.LastConfirmedCode);
    }

    [Fact]
    public async Task LegacyUpdateCannotImportForAnotherVisibleKey()
    {
        using var connection = new PhoneCodeConnection { Row = NewRow() };
        using var db = CreateDb(connection);
        await Assert.ThrowsAsync<TerritorialEntityRuleException>(() =>
            TerritorialEntityData.ImportLegacyPhoneOperationsAsync(db, "fixture", 4, "00127",
                [(Guid.NewGuid(), "phone-code.update", "127", "127")], default));
        Assert.Empty(connection.LegacyAttempts);
        Assert.Empty(connection.Writes);
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

    private sealed record AttemptRow(int EmployeeId, string Scope, string Kind, InventoryOperationState State);

    private sealed class PhoneCodeConnection : DbConnection
    {
        public PhoneCodeEntityRecord? Row { get; set; }
        public bool FailReadback { get; set; }
        public bool RejectDelete { get; init; }
        public bool LoseCommit { get; init; }
        public List<string> Writes { get; } = [];
        public Dictionary<Guid, AttemptRow> Attempts { get; set; } = [];
        public Dictionary<Guid, string> Results { get; set; } = [];
        public Dictionary<Guid, string> OtherScopes { get; set; } = [];
        public Dictionary<Guid, AttemptRow> LegacyAttempts { get; set; } = [];
        public Dictionary<Guid, string> LegacyScopes { get; set; } = [];
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
        internal Dictionary<Guid, AttemptRow> Attempts { get; } = new(connection.Attempts);
        internal Dictionary<Guid, string> Results { get; } = new(connection.Results);
        internal Dictionary<Guid, string> OtherScopes { get; } = new(connection.OtherScopes);
        internal Dictionary<Guid, AttemptRow> LegacyAttempts { get; } = new(connection.LegacyAttempts);
        internal Dictionary<Guid, string> LegacyScopes { get; } = new(connection.LegacyScopes);
        internal bool BusinessWrite { get; set; }
        private bool _completed;
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection DbConnection => connection;
        public override void Commit()
        {
            Assert.False(_completed);
            connection.Row = Row;
            connection.Attempts = Attempts;
            connection.Results = Results;
            connection.OtherScopes = OtherScopes;
            connection.LegacyAttempts = LegacyAttempts;
            connection.LegacyScopes = LegacyScopes;
            _completed = true;
            if (!BusinessWrite) return;
            connection.Commits++;
            if (connection.LoseCommit) throw new TimeoutException("Потерян ответ COMMIT.");
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
        private Dictionary<Guid, AttemptRow> Attempts => DbTransaction is PhoneCodeTransaction tx ? tx.Attempts : owner.Attempts;
        private Dictionary<Guid, string> Results => DbTransaction is PhoneCodeTransaction tx ? tx.Results : owner.Results;
        private Dictionary<Guid, string> OtherScopes => DbTransaction is PhoneCodeTransaction tx ? tx.OtherScopes : owner.OtherScopes;

        public override object ExecuteScalar()
        {
            var id = Value<Guid>("AttemptId");
            if (CommandText == SQLQueries.INSERT_ClayzorOperationScope)
                OtherScopes.Add(id, Value<string>("ConflictKey"));
            else if (CommandText == SQLQueries.INSERT_ClayzorLegacyOperationScope)
                Assert.IsType<PhoneCodeTransaction>(DbTransaction).LegacyScopes.Add(id, Value<string>("ConflictKey"));
            else if (CommandText == SQLQueries.INSERT_ClayzorOperationTextReceipt)
            {
                Assert.True(Assert.IsType<PhoneCodeTransaction>(DbTransaction).BusinessWrite);
                Assert.DoesNotContain(Parameters.Cast<DbParameter>(), item => item.ParameterName == "Comment");
                Results.Add(id, Value<string>("ResultKey"));
            }
            else if (CommandText == SQLQueries.UPDATE_ClayzorOperationJournalState)
            {
                var state = (InventoryOperationState)Value<byte>("NextState");
                if (state == InventoryOperationState.Confirmed)
                {
                    Assert.True(Assert.IsType<PhoneCodeTransaction>(DbTransaction).BusinessWrite);
                    Assert.True(Results.ContainsKey(id));
                }
                Attempts[id] = Attempts[id] with { State = state };
            }
            else if (CommandText == SQLQueries.UPDATE_ClayzorOperationScope)
            {
                Assert.True(OtherScopes.ContainsKey(id));
            }
            else
                throw new NotSupportedException(CommandText);
            return 1;
        }

        public override int ExecuteNonQuery()
        {
            var transaction = Assert.IsType<PhoneCodeTransaction>(DbTransaction);
            if (CommandText == SQLQueries.INSERT_ClayzorOperationJournalAttempt)
            {
                Attempts.Add(Value<Guid>("AttemptId"), new AttemptRow(Value<int>("EmployeeId"),
                    Value<string>("ConflictKey"), Value<string>("CommandKind"), InventoryOperationState.Reserved));
                return 1;
            }
            var kind = CommandText[..CommandText.IndexOf(' ')];
            transaction.BusinessWrite = true;
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
            if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalAttemptForUpdate)
            {
                var id = Value<Guid>("AttemptId");
                return Attempts.TryGetValue(id, out var attempt) ? AttemptReader(id, attempt) : EmptyRow();
            }
            if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalLatest)
            {
                var latest = Attempts.LastOrDefault(item => item.Value.EmployeeId == Value<int>("EmployeeId") &&
                    item.Value.Scope == Value<string>("ConflictKey"));
                return latest.Value is not null ? AttemptReader(latest.Key, latest.Value) : EmptyRow();
            }
            if (CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlot || CommandText == SQLQueries.SELECT_ClayzorOperationJournalOpenSlotForUpdate)
                return Values(("Pending", Attempts.Any(row => row.Value.State == InventoryOperationState.Reserved &&
                    (row.Value.Scope == Value<string>("ConflictKey") || OtherScopes.GetValueOrDefault(row.Key) == Value<string>("ConflictKey"))) ||
                    owner.LegacyAttempts.Any(row => row.Value.Scope == Value<string>("ConflictKey") ||
                        owner.LegacyScopes.GetValueOrDefault(row.Key) == Value<string>("ConflictKey"))));
            if (CommandText == SQLQueries.INSERT_ClayzorLegacyOperationEvidence)
            {
                var tx = Assert.IsType<PhoneCodeTransaction>(DbTransaction);
                tx.LegacyAttempts.Add(Value<Guid>("AttemptId"), new AttemptRow(Value<int>("EmployeeId"),
                    Value<string>("ConflictKey"), Value<string>("CommandKind"), InventoryOperationState.Reserved));
                return Values(("Owned", true));
            }
            if (CommandText == SQLQueries.SELECT_ClayzorOperationScope)
                return OtherScopes.TryGetValue(Value<Guid>("AttemptId"), out var scope) ? Values(("ConflictKey", scope)) : EmptyRow();
            if (CommandText == SQLQueries.SELECT_ClayzorOperationTextReceipt)
                return Results.TryGetValue(Value<Guid>("AttemptId"), out var key) ? Values(("ResultKey", key)) : EmptyRow();
            var transaction = DbTransaction as PhoneCodeTransaction;
            if (CommandText.Contains("SELECT COUNT(*)")) return Values(("Value", 1));
            Assert.Contains("FROM dbo.ТелефонныеКоды", CommandText);
            Assert.Equal("00127", Key());
            var read = transaction is null ? owner.Row : owner.FailReadback && transaction.BusinessWrite &&
                !CommandText.Contains("UPDLOCK") ? null : transaction.Row;
            return read is null ? EmptyRow() : Values(("Code", read.Code), ("TerritoryId", read.TerritoryId), ("Length", read.Length),
                ("TypeId", read.TypeId), ("Comment", read.Comment), ("ChangedBy", read.ChangedBy),
                ("ChangedByName", read.ChangedByName ?? ""), ("ChangedAt", read.ChangedAt));
        }

        private DbParameter Parameter(string key) => Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == key);
        private T Value<T>(string key) => (T)Parameter(key).Value!;

        private static DbDataReader AttemptReader(Guid id, AttemptRow row)
        {
            var table = new DataTable();
            table.Columns.Add("AttemptId", typeof(Guid));
            table.Columns.Add("EmployeeId", typeof(int));
            table.Columns.Add("ConflictKey", typeof(string));
            table.Columns.Add("CommandKind", typeof(string));
            table.Columns.Add("State", typeof(byte));
            table.Columns.Add("ResultEntityId", typeof(int));
            table.Rows.Add(id, row.EmployeeId, row.Scope, row.Kind, (byte)row.State, DBNull.Value);
            return table.CreateDataReader();
        }

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
