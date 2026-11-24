using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class LocationFactTests
{
    [Theory]
    [InlineData(300, 500, false, true)]
    [InlineData(301, 5, false, false)]
    [InlineData(300, 501, false, false)]
    [InlineData(300, 300, true, true)]
    [InlineData(300, 301, true, false)]
    public void ValidatesTheActualColumnLimits(int name, int notes, bool move, bool accepted) =>
        Assert.Equal(accepted, LocationFactData.Validate(new string('М', name), new string('А', notes), false, move) is null);

    [Theory]
    [InlineData("123456789 ", false)]
    [InlineData("1234567890", true)]
    public void RequiresAnExplanationForBrokenEquipment(string notes, bool accepted) =>
        Assert.Equal(accepted, LocationFactData.Validate("Model", notes, true, false) is null);

    [Fact]
    public void PreservesBrokenWithoutNotesAndComparesTheDayInUtc()
    {
        var checkedAt = new DateTime(2026, 11, 25, 23, 59, 0, DateTimeKind.Utc);
        var row = new LocationFactRow(1, "Model", checkedAt, 42, "Place", true, "");
        Assert.Equal("Сломано, ремонту не подлежит. ", row.DisplayNotes);
        Assert.True(row.CheckedOn(checkedAt));
        Assert.False(row.CheckedOn(checkedAt.AddMinutes(2)));
        Assert.False((row with { CheckedAt = null }).CheckedOn(checkedAt));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(10, false)]
    [InlineData(20, true)]
    public void RequestsAnExplicitMoveOnlyWhenTheBookedPlaceDiffers(int? location, bool different)
    {
        var equipment = new LocationFactEquipment(1, 2, "Owner", "Model", "", location, "Booked", null, DateTime.UnixEpoch);
        Assert.Equal(different, new LocationFactSnapshot(new(10, "Target", false), equipment, null, false, "").DifferentLocation);
    }
}
