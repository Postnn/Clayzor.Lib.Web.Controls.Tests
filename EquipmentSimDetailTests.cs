using System.Web;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки условий и URL детализации мобильной связи.</summary>
public sealed class EquipmentSimDetailTests
{
    /// <summary>Категория НДС, цвет и заголовок соответствуют трём состояниям legacy.</summary>
    [Theory]
    [InlineData(null, null, 0, "background:darkorange;", "Не определены условия договора сотовой связи")]
    [InlineData(1, 0, 2, "background:lightgray;", "Сумма указана без НДС")]
    [InlineData(0, 0, 1, "background:white;", "Сумма указана с НДС")]
    [InlineData(0, 1, 3, "background:white;", "Сумма указана с НДС")]
    public void DetailUri_UsesLegacyVatCategories(
        int? onTotal, int? inRating, int calculation, string color, string title)
    {
        var sim = Sim(onTotal, inRating);

        var uri = new Uri(sim.BuildAccountingPhoneUri("https://billing.test/phone", "Иванов & Co"));
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal(calculation.ToString(), query["NDSAllSumm"]);
        Assert.Equal(color, query["Color"]);
        Assert.Equal(title, query["Title"]);
        Assert.Equal("Иванов & Co", query["UserT"]);
        Assert.Equal("1", query["standalone"]);
    }

    /// <summary>Все параметры внешней формы сохранены, специальные символы кодируются.</summary>
    [Fact]
    public void DetailUri_EncodesContractAndPreservesParameters()
    {
        var sim = Sim(0, 0);
        sim.ContractName = "Билайн & Ко № А/1";
        sim.PhoneNumber = "+7 900 000-00-01";

        var uri = new Uri(sim.BuildAccountingPhoneUri("https://billing.test/phone?existing=1", "Пётр"));
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("1", query["existing"]);
        Assert.Equal(sim.ContractName, query["DogovorT"]);
        Assert.Equal(sim.PhoneNumber, query["Phone"]);
        Assert.Equal("12", query["Dogovor"]);
        Assert.Equal("0", query["NDSIn"]);
        Assert.Equal("20", query["NDSStavka"]);
        Assert.Equal("2", query["Scale"]);
        Assert.Equal("1", query["OpenMonth"]);
        Assert.Equal("0", query["Year"]);
        Assert.Equal("0", query["Month"]);
        Assert.Equal("0", query["bln"]);
        Assert.Equal(string.Empty, query["User"]);
    }

    /// <summary>URL строится только для одной доступной SIM и одного актуального ответственного.</summary>
    [Fact]
    public async Task DetailUri_RequiresSubordinateSimAndCurrentResponsible()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("От", DateTime.Today.AddDays(-1)),
                ("До", null), ("КодСотрудника", 4), ("Сотрудник", "Тестов А."))),
            Script.Rows(DetailRow(1)),
        ]));
        var service = CreateService(connection);

        var url = await service.GetSimDetailUrlAsync(7, "https://billing.test/phone");

        Assert.NotNull(url);
        Assert.Equal("Тестов А.", HttpUtility.ParseQueryString(new Uri(url).Query)["UserT"]);
        Assert.Equal(2, connection.CommandLog.Count);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Единственный ответственный используется как в legacy, а несколько SIM отклоняются.</summary>
    [Fact]
    public async Task DetailUri_UsesSingleResponsibleAndRejectsMultipleSims()
    {
        var historical = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("От", DateTime.Today.AddDays(-2)),
                ("До", DateTime.Today.AddDays(-1)), ("КодСотрудника", 4), ("Сотрудник", "Старый"))),
            Script.Rows(DetailRow(1)),
        ]));
        var historicalUrl = await CreateService(historical).GetSimDetailUrlAsync(7, "https://billing.test/phone");
        Assert.NotNull(historicalUrl);
        Assert.Equal("Старый", HttpUtility.ParseQueryString(new Uri(historicalUrl).Query)["UserT"]);

        var multiple = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("От", DateTime.Today),
                ("До", null), ("КодСотрудника", 4), ("Сотрудник", "Тестов А."))),
            Script.Rows(DetailRow(1), DetailRow(1)),
        ]));
        Assert.Null(await CreateService(multiple).GetSimDetailUrlAsync(7, "https://billing.test/phone"));
    }

    /// <summary>Недоступная SIM без внешней БД не открывает внешнюю форму.</summary>
    [Fact]
    public async Task DetailUri_RejectsNotSubordinateSim()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("От", DateTime.Today),
                ("До", null), ("КодСотрудника", 4), ("Сотрудник", "Тестов А."))),
            Script.Rows(DetailRow(0)), Script.Scalar(0),
        ]));
        Assert.Null(await CreateService(connection).GetSimDetailUrlAsync(7, "https://billing.test/phone"));
    }

    /// <summary>При отсутствии роли внешняя подчинённость проверяется только при наличии БД.</summary>
    [Fact]
    public async Task DetailUri_UsesExternalSubordinateWhenAvailable()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Rows(Script.Row(("КодОборудования", 7), ("От", DateTime.Today),
                ("До", null), ("КодСотрудника", 4), ("Сотрудник", "Тестов А."))),
            Script.Rows(DetailRow(0)), Script.Scalar(1), Script.Scalar(1),
        ]));
        var url = await CreateService(connection).GetSimDetailUrlAsync(7, "https://billing.test/phone");

        Assert.NotNull(url);
        Assert.Contains(connection.CommandLog, sql => sql.Contains("DB_ID"));
        Assert.Contains(connection.CommandLog, sql => sql.Contains("vwТарификацияСотовыхИтоги"));
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new EquipmentFormService(new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }

    private static Dictionary<string, object?> DetailRow(byte subordinate) => Script.Row(
        ("КодОборудования", 7), ("НомерТелефона", "79000000001"), ("КодДоговора", 12),
        ("КодЛицаИсполнителя", 908), ("Договор", "Билайн - Тест"), ("ОкруглениеСуммы", 2), ("СтавкаНДС", 20m),
        ("НДСВТарификации", (byte)0), ("НДСНаСумму", (byte)0), ("Подчинённый", subordinate));

    private static EquipmentSimCard Sim(int? onTotal, int? inRating) => new()
    {
        PhoneNumber = "79000000001", ContractId = 12, ContractName = "Билайн - Тест",
        VatOnTotal = onTotal is null ? null : (byte)onTotal.Value,
        VatInRating = inRating is null ? null : (byte)inRating.Value,
        VatRate = 20m, AmountScale = 2,
    };
}
