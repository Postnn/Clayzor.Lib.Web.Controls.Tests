using Clayzor.Lib.DALC;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>
/// Unit-тесты для классификатора <see cref="DbManager.IsConnectivityError"/>.
/// Проверяет, что коды connectivity-ошибок (сервер недоступен, сеть, таймаут)
/// классифицируются как connectivity, а обычные ошибки SQL (синтаксис, constraint) — нет.
/// </summary>
public class IsConnectivityErrorTests
{
    /// <summary>Коды ошибок, которые ДОЛЖНЫ классифицироваться как connectivity.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    [InlineData(53)]
    [InlineData(121)]
    [InlineData(233)]
    [InlineData(258)]
    [InlineData(4060)]
    [InlineData(11001)]
    [InlineData(1231)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void ConnectivityCodes_ReturnTrue(int errorNumber)
    {
        Assert.True(DbManager.IsConnectivityErrorCode(errorNumber),
            $"Код {errorNumber} должен классифицироваться как connectivity-ошибка");
    }

    /// <summary>Коды обычных ошибок SQL — НЕ должны классифицироваться как connectivity.</summary>
    [Theory]
    [InlineData(2627)]  // Violation of UNIQUE KEY constraint
    [InlineData(547)]   // INSERT/UPDATE conflicted with FOREIGN KEY constraint
    [InlineData(8152)]  // String or binary data would be truncated
    [InlineData(515)]   // Cannot insert NULL
    [InlineData(208)]   // Invalid object name
    [InlineData(8134)]  // Divide by zero error
    [InlineData(0)]     // Unknown / no error number
    public void NonConnectivityCodes_ReturnFalse(int errorNumber)
    {
        Assert.False(DbManager.IsConnectivityErrorCode(errorNumber),
            $"Код {errorNumber} НЕ должен классифицироваться как connectivity-ошибка");
    }
}
