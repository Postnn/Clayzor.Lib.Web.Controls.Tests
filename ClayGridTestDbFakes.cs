using System.Collections;
using System.Data;
using System.Data.Common;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Fake-инфраструктура БД для bUnit-тестов ClayGrid (CGFR1 §34).
/// Позволяет скриптовать ответы на SQL-запросы без реального SQL Server.
/// Использует internal seam <c>DbManager(string, ISqlErrorHandler?, Func&lt;DbConnection&gt;)</c>.
/// </summary>

/// <summary>
/// DataReader, оборачивающий <see cref="List{Dictionary{string, object?}}"/> для Dapper.
/// </summary>
internal sealed class ScriptedDataReader : DbDataReader
{
    private readonly List<Dictionary<string, object?>> _rows;
    private int _index = -1;

    public ScriptedDataReader(List<Dictionary<string, object?>> rows) => _rows = rows;

    public override int FieldCount => _rows.Count > 0 ? _rows[0].Count : 0;
    public override int RecordsAffected => -1;
    public override bool HasRows => _rows.Count > 0;
    public override bool IsClosed => false;
    public override int Depth => 0;

    public override bool Read() => ++_index < _rows.Count;
    public override bool NextResult() => false;

    public override string GetName(int i) => _rows[0].Keys.ElementAt(i);
    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < _rows[0].Count; i++)
            if (string.Equals(_rows[0].Keys.ElementAt(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public override object GetValue(int i) => _rows[_index].Values.ElementAt(i) ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (int j = 0; j < count; j++) values[j] = GetValue(j) ?? DBNull.Value;
        return count;
    }

    public override bool IsDBNull(int i) => GetValue(i) is DBNull;

    public override Type GetFieldType(int i) => typeof(object);

    // Неиспользуемые Dapper mandatory overrides
    public override bool GetBoolean(int i) => throw new NotSupportedException();
    public override byte GetByte(int i) => throw new NotSupportedException();
    public override long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
    public override char GetChar(int i) => throw new NotSupportedException();
    public override long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
    public override string GetDataTypeName(int i) => "nvarchar";
    public override DateTime GetDateTime(int i) => throw new NotSupportedException();
    public override decimal GetDecimal(int i) => throw new NotSupportedException();
    public override double GetDouble(int i) => throw new NotSupportedException();
    public override float GetFloat(int i) => throw new NotSupportedException();
    public override Guid GetGuid(int i) => throw new NotSupportedException();
    public override short GetInt16(int i) => throw new NotSupportedException();
    public override int GetInt32(int i) => Convert.ToInt32(GetValue(i));
    public override long GetInt64(int i) => Convert.ToInt64(GetValue(i));
    public override string GetString(int i) => GetValue(i).ToString() ?? "";
    public override object this[int i] => GetValue(i);
    public override object? this[string name] => GetValue(GetOrdinal(name));
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

/// <summary>
/// Одна запись скрипта для <see cref="ScriptedCommand"/>:
/// Rows/Reader (список словарей) для ExecuteDbDataReaderAsync,
/// Scalar (int) для ExecuteScalarAsync,
/// AffectedRows для ExecuteNonQueryAsync.
/// </summary>
internal sealed class ScriptEntry
{
    public List<Dictionary<string, object?>>? Rows { get; init; }
    public int? Scalar { get; init; }
    public int? AffectedRows { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// DbCommand, возвращающий скриптованные данные. Выполняет один <see cref="ScriptEntry"/>.
/// </summary>
internal sealed class ScriptedCommand : DbCommand
{
    private readonly ScriptEntry _entry;
    public List<string> CommandLog { get; }

    public ScriptedCommand(ScriptEntry entry, List<string> commandLog)
    {
        _entry = entry;
        CommandLog = commandLog;
    }

    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbParameterCollection DbParameterCollection => new FakeParameterCollection();
    public override bool DesignTimeVisible { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();

    public override int ExecuteNonQuery()
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return _entry.AffectedRows ?? 1;
    }

    public override object? ExecuteScalar()
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return (object?)_entry.Scalar;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return new ScriptedDataReader(_entry.Rows ?? []);
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken ct)
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return Task.FromResult(_entry.AffectedRows ?? 1);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken ct)
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return Task.FromResult((object?)_entry.Scalar);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
    {
        CommandLog.Add(CommandText);
        if (_entry.Exception is not null) throw _entry.Exception;
        return Task.FromResult<DbDataReader>(new ScriptedDataReader(_entry.Rows ?? []));
    }
}

/// <summary>
/// DbConnection, создающая <see cref="ScriptedCommand"/> из очереди <see cref="ScriptEntry"/>.
/// </summary>
internal sealed class ScriptedConnection : DbConnection
{
    private readonly Queue<ScriptEntry> _entries;
    private ConnectionState _state;
    public List<string> CommandLog { get; } = [];

    public ScriptedConnection(Queue<ScriptEntry> entries) => _entries = entries;

    public override string ConnectionString { get; set; } = "";
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void Open() => _state = ConnectionState.Open;
    public override Task OpenAsync(CancellationToken ct) { Open(); return Task.CompletedTask; }
    public override void Close() => _state = ConnectionState.Closed;
    protected override DbCommand CreateDbCommand() => new ScriptedCommand(_entries.Dequeue(), CommandLog);
    protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotSupportedException();
    public override void ChangeDatabase(string name) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _state = ConnectionState.Closed; }
}

/// <summary>
/// Реюз существующих <c>FakeParameterCollection</c> и <c>FakeParameter</c> из <c>DbManagerSeamTests</c>.
/// </summary>
internal sealed class FakeParameterCollection : DbParameterCollection
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

internal sealed class FakeParameter : DbParameter
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

/// <summary>
/// Fake <see cref="ISqlErrorHandler"/> для тестов — подавляет ошибки.
/// </summary>
internal sealed class FakeSqlErrorHandler : Clayzor.Lib.DALC.ISqlErrorHandler
{
    public void HandleSqlError(Microsoft.Data.SqlClient.SqlException exception, string connectionString,
        string commandText, IReadOnlyList<(string Name, object? Value)> parameters) { }
}

/// <summary>
/// Helper для создания script entry с reader-данными.
/// </summary>
internal static class Script
{
    public static ScriptEntry Rows(params Dictionary<string, object?>[] rows) => new() { Rows = rows.ToList() };
    public static ScriptEntry Scalar(int value) => new() { Scalar = value };
    public static ScriptEntry NonQuery(int affected = 1) => new() { AffectedRows = affected };
    public static ScriptEntry Error(Exception ex) => new() { Exception = ex };

    /// <summary>Строит строку-словарь для удобства.</summary>
    public static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }
}
