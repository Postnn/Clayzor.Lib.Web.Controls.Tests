using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Kesco.App.Web.Inventory.Rules;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки raw/display имён и допустимых UPS-устройств EQ8-11.</summary>
public sealed class Eq8UpsDataTests
{
    /// <summary>Серверный SELECT возвращает настоящее сетевое имя и отдельный display label.</summary>
    [Fact]
    public void ServersQuery_KeepsRawNetworkNameSeparateFromEquipmentName()
    {
        var sql = SQLQueries.SELECT_ID_СервераПодключенияUPS;
        Assert.Contains("E.СетевоеИмя AS НазваниеСервера", sql);
        Assert.Contains("AS НазваниеОборудования", sql);
        Assert.Contains("U.Изменено", sql);
        Assert.Contains("ИзменилФИО", sql);
        Assert.DoesNotContain("CASE WHEN E.СетевоеИмя <> '' THEN E.СетевоеИмя ELSE", sql);
    }

    /// <summary>Server lookup и GetById не предлагают типы, которые server guard отклоняет.</summary>
    [Fact]
    public async Task ServerLookup_UsesSameApplicabilityAsSaveGuard()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([Script.Rows(), Script.Rows()]));
        var source = new UpsEquipmentSelectDataSource(CreateDb(connection), upsOnly: false);

        await source.SearchAsync(new ClaySelectSearchRequest { SearchText = string.Empty, Limit = 8 });
        await source.GetByIdAsync(7);

        Assert.Equal(2, connection.CommandLog.Count);
        foreach (var command in connection.CommandLog)
        {
            Assert.Contains("T.КодТипаОборудования < 100", command);
            Assert.Contains("T.ЕстьХарактеристикиSIM = 0", command);
            Assert.Contains("T.КодТипаОборудования <> 53", command);
        }
        Assert.Equal(0, connection.ParameterLog[1]["UpsOnly"]);
    }

    /// <summary>Named computer доступен, unnamed остаётся display-only, noncomputer и unknown historical не смешиваются.</summary>
    [Fact]
    public void Selection_DistinguishesNamedUnnamedNoncomputerAndUnknownHistorical()
    {
        var servers = new[]
        {
            new UpsServerLink { ServerEquipmentId = 1, EquipmentTypeId = 1, ServerName = "srv-a", EquipmentName = "Компьютер A" },
            new UpsServerLink { ServerEquipmentId = 2, EquipmentTypeId = 1, ServerName = "", EquipmentName = "Компьютер без имени" },
            new UpsServerLink { ServerEquipmentId = 3, EquipmentTypeId = 2, ServerName = "printer", EquipmentName = "Принтер" },
        };
        var selected = UpsTestServerSelection.ParseSelected(
            "srv-a,legacy", UpsTestServerSelection.AvailableNames(servers));

        var rows = UpsTestServerSelection.BuildRows(selected, servers);

        Assert.Equal(["srv-a", "legacy"], rows.Select(row => row.Name).ToArray());
        Assert.DoesNotContain(rows, row => row.Name.Contains("Компьютер без имени"));
        Assert.DoesNotContain(rows, row => row.Name == "printer");
        Assert.False(rows.Single(row => row.Name == "legacy").IsRecognized);
        Assert.Equal("srv-a,legacy", UpsTestServerSelection.Format(selected));
        Assert.DoesNotContain("legacy", UpsTestServerSelection.PatchIssues("", rows));
    }

    /// <summary>SQL row сохраняет raw/display и audit-поля при чтении.</summary>
    [Fact]
    public async Task ServerRead_MapsDisplayAndAuditWithoutFallbackHostname()
    {
        DapperColumnMapper.Initialize();
        var changedAt = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(
                ("КодОборудованияUPS", 10), ("КодОборудованияСервера", 7), ("НастроеноВыключение", (byte)0),
                ("КодТипаОборудования", 1), ("НазваниеСервера", ""),
                ("НазваниеОборудования", "Компьютер без имени"), ("ИзменилФИО", "Server Editor"), ("Изменено", changedAt))),
        ]));
        var service = new Kesco.App.Web.Inventory.Services.EquipmentFormService(CreateDb(connection));

        var server = Assert.Single(await service.GetUpsServersAsync(10));

        Assert.Equal("", server.ServerName);
        Assert.Equal("Компьютер без имени", server.EquipmentName);
        Assert.Equal("Server Editor", server.GetType().GetProperty("ChangedByName")?.GetValue(server));
        Assert.Equal(changedAt, server.GetType().GetProperty("ChangedAt")?.GetValue(server));
    }

    private static DbManager CreateDb(ScriptedConnection connection) =>
        new("Server=test", new FakeSqlErrorHandler(), () => connection);
}
