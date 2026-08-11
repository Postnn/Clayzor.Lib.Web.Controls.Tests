using System.Data;
using System.Data.Common;
using System.Reflection;
using Clayzor.Lib.DALC;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Behavioral tests через CTFR3.3/3.4 connection factory seam.
/// Fake DbConnection/DbCommand контролирует execution без реального SQL Server.
/// Gate-release tests используют ОДИН DbManager instance.
/// </summary>
public class DbManagerSeamTests
{
    private sealed class CountingHandler : ISqlErrorHandler
    {
        public int CallCount { get; private set; }
        public void HandleSqlError(SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters) => CallCount++;
    }

    private sealed class FakeConnection : DbConnection
    {
        private readonly Queue<FakeCommand> _commands;
        private ConnectionState _state;

        public FakeConnection(Queue<FakeCommand> commands) { _commands = commands; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken ct) { Open(); return Task.CompletedTask; }
        public override void Close() => _state = ConnectionState.Closed;
        protected override DbCommand CreateDbCommand() => _commands.Dequeue();
        protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotSupportedException();
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _state = ConnectionState.Closed; }
    }

    private sealed class FakeCommand : DbCommand
    {
        private readonly int _affectedRows;
        private readonly Exception? _exception;
        public FakeCommand(int affectedRows = 0, Exception? exception = null)
        { _affectedRows = affectedRows; _exception = exception; }

        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => new FakeParameterCollection();
        public override bool DesignTimeVisible { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => _exception is not null ? throw _exception : _affectedRows;
        public override object? ExecuteScalar() => _exception is not null ? throw _exception : _affectedRows;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken ct)
        { await Task.Yield(); if (_exception is not null) throw _exception; return _affectedRows; }

        public override async Task<object?> ExecuteScalarAsync(CancellationToken ct)
        { await Task.Yield(); if (_exception is not null) throw _exception; return _affectedRows; }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _list = [];
        public override int Count => _list.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _list.Add((DbParameter)value); return _list.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v); }
        public override void Clear() => _list.Clear();
        public override bool Contains(object value) => _list.Contains((DbParameter)value);
        public override bool Contains(string value) => _list.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => _list.CopyTo((DbParameter[])array, index);
        public override IEnumerator<DbParameter> GetEnumerator() => _list.GetEnumerator();
        public override int IndexOf(object value) => _list.IndexOf((DbParameter)value);
        public override int IndexOf(string n) => _list.FindIndex(p => p.ParameterName == n);
        public override void Insert(int index, object value) => _list.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _list.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _list.RemoveAt(index);
        public override void RemoveAt(string n) => _list.RemoveAll(p => p.ParameterName == n);
        protected override DbParameter GetParameter(int index) => _list[index];
        protected override DbParameter GetParameter(string n) => _list.First(p => p.ParameterName == n);
        protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
        protected override void SetParameter(string n, DbParameter value)
        { var idx = IndexOf(n); if (idx >= 0) _list[idx] = value; else Add(value); }
    }

    private sealed class FakeParameter : DbParameter
    {
        public override string ParameterName { get; set; } = "";
        public override object? Value { get; set; }
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override int Size { get; set; }
        public override string SourceColumn { get; set; } = "";
        public override bool SourceColumnNullMapping { get; set; }
        public override DataRowVersion SourceVersion { get; set; }
        public override void ResetDbType() { }
    }

    private static DbManager CreateDb(
        CountingHandler handler, Func<DbConnection> factory)
        => new("Server=test", handler, factory);

    // ═══ Zero affected rows ═══

    [Fact]
    public async Task ExecuteAsync_ZeroAffectedRows_ReturnsZeroWithoutError()
    {
        var handler = new CountingHandler();
        var db = CreateDb(handler,
            () => new FakeConnection(new Queue<FakeCommand>([new(affectedRows: 0)])));

        var result = await db.ExecuteAsync("DELETE FROM t", new { id = 1 }, CommandType.Text);

        Assert.Equal(0, result);
        Assert.Equal(0, handler.CallCount);
    }

    // ═══ Ordinary non-connectivity SqlException + gate release (SAME instance) ═══

    [Fact]
    public async Task ExecuteAsync_NonConnectivitySqlException_ThrowsAndReleasesGate()
    {
        var handler = new CountingHandler();
        var ex = SqlExceptionFactory.Create(number: 547, message: "CHECK constraint violation");
        Assert.False(DbManager.IsConnectivityError(ex));

        // Queue: 1-й вызов → throw, 2-й → успех
        var queue = new Queue<FakeCommand>([
            new(exception: ex),
            new(affectedRows: 5)
        ]);
        var db = CreateDb(handler, () => new FakeConnection(queue));

        // 1-я операция — бросает
        var thrown = await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecuteAsync("DELETE FROM t", new { id = 1 }, CommandType.Text));
        Assert.Equal(547, thrown.Number);
        Assert.Equal(1, handler.CallCount);

        // 2-я операция — тот же db, gate освобождён
        var result = await db.ExecuteAsync("DELETE FROM t");
        Assert.Equal(5, result);
        Assert.Equal(1, handler.CallCount); // handler не вызван повторно
    }

    // ═══ Cancellation + gate release (SAME instance) ═══

    [Fact]
    public async Task RunAsync_Cancellation_PropagatesAndReleasesGate()
    {
        var handler = new CountingHandler();
        // Queue: после cancellation ExecuteAsync должен выполниться
        var queue = new Queue<FakeCommand>([new(affectedRows: 42)]);
        var db = CreateDb(handler, () => new FakeConnection(queue));

        // Cancellation через RunDbAsync (internal seam на том же gate)
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.RunDbAsync<int>(_ => throw new OperationCanceledException()));

        Assert.Equal(0, handler.CallCount);

        // Тот же db — gate освобождён, операция успешна
        var result = await db.ExecuteAsync("DELETE FROM t");
        Assert.Equal(42, result);
        Assert.Equal(0, handler.CallCount);
    }

    // ═══ Reentrant handler — доказывает handler outside gate (CTFR3.5) ═══

    private sealed class ReentrantHandler : ISqlErrorHandler
    {
        public DbManager Db { get; set; } = null!;
        public bool ReentrantSucceeded { get; private set; }

        public void HandleSqlError(SqlException exception, string connectionString,
            string commandText, IReadOnlyList<(string Name, object? Value)> parameters)
        {
            // Handler должен мочь войти в тот же _gate → доказывает release до handler
            var task = Db.RunDbAsync<int>(_ => Task.FromResult(123));
            ReentrantSucceeded = task.Wait(TimeSpan.FromSeconds(2)) && task.Result == 123;
        }
    }

    [Fact]
    public async Task ConnectivityHandler_ExecutesOutsideGate_ReentrantSucceeds()
    {
        var ex = SqlExceptionFactory.Create(number: 53, message: "connection error");
        Assert.True(DbManager.IsConnectivityError(ex));

        // Queue: 1 → connectivity throw, 2 → success (для reentrant probe)
        var queue = new Queue<FakeCommand>([
            new(exception: ex),
            new(affectedRows: 123)
        ]);

        var handler = new ReentrantHandler();
        var db = new DbManager("Server=test", handler,
            () => new FakeConnection(queue));
        handler.Db = db;

        await Assert.ThrowsAsync<SqlException>(() =>
            db.ExecuteAsync("DELETE FROM t", new { id = 1 }, CommandType.Text));

        Assert.True(handler.ReentrantSucceeded,
            "Handler must be able to enter the same gate — proves handler executes AFTER gate release");
    }

    // ═══ API compatibility (CTFR3.4) ═══

    [Fact]
    public void Connection_PublicContract_IsSqlConnection()
    {
        var prop = typeof(DbManager).GetProperty(nameof(DbManager.Connection))!;
        Assert.Equal(typeof(SqlConnection), prop.PropertyType);
    }

    [Fact]
    public void RunAsync_PublicContract_AcceptsSqlConnectionLambda()
    {
        // Proof: компиляция этого метода подтверждает сигнатуру
        static Task<int> UseLegacyApi(DbManager db)
            => db.RunAsync<int>((SqlConnection c) => Task.FromResult(1));
    }
}
