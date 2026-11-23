using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки UPS same-row concurrency и applicability EQ8-06A/EQ8-11.</summary>
public sealed class Eq8UpsConcurrencyTests
{
    /// <summary>Test UPDATE сравнивает исходный snapshot и требует ровно одну строку.</summary>
    [Fact]
    public void TestUpdate_UsesOriginalSnapshotAndRowCountGuard()
    {
        var original = new UpsTest
        {
            Id = 5,
            TestedAt = new DateTime(2026, 11, 20, 8, 30, 0),
            BatteryTime = 10,
            Servers = "srv-a",
            Issues = "old",
        };
        var current = new UpsTest
        {
            Id = 5,
            TestedAt = original.TestedAt,
            BatteryTime = 10,
            Servers = "srv-a",
            Issues = "new",
        };

        var command = UpsSaveBuilder.Build(10, 1, [], [], [original], [current]);

        Assert.Equal(original.TestedAt, command.Parameters.Get<DateTime>("@testUpd0OriginalDate"));
        Assert.Equal(10, command.Parameters.Get<int>("@testUpd0OriginalBattery"));
        Assert.Equal("srv-a", command.Parameters.Get<string>("@testUpd0OriginalServers"));
        Assert.Equal("old", command.Parameters.Get<string>("@testUpd0OriginalIssues"));
        Assert.Contains("ДатаТестирования = @testUpd0OriginalDate", command.Sql);
        Assert.Contains("ВремяБатарейUPS = @testUpd0OriginalBattery", command.Sql);
        Assert.Contains("СервераПолучившиеОповещение", command.Sql);
        Assert.Contains("ОшибкиТестирования", command.Sql);
        var update = command.Sql.IndexOf("UPDATE vwПодключенияUPSтестирование", StringComparison.Ordinal);
        var guard = command.Sql.IndexOf("IF @@ROWCOUNT <> 1", update, StringComparison.Ordinal);
        Assert.True(guard > update);
        Assert.True(guard < command.Sql.IndexOf("COMMIT TRANSACTION", update, StringComparison.Ordinal));
    }

    /// <summary>Server flag UPDATE не перезаписывает чужое изменение исходного флага.</summary>
    [Fact]
    public void ServerUpdate_UsesOriginalFlagAndRowCountGuard()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1, EquipmentTypeId = 1, TurnOffConfigured = 0 }],
            [new UpsServerLink { ServerEquipmentId = 1, EquipmentTypeId = 1, TurnOffConfigured = 1 }], [], []);

        Assert.Equal((byte)0, command.Parameters.Get<byte>("@srvUpd0OriginalFlag"));
        Assert.Contains("НастроеноВыключение = @srvUpd0OriginalFlag", command.Sql);
        var update = command.Sql.IndexOf("UPDATE vwПодключенияUPS ", StringComparison.Ordinal);
        Assert.True(command.Sql.IndexOf("IF @@ROWCOUNT <> 1", update, StringComparison.Ordinal) > update);
    }

    /// <summary>Новый noncomputer с признаком выключения отклоняется до DML.</summary>
    [Fact]
    public void AddedNonComputerWithShutdownFlag_ThrowsBeforeDml()
    {
        Assert.Throws<InvalidOperationException>(() => UpsSaveBuilder.Build(10, 1, [],
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 2, TurnOffConfigured = 1 }], [], []));
    }

    /// <summary>Изменение исторического noncomputer flag не маскируется обычным UPDATE.</summary>
    [Fact]
    public void ChangedHistoricalNonComputerShutdownFlag_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 2, TurnOffConfigured = 1 }],
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 2, TurnOffConfigured = 0 }], [], []));
    }

    /// <summary>Неизменное историческое значение сохраняется и не создаёт скрытой очистки.</summary>
    [Fact]
    public void UnchangedHistoricalNonComputerShutdownFlag_IsPreserved()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 2, TurnOffConfigured = 1 }],
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 2, TurnOffConfigured = 1 }], [], []);

        Assert.DoesNotContain("UPDATE vwПодключенияUPS ", command.Sql);
        Assert.DoesNotContain("INSERT vwПодключенияUPS", command.Sql);
    }

    /// <summary>Даже подделанный DTO computer type не обходит проверку фактического типа в БД.</summary>
    [Fact]
    public void AddedComputerDto_RetainsDatabaseApplicabilityGuard()
    {
        var command = UpsSaveBuilder.Build(10, 1, [],
            [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 1, TurnOffConfigured = 1 }], [], []);

        Assert.Contains("@accessComputerType", command.Sql);
        Assert.Contains("T.КодТипаОборудования <> @accessComputerType", command.Sql);
        Assert.Contains("SELECT 4", command.Sql);
        Assert.True(command.Sql.IndexOf("SELECT 4", StringComparison.Ordinal) <
                    command.Sql.IndexOf("INSERT vwПодключенияUPS", StringComparison.Ordinal));
    }

    /// <summary>Статус applicability отличается от отказа в правах.</summary>
    [Fact]
    public async Task SaveDetails_ApplicabilityStatus_IsDomainError()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(4)]));
        var db = new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => UpsServerLink.SaveDetailsAsync(
            db, 10, 1, [], [new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 1, TurnOffConfigured = 1 }], [], []));

        Assert.Contains("только для компьютеров", exception.Message);
    }
}
