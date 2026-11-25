using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Telefon.Beeline;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет строковый договор, ровно два параметра записи и сценарии карточки Билайн.</summary>
public sealed class BeelineParamsTests
{
    private const string Number = "000AB-001";
    private static readonly string[] Zones = ["Russian Standard Time", "Kaliningrad Standard Time", "Ekaterinburg Standard Time"];
    private static readonly DateTime Actual = new(2026, 11, 25, 23, 59, 59);
    private static readonly BeelineContract Original = new(Number, Number, "", "", null, "Тестовая компания", "",
        Actual, Zones[0], "initial@example.test", false, 4, Actual.AddHours(-1), "Тестовый инженер");

    /// <summary>Legacy email допускает +, апостроф и регистр. Пробелы и некорректный адрес отклоняются.</summary>
    [Theory]
    [InlineData("A+Alert@example.test", true)]
    [InlineData("o'connor@example.test", true)]
    [InlineData("user@example.test ", false)]
    [InlineData("user@example.test\n", false)]
    [InlineData("bad@@example.test", false)]
    [InlineData("user😀@example.test", false)]
    public void EmailUsesLegacyPatternAndSqlEncoding(string email, bool accepted) =>
        Assert.Equal(accepted, BeelineRules.Validate(new BeelineDraft(Zones[0], email), Zones).Count == 0);

    /// <summary>Проверяется точное конфигурационное значение и граница varchar(50).</summary>
    [Fact]
    public void ServerValidationRejectsUnknownZoneAndOverlongAddress()
    {
        Assert.True(BeelineRules.ValidNumber(Number));
        Assert.False(BeelineRules.ValidNumber(new string('0', 51)));
        Assert.False(BeelineRules.ValidNumber("\t"));
        Assert.Empty(BeelineRules.Validate(new BeelineDraft(Zones[0], new string('x', 40) + "@a.example"), Zones));
        Assert.Equal(2, BeelineRules.Validate(new BeelineDraft("Europe/Moscow", new string('x', 41) + "@a.example"), Zones).Count);
        Assert.Equal(2, BeelineRules.Validate(new BeelineDraft("", ""), Zones).Count);
    }

    /// <summary>Прямой вызов проверяет изменённую строку, роль, область роли и существование записи.</summary>
    [Theory]
    [InlineData("stale")]
    [InlineData("deleted")]
    [InlineData("viewer")]
    [InlineData("scoped")]
    [InlineData("principal")]
    public async Task ServerRejectsStaleOrUnauthorizedUpdate(string condition)
    {
        using var sql = new Probe();
        if (condition == "stale") sql.Row = Original with { EmailManager = "other@example.test" };
        if (condition == "deleted") sql.Row = null;
        if (condition == "viewer") sql.Role = Роли.Наблюдатель_услуг_связи;
        if (condition == "scoped") sql.PersonScope = 1;
        var expected = condition == "principal" ? 5 : 4;
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineRejectedException>(() =>
            BeelineData.SaveAsync(db, "fixture", expected, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
        Assert.Equal(0, sql.Updates);
        Assert.Equal(0, sql.Commits);
    }

    /// <summary>Если триггер вернул параметры основного договора, UPDATE откатывается.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedReadBackOrZeroRowsCannotCommit(bool overwriteByTrigger)
    {
        using var sql = new Probe { OverrideParameters = overwriteByTrigger, Affected = overwriteByTrigger ? 1 : 0 };
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineRejectedException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
        Assert.Equal(1, sql.Updates);
        Assert.Equal(0, sql.Commits);
        Assert.Equal(Original, sql.Row);
    }

    /// <summary>Статус факта выполнения не угадывается после потерянного ответа COMMIT.</summary>
    [Fact]
    public async Task CommitTimeoutStaysUnknown()
    {
        using var sql = new Probe { LoseCommit = true };
        using var db = Db(sql);
        await Assert.ThrowsAsync<BeelineUnknownException>(() =>
            BeelineData.SaveAsync(db, "fixture", 4, Original, new BeelineDraft(Zones[1], "new@example.test"), Zones, default));
        Assert.Equal(1, sql.Updates);
        Assert.Equal(1, sql.Commits);
    }

    /// <summary>Обычный договор скрывает МБ. Сохранение берёт последний символ и обновляет аудит.</summary>
    [Fact]
    public async Task ExistingEditorUpdatesOnlyTwoFields()
    {
        using var sql = new Probe();
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains(Number, card.Markup));
        Assert.DoesNotContain("Номер телефона МБ:", card.Markup);
        Assert.Contains("23:59:59", card.Markup);
        Assert.Equal(Zones, card.FindComponent<ClayDropDownList<string, string>>().Instance.Items);
        await card.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "last+symbol@example.test" });
        await Button(card, "Сохранить").ClickAsync(new MouseEventArgs());
        Assert.Equal(1, sql.Commits);
        Assert.Equal(Original with { EmailManager = "last+symbol@example.test", ChangedAt = Original.ChangedAt.AddSeconds(1) }, sql.Row);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    /// <summary>Мобильный бюджет виден даже без найденной активной SIM.</summary>
    [Fact]
    public async Task MissingEquipmentReportsErrorWithoutAWrite()
    {
        using var sql = new Probe { Row = Original with { BudgetNumber = Number, BudgetPhone = "79000000000", UserName = "Тест" } };
        await using var context = Configure(sql);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Мобильный бюджет", card.Markup));
        await card.FindAll("a").Single(link => link.TextContent.Contains("Номер телефона МБ")).ClickAsync(new MouseEventArgs());
        Assert.Contains("не удалось определить код оборудования", card.Markup);
        Assert.Equal(0, sql.Updates);
    }

    /// <summary>Исторический пояс отображается при отсутствии настроенного списка, а запись недоступна.</summary>
    [Fact]
    public async Task MissingConfigurationPreservesExistingZone()
    {
        using var sql = new Probe { Row = Original with { TimeZone = "Historic Standard Time" } };
        await using var context = Configure(sql, includeZones: false);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Список Time Zone не настроен", card.Markup));
        var dropdown = card.FindComponent<ClayDropDownList<string, string>>();
        Assert.Equal("Historic Standard Time", dropdown.Instance.Value);
        Assert.True(Button(card, "Сохранить").HasAttribute("disabled"));
    }

    /// <summary>Карточка оборудования открывается по подтверждённому ID и сохраняет локальный email.</summary>
    [Fact]
    public async Task ActiveSimLinksToEquipmentAndPreservesDraft()
    {
        using var sql = new Probe { Row = Original with
        {
            EquipmentId = 27, BudgetNumber = Number, BudgetPhone = "79000000000", UserName = "Тест",
        } };
        await using var context = Configure(sql);
        context.JSInterop.Setup<bool>("clayFormWindow.open", _ => true).SetResult(true);
        var card = context.Render<BeelineParamsForm>();
        card.WaitForAssertion(() => Assert.Contains("Мобильный бюджет", card.Markup));
        await card.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "draft@example.test" });
        await card.FindAll("a").Single(link => link.TextContent.Contains("Номер телефона МБ")).ClickAsync(new MouseEventArgs());
        var open = Assert.Single(context.JSInterop.Invocations["clayFormWindow.open"]);
        Assert.EndsWith("/Equipment/EquipmentForm?id=27", Assert.IsType<string>(open.Arguments[0]));
        Assert.Equal("beeline-sim-27", open.Arguments[1]);
        Assert.Contains("draft@example.test", card.Markup);
        Assert.False(Button(card, "Сохранить").HasAttribute("disabled"));
        Assert.Equal(0, sql.Updates);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<BeelineParamsForm> card, string label) =>
        card.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private static BunitContext Configure(Probe sql, bool includeZones = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("beeline-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"fixture","kind":"beeline.update","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => Db(sql));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        var settings = new ConfigurationBuilder();
        if (includeZones)
            settings.AddInMemoryCollection(Zones.Select((zone, index) =>
                new KeyValuePair<string, string?>($"BeelineTimeZones:{index}", zone)));
        context.Services.AddSingleton<IConfiguration>(settings.Build());
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/Telefon/BeelineParams?id=" + Number);
        return context;
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class Probe : DbConnection
    {
        public Роли Role { get; set; } = Роли.Администратор_тарифов_связи;
        public int PersonScope { get; set; }
        public int Affected { get; init; } = 1;
        public bool OverrideParameters { get; init; }
        public bool LoseCommit { get; init; }
        public int Commits { get; set; }
        public int Updates { get; set; }
        public BeelineContract? Row { get; set; } = Original;
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return new ProbeTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new Command(this);
    }

    private sealed class ProbeTransaction(Probe owner) : DbTransaction
    {
        internal BeelineContract? Row { get; set; } = owner.Row;
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            owner.Row = Row;
            owner.Commits++;
            if (owner.LoseCommit) throw new TimeoutException();
        }
        public override void Rollback() { }
    }

    private sealed class Command(Probe owner) : DbCommand
    {
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        public override void Prepare() { }
        public override void Cancel() { }
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SUSER_SID()"))
                return Rows([("КодСотрудника", typeof(int))], [4]).CreateDataReader();
            if (CommandText.Contains("fn_ТекущиеРоли"))
                return Rows([("RoleId", typeof(int)), ("PersonId", typeof(int))], [(int)owner.Role, owner.PersonScope]).CreateDataReader();
            if (CommandText.Contains("HAS_PERMS_BY_NAME"))
                return Rows([("CanRead", typeof(bool)), ("CanUpdate", typeof(bool)), ("NamedWriter", typeof(bool))],
                    [true, true, false]).CreateDataReader();

            var id = Parameter("id");
            Assert.Equal(Number, id.Value);
            Assert.Equal(DbType.String, id.DbType);
            Assert.Equal(50, id.Size);
            if (CommandText.StartsWith("UPDATE ", StringComparison.Ordinal))
            {
                var tx = Assert.IsType<ProbeTransaction>(DbTransaction);
                Assert.Equal(new[] { "email", "id", "timeZone" }, Parameters.Cast<DbParameter>().Select(p => p.ParameterName).Order().ToArray());
                owner.Updates++;
                if (owner.Affected > 0 && !owner.OverrideParameters)
                    tx.Row = tx.Row! with { TimeZone = (string)Parameter("timeZone").Value!,
                        EmailManager = (string)Parameter("email").Value!, ChangedAt = tx.Row!.ChangedAt.AddSeconds(1) };
                return Rows([("Affected", typeof(int))], [owner.Affected]).CreateDataReader();
            }

            Assert.Equal((int)Лица.Билайн, Parameter("beeline").Value);
            if (DbTransaction is not null) Assert.Contains("UPDLOCK", CommandText);
            var row = DbTransaction is ProbeTransaction transaction ? transaction.Row : owner.Row;
            return Rows([
                ("Number", typeof(string)), ("BaseNumber", typeof(string)), ("BudgetNumber", typeof(string)),
                ("BudgetPhone", typeof(string)), ("EquipmentId", typeof(int)), ("Customer", typeof(string)),
                ("UserName", typeof(string)), ("Actual", typeof(DateTime)), ("TimeZone", typeof(string)),
                ("EmailManager", typeof(string)), ("HasAccount", typeof(bool)), ("ChangedBy", typeof(int)),
                ("ChangedAt", typeof(DateTime)), ("ChangedByName", typeof(string))],
                row is null ? null : [row.Number, row.BaseNumber, row.BudgetNumber, row.BudgetPhone, row.EquipmentId,
                    row.Customer, row.UserName, row.Actual, row.TimeZone, row.EmailManager, row.HasAccount,
                    row.ChangedBy, row.ChangedAt, row.ChangedByName]).CreateDataReader();
        }

        private DbParameter Parameter(string name) =>
            Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == name);

        private static DataTable Rows((string Name, Type Type)[] columns, object?[]? values)
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            if (values is not null) table.Rows.Add(values.Select(value => value ?? DBNull.Value).ToArray());
            return table;
        }
    }
}

