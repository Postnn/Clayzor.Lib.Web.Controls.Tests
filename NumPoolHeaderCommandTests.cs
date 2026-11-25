using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class NumPoolHeaderCommandTests
{
    private static NumPoolHeader Header => new() { Number = "00023", CapacityId = 2, Control = 0 };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RelationOnlyEditDoesNotIssueHeaderUpdate(string? text)
    {
        var db = new ScriptedConnection(new Queue<ScriptEntry>());
        var original = Header with { IncomingFor = text };
        var saved = await NumPoolData.SaveHeaderAsync(db, new TestTransaction(db), original, original with { }, default);
        Assert.Empty(db.CommandLog);
        Assert.Equal(text, saved.IncomingFor);
    }

    [Fact]
    public async Task ExistingUpdatePreservesOtherNullableAndEmptyFields()
    {
        var db = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        var draft = Header with { IncomingTo = "", Notes = "changed" };
        await NumPoolData.SaveHeaderAsync(db, new TestTransaction(db), Header, draft, default);
        Assert.StartsWith("UPDATE", Assert.Single(db.CommandLog));
        var parameters = Assert.Single(db.ParameterLog);
        Assert.Equal(DBNull.Value, parameters["IncomingFor"]);
        Assert.Equal("", parameters["IncomingTo"]);
        Assert.Equal("changed", parameters["Notes"]);
    }

    [Fact]
    public async Task InsertDefaultsOnlyNullTextAndKeepsStringKey()
    {
        var db = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        await NumPoolData.SaveHeaderAsync(db, new TestTransaction(db), null, Header with { Notes = "" }, default);
        Assert.StartsWith("INSERT", Assert.Single(db.CommandLog));
        var parameters = Assert.Single(db.ParameterLog);
        Assert.Equal(" ", parameters["IncomingFor"]);
        Assert.Equal(" ", parameters["IncomingTo"]);
        Assert.Equal("", parameters["Notes"]);
        Assert.Equal("00023", parameters["Id"]);
    }

    private sealed class TestTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
