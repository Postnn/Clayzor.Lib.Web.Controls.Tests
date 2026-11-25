using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class NumPoolTests
{
    [Theory]
    [InlineData("00018", true)]
    [InlineData("0000", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("012345678901234567890", false)]
    public void UsesTheFullStringNumber(string key, bool expected) => Assert.Equal(expected, NumPoolData.ValidNumber(key));

    [Theory]
    [InlineData(0, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    [InlineData(-1, false)]
    public void ValidatesTheActualTinyintRange(int control, bool expected) =>
        Assert.Equal(expected, NumPoolData.Validate(NumPoolSnapshot.Empty.Header with { Number = "001", CapacityId = 9, Control = control }).Count == 0);

    [Fact]
    public void RejectsFractionalControlMissingCapacityAndOverlongText()
    {
        var record = new NumPoolHeader { Number = "001", Control = 0.5m, IncomingTo = new string('a', 51) };
        Assert.Equal(3, NumPoolData.Validate(record).Count);
    }

    [Fact]
    public void ChangedNamesAndAuditDoNotCauseDeleteAndReinsert()
    {
        var original = Link(10, 1, "Original");
        var renamed = original with { Name = "Renamed", ChangedByName = "Another display name" };
        var (removed, added) = NumPoolData.Diff([original], [renamed]);
        Assert.Empty(removed);
        Assert.Empty(added);
        Assert.True(NumPoolData.SameValues(NumPoolSnapshot.Empty with { Employees = [original] }, NumPoolSnapshot.Empty with { Employees = [renamed] }));
    }

    [Fact]
    public void ChangingPrivateTouchesOnlyThatEmployee()
    {
        var first = Link(10, 0, "First");
        var second = Link(11, 1, "Second");
        var (removed, added) = NumPoolData.Diff([first, second], [first with { Private = 1 }, second]);
        Assert.Equal(10, Assert.Single(removed));
        Assert.Equal(first with { Private = 1 }, Assert.Single(added));
    }

    [Fact]
    public void AnotherAddedLinkMakesTheOriginalAggregateStale()
    {
        var original = NumPoolSnapshot.Empty with { Customers = [Link(10, 0, "First")] };
        var concurrent = original with { Customers = [.. original.Customers, Link(11, 0, "New")] };
        Assert.False(NumPoolData.SameVersion(original, concurrent));
    }

    [Fact]
    public void UsesOneStringKeyAndParameterizesEveryLink()
    {
        var links = new[] { Link(10, 1, "First"), Link(11, 0, "Second") };
        var (sql, parameters) = NumPoolData.BuildLinkInsert("000018", NumPoolKind.Employee, links);
        Assert.Contains("(НомерТфОП,КодСотрудника,Private) VALUES (@Id,@Link0,@Private0),(@Id,@Link1,@Private1)", sql);
        Assert.DoesNotContain("000018", sql);
        Assert.Equal(11, parameters.Get<int>("Link1"));
        Assert.Equal((byte)1, parameters.Get<byte>("Private0"));
        Assert.Equal((byte)0, parameters.Get<byte>("Private1"));
    }

    [Fact]
    public async Task DirectWriteCannotProceedWithoutATrustedIdentity()
    {
        var valid = NumPoolSnapshot.Empty with { Header = new NumPoolHeader { Number = "001", CapacityId = 9 } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NumPoolData.SaveAsync(null!, null, null, valid, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NumPoolData.DeleteAsync(null!, null, valid, CancellationToken.None));
    }

    private static NumPoolLink Link(int id, byte value, string name) => new(id, name, value, 42, "Author", DateTime.UnixEpoch);
}
