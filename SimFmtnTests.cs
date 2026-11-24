using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class SimFmtnTests
{
    [Theory]
    [InlineData("", "Необходимо указать префикс FMTN!")]
    [InlineData("0", "Необходимо указать префикс FMTN!")]
    [InlineData("001x", "Префикс FMTN может содержать только цифры, круглые скобки и знак подчеркивания")]
    public void ValidatesLegacyPrefix(string prefix, string error) => Assert.Equal(error, Assert.Single(new SimFmtnFields(prefix, "", "").Validate()));

    [Theory]
    [InlineData("001(2)_", "")]
    [InlineData("000", "٥١٠١")]
    [InlineData("00123456789", "00123456789012345678")]
    public void PreservesPrefixesAndLegacyShortNumbers(string prefix, string shortNumber)
    {
        var fields = new SimFmtnFields(prefix, shortNumber, new string('Я', 50));
        Assert.Empty(fields.Validate());
        Assert.Equal(prefix, fields.Prefix);
        Assert.Equal(shortNumber, fields.ShortNumber);
    }

    [Fact]
    public void RejectsOverlongValuesAndInvalidShortNumber()
    {
        Assert.Equal(3, new SimFmtnFields(new string('1', 12), new string('2', 21), new string('3', 51)).Validate().Count);
        Assert.Equal("Короткий номер может содержать только цифры", Assert.Single(new SimFmtnFields("001", "1 2", "").Validate()));
    }

    [Theory]
    [InlineData("00123456789", "5101", true)]
    [InlineData("00123456789", "5001", false)]
    [InlineData("00123456789", "٥١٠١", false)]
    [InlineData("001(2)_", "5101", false)]
    public void DistinguishesInputFormatFromVerifiedWritePolicy(string prefix, string shortNumber, bool allowed) =>
        Assert.Equal(allowed, new SimFmtnFields(prefix, shortNumber, "").FitsWritePolicy);
}
