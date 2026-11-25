using System.Data;
using System.Data.Common;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Phones;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class NumPoolLookupBoundaryTests
{
    [Theory]
    [InlineData(NumPoolKind.Connection, 100, 100, false)]
    [InlineData(NumPoolKind.Connection, 100, 101, true)]
    [InlineData(NumPoolKind.Customer, 100, 100, false)]
    [InlineData(NumPoolKind.Customer, 100, 101, true)]
    [InlineData(NumPoolKind.Employee, 8, 9, true)]
    [InlineData(NumPoolKind.Phone, 8, 8, false)]
    public async Task ReturnsVisibleRowsAndDetectsAnExtraMatch(NumPoolKind kind, int limit, int matches, bool more)
    {
        var connection = new OptionConnection(matches);
        var source = new NumPoolSelectDataSource(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), kind);
        var result = await source.SearchAsync(new ClaySelectSearchRequest { SearchText = "Item", Limit = limit });
        Assert.Equal(limit + 1, connection.RequestedLimit);
        Assert.Equal(limit, result.Items.Count());
        Assert.Equal(more, result.HasMore);
    }
    private sealed class OptionConnection(int matches) : DbConnection
    {
        public int RequestedLimit { get; set; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new OptionCommand(this, matches);
    }

    private sealed class OptionCommand(OptionConnection owner, int matches) : DbCommand
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
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            owner.RequestedLimit = Convert.ToInt32(Parameters["Limit"].Value);
            var table = new DataTable();
            table.Columns.Add("Id", typeof(int));
            table.Columns.Add("Name", typeof(string));
            for (var i = 1; i <= Math.Min(matches, owner.RequestedLimit); i++) table.Rows.Add(i, $"Item {i}");
            return table.CreateDataReader();
        }
    }
}
