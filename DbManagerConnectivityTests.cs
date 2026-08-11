using Clayzor.Lib.DALC;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Тесты классификации connectivity-ошибок <see cref="DbManager"/> (CTFR3).
/// </summary>
public class DbManagerConnectivityTests
{
    /// <summary>
    /// Известные connectivity-коды классифицируются верно.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    [InlineData(53)]
    [InlineData(64)]
    [InlineData(121)]
    [InlineData(233)]
    [InlineData(258)]
    [InlineData(4060)]
    [InlineData(11001)]
    [InlineData(1231)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void IsConnectivityErrorCode_KnownCodes_ReturnsTrue(int code)
    {
        Assert.True(DbManager.IsConnectivityErrorCode(code));
    }

    /// <summary>
    /// Не-connectivity коды SQL Server не классифицируются как connectivity.
    /// </summary>
    [Theory]
    [InlineData(547)]   // CHECK constraint violation
    [InlineData(2627)]  // PRIMARY KEY / UNIQUE violation
    [InlineData(8152)]  // string or binary data would be truncated
    [InlineData(515)]   // cannot insert NULL
    [InlineData(208)]   // invalid object name
    public void IsConnectivityErrorCode_NonConnectivityCode_ReturnsFalse(int code)
    {
        Assert.False(DbManager.IsConnectivityErrorCode(code));
    }

    /// <summary>
    /// Неизвестный код не считается connectivity.
    /// </summary>
    [Fact]
    public void IsConnectivityErrorCode_UnknownCode_ReturnsFalse()
    {
        Assert.False(DbManager.IsConnectivityErrorCode(99999));
    }
}
