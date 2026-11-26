using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки фактических параметров записи лицензии на границе Dapper и DbCommand.</summary>
public sealed class EquipmentLicenseAuditParameterTests
{
    /// <summary>Новая лицензия не имеет исходной версии, даже если в модели заполнено время аудита.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertBindsNullExpectedAuditTimestamp(bool hasModelTimestamp)
    {
        var license = new EquipmentLicense { EquipmentId = 7, ExpectedExisting = false };
        if (hasModelTimestamp)
            license.ChangedAt = new DateTime(2026, 11, 26, 1, 2, 3, DateTimeKind.Utc);

        var parameters = await ReadBoundParametersAsync(license);

        Assert.Equal(0, Assert.IsType<int>(parameters["ОжидаетсяСуществующий"]));
        Assert.Same(DBNull.Value, parameters["ИсходноеИзменено"]);
    }

    /// <summary>Для существующей лицензии сравнение получает исходный UTC timestamp с точностью до тика.</summary>
    [Fact]
    public async Task UpdateBindsExactOriginalAuditTimestamp()
    {
        var timestamp = new DateTime(2026, 11, 26, 8, 9, 10, DateTimeKind.Unspecified).AddTicks(1234567);
        var license = new EquipmentLicense { EquipmentId = 7, ExpectedExisting = true, ChangedAt = timestamp };

        var parameters = await ReadBoundParametersAsync(license);

        Assert.Equal(1, Assert.IsType<int>(parameters["ОжидаетсяСуществующий"]));
        var bound = Assert.IsType<DateTime>(parameters["ИсходноеИзменено"]);
        Assert.Equal(timestamp.Ticks, bound.Ticks);
        Assert.Equal(timestamp.Kind, bound.Kind);
    }

    private static async Task<Dictionary<string, object?>> ReadBoundParametersAsync(EquipmentLicense license)
    {
        using var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.NonQuery()]));
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        await license.SaveAsync(db);
        Assert.Contains("Изменено = @ИсходноеИзменено", Assert.Single(connection.CommandLog));
        return Assert.Single(connection.ParameterLog);
    }
}
