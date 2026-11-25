using Clayzor.Lib.Entities.TerritorialEntity;
namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritoryReparentTests
{
    private static readonly TerritorialEntityData.MoveNode Source = new(7, 3, 10, 21);

    [Theory]
    [InlineData(7, 10, 21, false)]
    [InlineData(8, 12, 15, false)]
    [InlineData(3, 1, 40, false)]
    [InlineData(12, 22, 29, true)]
    [InlineData(0, 22, 29, false)]
    public void ValidatesDestinationBeforeWrite(int target, int left, int right, bool expected) =>
        Assert.Equal(expected, TerritorialEntityData.CanReparent(Source, new(target, null, left, right)));

    [Fact]
    public void RootIsNullAndMovingAnExistingRootToRootIsANoOp()
    {
        Assert.True(TerritorialEntityData.CanReparent(Source, null));
        Assert.False(TerritorialEntityData.CanReparent(Source with { ParentId = null }, null));
    }
    [Theory]
    [InlineData(229, true)]
    [InlineData(547, true)]
    [InlineData(1205, true)]
    [InlineData(3609, true)]
    [InlineData(50000, true)]
    [InlineData(-2, false)]
    [InlineData(10054, false)]
    [InlineData(0, false)]
    public void DistinguishesServerRejectionFromUncertainTransport(int number, bool rejected) =>
        Assert.Equal(rejected, TerritorialEntityData.IsKnownMoveRejection(number));
}
