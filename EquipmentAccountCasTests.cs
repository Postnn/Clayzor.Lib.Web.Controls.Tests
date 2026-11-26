using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки CAS учётных записей оборудования на границе Dapper.</summary>
public sealed class EquipmentAccountCasTests
{
    /// <summary>Редактирование и удаление без исходной версии не отправляют SQL.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOriginalVersion_NeverWrites(bool delete)
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var account = new EquipmentAccount { EquipmentId = 7, Login = "test" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => delete ? account.DeleteAsync(db) : account.UpdateAsync(db));

        Assert.Empty(connection.CommandLog);
    }

    /// <summary>Изменение любого исходного поля и удалённая строка блокируют UPDATE и DELETE.</summary>
    [Theory]
    [InlineData("password", false)]
    [InlineData("rights", false)]
    [InlineData("notes", true)]
    [InlineData("missing", true)]
    public async Task StaleOriginal_RollsBackWithoutSendingWrite(string changedField, bool delete)
    {
        var original = Original();
        var current = Original();
        if (changedField == "password") current.Password += " ";
        if (changedField == "rights") current.LimitedRights = 1;
        if (changedField == "notes") current.Notes = "other editor";
        using var script = Connection(changedField == "missing" ? Script.Rows() : SnapshotRow(current));
        using var connection = new EquipmentAccountTransactionConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var draft = Original();
        draft.ExpectedVersion = original.CaptureVersion();
        draft.Notes = "local input";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            delete ? draft.DeleteAsync(db) : draft.UpdateAsync(db));

        Assert.Contains("Сверьте актуальные данные", ex.Message);
        Assert.Contains("UPDLOCK, HOLDLOCK", Assert.Single(script.CommandLog));
        Assert.Equal(["begin", "rollback"], connection.TransactionLog);
        Assert.Equal("local input", draft.Notes);
    }

    /// <summary>Результат CAS подтверждает только запись одной строки в той же транзакции.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task AffectedRows_ChangesExactlyOneOrRollsBack(bool delete, int affected)
    {
        var original = Original();
        using var script = Connection(SnapshotRow(original), Script.Scalar(affected));
        using var connection = new EquipmentAccountTransactionConnection(script);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var draft = Original();
        draft.ExpectedVersion = original.CaptureVersion();
        Task Save() => delete ? draft.DeleteAsync(db) : draft.UpdateAsync(db);

        if (affected == 1)
            await Save();
        else
            await Assert.ThrowsAsync<InvalidOperationException>(Save);

        Assert.Equal(2, script.CommandLog.Count);
        Assert.Contains("UPDLOCK, HOLDLOCK", script.CommandLog[0]);
        Assert.Contains("SELECT @@ROWCOUNT", script.CommandLog[1]);
        Assert.All(connection.Commands, command => Assert.NotNull(command.Transaction));
        Assert.Equal(new[] { "begin", affected == 1 ? "commit" : "rollback" }, connection.TransactionLog);
        Assert.DoesNotContain(script.ParameterLog[0].Keys, name => name.Contains("Пароль"));
        if (delete)
            Assert.DoesNotContain(script.ParameterLog[1].Keys, name => name.Contains("Пароль"));
    }

    /// <summary>Даже текст ошибки провайдера не передаёт старый или новый пароль в диагностику.</summary>
    [Theory]
    [InlineData(-2, true)]
    [InlineData(50000, false)]
    public async Task SensitiveSqlFailure_IsRedactedAndRollsBack(int number, bool unknown)
    {
        const string fakeOriginal = "fixture-original-password";
        const string fakeProposed = "fixture-proposed-password";
        var original = Original();
        original.Password = fakeOriginal;
        using var script = Connection(SnapshotRow(original),
            Script.Error(SqlExceptionFactory.Create(number, fakeOriginal + " " + fakeProposed)));
        using var connection = new EquipmentAccountTransactionConnection(script);
        var diagnostics = new AccountSqlDiagnostics();
        using var db = new DbManager("Server=fixture", diagnostics, () => connection);
        var draft = Original();
        draft.ExpectedVersion = original.CaptureVersion();
        draft.Password = fakeProposed;

        var error = await Assert.ThrowsAsync<EquipmentAccountStorageException>(() => draft.UpdateAsync(db));

        Assert.Equal(unknown, error.MayHaveWritten);
        Assert.True(error.WasDispatched);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(fakeOriginal, error.ToString());
        Assert.DoesNotContain(fakeProposed, error.ToString());
        Assert.False(diagnostics.Called);
        Assert.Equal(["begin", "rollback"], connection.TransactionLog);
    }

    internal static ScriptEntry SnapshotRow(EquipmentAccount account) => Script.Rows(Script.Row(
        (nameof(EquipmentAccount.EquipmentId), account.EquipmentId),
        (nameof(EquipmentAccount.Login), account.Login),
        (nameof(EquipmentAccount.Password), account.Password),
        (nameof(EquipmentAccount.LimitedRights), account.LimitedRights),
        (nameof(EquipmentAccount.Notes), account.Notes)));

    private static EquipmentAccount Original() => new()
    {
        EquipmentId = 7, Login = "test", Password = "fixture-password", Notes = "fixture notes",
    };

    private static ScriptedConnection Connection(params ScriptEntry[] entries) => new(new Queue<ScriptEntry>(entries));

    private sealed class AccountSqlDiagnostics : ISqlErrorHandler
    {
        internal bool Called { get; private set; }

        public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) => Called = true;
    }
}

internal sealed class EquipmentAccountTransactionConnection(ScriptedConnection script) : DbConnection
{
    internal List<string> TransactionLog { get; } = [];
    internal List<DbCommand> Commands { get; } = [];

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get => script.ConnectionString; set => script.ConnectionString = value ?? ""; }
    public override string Database => script.Database;
    public override string DataSource => script.DataSource;
    public override string ServerVersion => script.ServerVersion;
    public override ConnectionState State => script.State;
    public override void Open() => script.Open();
    public override void Close() => script.Close();
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand()
    {
        var command = script.CreateCommand();
        Commands.Add(command);
        return command;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        TransactionLog.Add("begin");
        return new Transaction(this, isolationLevel);
    }

    private sealed class Transaction(EquipmentAccountTransactionConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        protected override DbConnection DbConnection => connection;
        public override IsolationLevel IsolationLevel => isolationLevel;
        public override void Commit() => connection.TransactionLog.Add("commit");
        public override void Rollback() => connection.TransactionLog.Add("rollback");
    }
}
