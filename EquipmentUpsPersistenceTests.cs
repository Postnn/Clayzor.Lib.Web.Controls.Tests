using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки UPS и последней инвентаризации.</summary>
public sealed class EquipmentUpsPersistenceTests
{
    /// <summary>Быстрая связь и её удаление используют пару UPS и сервер.</summary>
    [Fact]
    public async Task QuickLink_UsesUpsAndServerIds()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1), ("КодЛицаВладельца", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиSIM", (byte)0))),
            Script.Scalar(1), Script.NonQuery(),
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1), ("КодЛицаВладельца", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиSIM", (byte)0))),
            Script.Scalar(1), Script.NonQuery(),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await service.AddUpsLinkAsync(10, 7);
        await service.DeleteUpsLinkAsync(10, 7);

        Assert.Contains("INSERT vwПодключенияUPS", connection.CommandLog[3]);
        Assert.Contains("НастроеноВыключение", connection.CommandLog[3]);
        Assert.Contains("DELETE vwПодключенияUPS", connection.CommandLog[7]);
    }

    /// <summary>Без роли 43 по владельцу связь UPS не добавляется.</summary>
    [Fact]
    public async Task QuickLinkWithoutOwnerRole_DoesNotWrite()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("КодТипаОборудования", 1), ("КодЛицаВладельца", 1))),
            Script.Rows(Script.Row(("КодТипаОборудования", 1), ("ЕстьХарактеристикиSIM", (byte)0))),
            Script.Scalar(0),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddUpsLinkAsync(10, 7));
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("INSERT vwПодключенияUPS"));
    }

    /// <summary>Diff удаляет только связи, меняет флаги и сохраняет тесты одной транзакцией.</summary>
    [Fact]
    public void DetailsDiff_ComposesTransactionWithoutTestDelete()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [
                new UpsServerLink { ServerEquipmentId = 1 },
                new UpsServerLink { ServerEquipmentId = 3 },
            ],
            [
                new UpsServerLink { ServerEquipmentId = 1, TurnOffConfigured = 1 },
                new UpsServerLink { ServerEquipmentId = 2 },
            ],
            [new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 1), BatteryTime = 10 }],
            [
                new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 2), BatteryTime = 20 },
                new UpsTest { TestedAt = new DateTime(2026, 11, 3), BatteryTime = 30 },
            ]);

        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("DELETE vwПодключенияUPS", command.Sql);
        Assert.Contains("UPDATE vwПодключенияUPS", command.Sql);
        Assert.Contains("INSERT vwПодключенияUPS", command.Sql);
        Assert.Contains("UPDATE vwПодключенияUPSтестирование", command.Sql);
        Assert.Contains("INSERT vwПодключенияUPSтестирование", command.Sql);
        Assert.DoesNotContain("DELETE vwПодключенияUPSтестирование", command.Sql);
        Assert.True(command.Parameters.ParameterNames.Count() >= 15);
    }

    /// <summary>Два неизменных сервера и два неизменных теста не создают SQL UPDATE.</summary>
    [Fact]
    public void DetailsDiff_UnchangedRows_DoNotUpdate()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 2, TurnOffConfigured = 1 }],
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 2, TurnOffConfigured = 1 }],
            [
                new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 1), BatteryTime = 10, Servers = "one", Issues = "old" },
                new UpsTest { Id = 6, TestedAt = new DateTime(2026, 11, 2), BatteryTime = 20, Servers = "two", Issues = "same" },
            ],
            [
                new UpsTest { Id = 5, TestedAt = new DateTime(2026, 11, 1), BatteryTime = 10, Servers = "one", Issues = "old" },
                new UpsTest { Id = 6, TestedAt = new DateTime(2026, 11, 2), BatteryTime = 20, Servers = "two", Issues = "same" },
            ]);

        Assert.DoesNotContain("UPDATE vwПодключенияUPS ", command.Sql);
        Assert.DoesNotContain("UPDATE vwПодключенияUPSтестирование", command.Sql);
        Assert.DoesNotContain("INSERT vwПодключенияUPS", command.Sql);
        Assert.DoesNotContain("DELETE vwПодключенияUPS", command.Sql);
    }

    /// <summary>Изменение только Issues одного теста создаёт ровно один UPDATE.</summary>
    [Fact]
    public void DetailsDiff_IssuesOnly_UpdatesOneTest()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }], [new UpsServerLink { ServerEquipmentId = 1 }],
            [new UpsTest { Id = 5, Issues = "old" }], [new UpsTest { Id = 5, Issues = "new" }]);

        Assert.Equal(1, command.Sql.Split("UPDATE vwПодключенияUPSтестирование").Length - 1);
        Assert.DoesNotContain("UPDATE vwПодключенияUPS ", command.Sql);
    }

    /// <summary>Дубликат текущего ключа отклоняется до генерации записи.</summary>
    [Fact]
    public void DetailsDiff_DuplicateCurrentKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => UpsSaveBuilder.Build(10, 1, [],
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 1 }], [], []));
    }

    /// <summary>Два новых теста с нулевыми ключами создают независимые INSERT.</summary>
    [Fact]
    public void DetailsDiff_TwoNewTests_InsertBothWithIndependentParameters()
    {
        var command = UpsSaveBuilder.Build(10, 1, [], [],
            [],
            [
                new UpsTest { TestedAt = new DateTime(2026, 11, 1), BatteryTime = 10, Servers = "srv-a", Issues = "first" },
                new UpsTest { TestedAt = new DateTime(2026, 11, 2), BatteryTime = 20, Servers = "srv-b", Issues = "second" },
            ]);

        Assert.Equal(2, command.Sql.Split("INSERT vwПодключенияUPSтестирование").Length - 1);
        Assert.Equal(10, command.Parameters.Get<int>("@testIns0Battery"));
        Assert.Equal(20, command.Parameters.Get<int>("@testIns1Battery"));
        Assert.Equal("srv-a", command.Parameters.Get<string>("@testIns0Servers"));
        Assert.Equal("srv-b", command.Parameters.Get<string>("@testIns1Servers"));
        Assert.Equal("first", command.Parameters.Get<string>("@testIns0Issues"));
        Assert.Equal("second", command.Parameters.Get<string>("@testIns1Issues"));
    }

    /// <summary>Дубликат сохранённого положительного ключа теста отклоняется.</summary>
    [Fact]
    public void DetailsDiff_DuplicatePersistedTestKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => UpsSaveBuilder.Build(10, 1, [], [], [],
            [new UpsTest { Id = 5 }, new UpsTest { Id = 5 }]));
    }

    /// <summary>Изменение сохранённого теста и два новых создают UPDATE и два INSERT без DELETE.</summary>
    [Fact]
    public void DetailsDiff_UpdateAndTwoNewTests_ComposesCompleteDiff()
    {
        var command = UpsSaveBuilder.Build(10, 1, [], [],
            [new UpsTest { Id = 5, Issues = "old" }],
            [new UpsTest { Id = 5, Issues = "new" }, new UpsTest { Issues = "first" }, new UpsTest { Issues = "second" }]);

        Assert.Equal(1, command.Sql.Split("UPDATE vwПодключенияUPSтестирование").Length - 1);
        Assert.Equal(2, command.Sql.Split("INSERT vwПодключенияUPSтестирование").Length - 1);
        Assert.DoesNotContain("DELETE vwПодключенияUPSтестирование", command.Sql);
    }

    /// <summary>Проверяются права только на изменяемые серверы, неизменный чужой сервер не блокирует тест UPS.</summary>
    [Fact]
    public void AccessGuard_TestsOnlyAndSingleFlagChange_DoNotIncludeUntouchedServers()
    {
        var original = new[]
        {
            new UpsServerLink { ServerEquipmentId = 2 },
            new UpsServerLink { ServerEquipmentId = 1 },
        };
        var testsOnly = UpsSaveBuilder.Build(10, 1, original, original,
            [new UpsTest { Id = 5, Issues = "old" }], [new UpsTest { Id = 5, Issues = "new" }]);
        Assert.DoesNotContain("@accessAffected", testsOnly.Parameters.ParameterNames);

        var flagChange = UpsSaveBuilder.Build(10, 1, original,
            [original[0], new UpsServerLink { ServerEquipmentId = 1, TurnOffConfigured = 1 }], [], []);
        Assert.Equal(1, flagChange.Parameters.Get<int>("@accessAffected0"));
        Assert.DoesNotContain("@accessAffected1", flagChange.Parameters.ParameterNames);
        Assert.Contains("fn_ТекущиеРоли", flagChange.Sql);
    }

    /// <summary>Удаление и добавление серверов включают оба фактически затронутых владельца.</summary>
    [Fact]
    public void AccessGuard_DeleteAndAdd_IncludeBothAffectedServers()
    {
        var command = UpsSaveBuilder.Build(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 2 }],
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 3 }], [], []);

        Assert.Equal(2, command.Parameters.Get<int>("@accessAffected0"));
        Assert.Equal(3, command.Parameters.Get<int>("@accessAffected1"));
        Assert.True(command.Sql.IndexOf("SELECT 3", StringComparison.Ordinal) < command.Sql.IndexOf("DELETE vwПодключенияUPS", StringComparison.Ordinal));
    }

    /// <summary>Отказ источника, изменяемого сервера и устаревшая связь не выполняют success-path.</summary>
    [Theory]
    [InlineData(1, typeof(UnauthorizedAccessException))]
    [InlineData(2, typeof(InvalidOperationException))]
    [InlineData(3, typeof(UnauthorizedAccessException))]
    public async Task SaveDetails_AccessGuardStatus_Throws(int status, Type exceptionType)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Scalar(status)]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => service.SaveUpsDetailsAsync(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }, new UpsServerLink { ServerEquipmentId = 2 }],
            [new UpsServerLink { ServerEquipmentId = 1 }], [], []));
        Assert.IsType(exceptionType, exception);
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Ошибка соединения при сохранении UPS не превращается в успешный статус.</summary>
    [Theory]
    [InlineData(53)]
    [InlineData(10054)]
    public async Task SaveDetailsConnectivity_ThrowsInsteadOfSuccess(int number)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            new ScriptEntry { Exception = SqlExceptionFactory.Create(number, "connection lost") },
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<SqlException>(() => service.SaveUpsDetailsAsync(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }], [new UpsServerLink { ServerEquipmentId = 1 }], [], []));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Отсутствие результата и неизвестный статус сохранения UPS отклоняются.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task SaveDetailsMissingOrUnknownStatus_FailsClosed(int? status)
    {
        var entry = status is int value ? Script.Scalar(value) : new ScriptEntry();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([entry]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveUpsDetailsAsync(10, 1,
            [new UpsServerLink { ServerEquipmentId = 1 }], [new UpsServerLink { ServerEquipmentId = 1 }], [], []));
        Assert.Single(connection.CommandLog);
    }

    /// <summary>Серверы и тесты восстанавливают отображаемые поля.</summary>
    [Fact]
    public async Task GetServersAndTests_MapsFields()
    {
        DapperColumnMapper.Initialize();
        var serverConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудованияUPS", 10), ("КодОборудованияСервера", 7),
                ("НастроеноВыключение", (byte)1), ("НазваниеСервера", "server"), ("КодТипаОборудования", 1))),
        ]));
        var testConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодТестирования", 5), ("КодОборудованияUPS", 10),
                ("ДатаТестирования", new DateTime(2026, 11, 1)), ("ВремяБатарейUPS", 20),
                ("СервераПолучившиеОповещение", "server"), ("ОшибкиТестирования", "issue"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => serverConnection), new TestAuthenticationStateProvider());
        var testService = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => testConnection), new TestAuthenticationStateProvider());

        var server = Assert.Single(await service.GetUpsServersAsync(10));
        var test = Assert.Single(await testService.GetUpsTestsAsync(10));

        Assert.Equal("server", server.ServerName);
        Assert.Equal(1, server.TurnOffConfigured);
        Assert.Equal(1, server.EquipmentTypeId);
        Assert.Contains("T.КодТипаОборудования", serverConnection.CommandLog[0]);
        Assert.Equal(20, test.BatteryTime);
        Assert.Equal("issue", test.Issues);
    }

    /// <summary>Последняя инвентаризация читается по максимальному времени без записи.</summary>
    [Fact]
    public async Task GetLastInventory_MapsFieldsWithoutCrud()
    {
        DapperColumnMapper.Initialize();
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("ВремяПроверки", new DateTime(2026, 11, 1)), ("КодОборудования", 7),
                ("Оборудование", "equipment"), ("КодРасположения", 55),
                ("СломаноРемонтуНеПодлежит", (byte)1), ("Примечание", "note"),
                ("ИзменилФИО", "Editor"))),
        ]));
        var service = new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider());

        var inventory = await service.GetLastInventoryAsync(7);

        Assert.NotNull(inventory);
        Assert.Equal(55, inventory.LocationId);
        Assert.Equal(1, inventory.IsBeyondRepair);
        Assert.Equal("Editor", inventory.ChangedByName);
        Assert.Contains("ORDER BY A.ВремяПроверки DESC", connection.CommandLog[0]);
        Assert.DoesNotContain("UPDATE", connection.CommandLog[0]);
        Assert.DoesNotContain("INSERT", connection.CommandLog[0]);
        Assert.DoesNotContain("DELETE", connection.CommandLog[0]);
    }
}
