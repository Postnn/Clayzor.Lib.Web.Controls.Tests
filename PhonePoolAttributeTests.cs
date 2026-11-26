using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.PhonePoolAttribute;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Контракт составного ключа, записи и общих команд карточки атрибута пула.</summary>
public sealed class PhonePoolAttributeTests
{
    private static readonly PhonePoolAttributeKey Key = new(1002, "Тест+%");

    /// <summary>Query decoding проходит один раз. Разделитель US позволяет передать тип с двоеточием.</summary>
    [Theory]
    [InlineData("1002%3A%25%2B", "%+")]
    [InlineData("1002%1F%25%2B", "%+")]
    [InlineData("1002%1FA%3AB", "A:B")]
    [InlineData("1002%3A%253A", "%3A")]
    public void FrameworkDecodesCompositeKeyOnce(string encoded, string type)
    {
        var decoded = QueryHelpers.ParseQuery("?id=" + encoded)["id"].ToString();
        var key = Assert.IsType<PhonePoolAttributeKey>(PhonePoolAttributeKey.Parse(decoded));
        Assert.Equal(type, key.Type);
        Assert.Equal(1002, key.PoolId);
        var roundTrip = QueryHelpers.ParseQuery("?id=" + Uri.EscapeDataString(key.RouteId))["id"].ToString();
        Assert.Equal(key, PhonePoolAttributeKey.Parse(roundTrip));
    }

    /// <summary>Пустой, неоднозначный и дважды закодированный ключ отклоняются.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1002")]
    [InlineData("1002:a:b")]
    [InlineData("1002%3Aa")]
    [InlineData("1002:")]
    [InlineData("0:a")]
    [InlineData("pool:a")]
    [InlineData("1002:😀")]
    public void InvalidKeyIsRejected(string? id) => Assert.Null(PhonePoolAttributeKey.Parse(id));

    /// <summary>Границы SQL varchar проверяются без молчаливой замены Unicode.</summary>
    [Fact]
    public void ValueValidationKeepsEmptyAndCyrillic()
    {
        Assert.Null(PhonePoolAttributeKey.ValidateValue(""));
        Assert.Null(PhonePoolAttributeKey.ValidateValue(new string('Ж', 150)));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue(new string('Ж', 151)));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue("😀"));
        Assert.NotNull(PhonePoolAttributeKey.ValidateValue(null));
    }

    /// <summary>Прямые команды учитывают исходную версию и отзыв права.</summary>
    [Theory]
    [InlineData("insert-conflict")]
    [InlineData("update-missing")]
    [InlineData("delete-missing")]
    [InlineData("revoked")]
    public async Task ServerRejectsStaleOrUnauthorizedCommand(string condition)
    {
        using var connection = new Probe();
        var original = Value("исходное");
        if (condition == "insert-conflict") connection.Row = original;
        if (condition == "revoked") { connection.Allowed = false; connection.Row = original; }
        using var db = Db(connection);
        if (condition == "delete-missing")
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, Key, original, default));
        else
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, condition == "insert-conflict" ? null : original, "", default));
        Assert.Empty(connection.Writes);
        Assert.Equal(0, connection.Commits);
    }

    /// <summary>Потеря ответа COMMIT остаётся неопределённым исходом.</summary>
    [Fact]
    public async Task CommitResponseLossIsNotKnownRejection()
    {
        using var connection = new Probe { LoseCommit = true };
        using var db = Db(connection);
        await Assert.ThrowsAsync<PhonePoolAttributeUnknownException>(() =>
            PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, null, "", default));
        Assert.Equal(["INSERT"], connection.Writes);
        Assert.Equal(1, connection.Commits);
        Assert.NotNull(connection.Row);
    }

    /// <summary>Применить создаёт пустое значение. Повторное применение и OK без правок не пишут.</summary>
    [Fact]
    public async Task ApplyCanInsertEmptyValueWithoutASecondInsert()
    {
        using var sql = new Probe();
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Тестовый пул", page.Markup);
            Assert.False(Button(page, "Применить").HasAttribute("disabled"));
        });

        await Button(page, "Применить").ClickAsync(new MouseEventArgs());

        Assert.Equal(["INSERT"], sql.Writes);
        Assert.Equal("", sql.Row!.Value);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        await Button(page, "OK").ClickAsync(new MouseEventArgs());
        Assert.Single(sql.Writes);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    /// <summary>Отрицательный ответ при удалении оставляет значение и не обновляет родителя.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRunsOnlyAfterAffirmativeAnswer(bool accept)
    {
        using var sql = new Probe { Row = Value("сохранено") };
        await using var context = CreateContext(sql);
        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.False(Button(page, "Удалить").HasAttribute("disabled")));
        var action = Button(page, "Удалить").ClickAsync(new MouseEventArgs());
        var text = accept ? "Да" : "Нет";
        provider.WaitForAssertion(() => Assert.Contains(provider.FindAll("button"), b => b.TextContent.Trim() == text));
        await provider.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new MouseEventArgs());
        await action;
        Assert.Equal(accept ? new[] { "DELETE" } : Array.Empty<string>(), sql.Writes);
        Assert.Equal(!accept, sql.Row is not null);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    /// <summary>Отказ чтения типа оставляет карточку без команды создания значения.</summary>
    [Fact]
    public async Task UnknownTypeCannotBecomeNewValue()
    {
        using var sql = new Probe { TypeExists = false };
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("Тип атрибута не найден", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
    }

    /// <summary>Редактирование существующей пары берёт последний введённый символ и обновляет аудит.</summary>
    [Fact]
    public async Task ExistingApplyUsesTheLatestValue()
    {
        using var sql = new Probe { Row = Value("исходное") };
        await using var context = CreateContext(sql);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains("исходное", page.Markup));
        await page.FindAll("input").Last().InputAsync(new ChangeEventArgs { Value = "новое!" });
        await Button(page, "Применить").ClickAsync(new MouseEventArgs());

        Assert.Equal(["UPDATE"], sql.Writes);
        Assert.Equal("новое!", sql.Row!.Value);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
    }

    /// <summary>Нулевой результат UPDATE или DELETE оставляет прежнюю пару.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroAffectedRowsCannotCommit(bool delete)
    {
        var original = Value("исходное");
        using var connection = new Probe { Row = original, Affected = 0 };
        using var db = Db(connection);
        if (delete)
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.DeleteAsync(db, "fixture", 4, Key, original, default));
        else
            await Assert.ThrowsAsync<PhonePoolAttributeRejectedException>(() =>
                PhonePoolAttributeData.SaveAsync(db, "fixture", 4, Key, original, "другое", default));
        Assert.Equal(0, connection.Commits);
        Assert.Equal(original, connection.Row);
    }

    /// <summary>Собственный черновик виден при исчезновении родителей или отказе свежего чтения.</summary>
    [Theory]
    [InlineData("pool")]
    [InlineData("type")]
    [InlineData("denied")]
    public async Task InaccessibleParentShowsOwnDraftWithoutRestoringWriteAccess(string failure)
    {
        using var sql = new Probe { PoolExists = failure != "pool", TypeExists = failure != "type", DenyRead = failure == "denied" };
        const string localValue = "Моё сохранённое значение";
        var snapshot = RecoverySnapshot(localValue);
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = snapshot }), hasPendingRaw: true);
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains(localValue, page.Markup));
        Assert.Contains(Key.PoolId.ToString(), page.Markup);
        Assert.Contains(Key.Type, page.Markup);
        Assert.Empty(page.FindAll("[data-draft-key='pool-attribute.value']"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.DoesNotContain("Тестовый пул", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.True(Button(page, "OK").HasAttribute("disabled"));
        Assert.DoesNotContain(page.FindAll("button"), button => button.TextContent.Trim() == "Удалить");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        var registration = Assert.Single(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Equal(new object[] { "phone-pool-attribute", Key.RouteId, "4" }, registration.Arguments.Take(3));
        Assert.Empty(sql.Writes);
    }

    /// <summary>Сверка Unknown использует неизменный ключ из URL и сохраняет маркер.</summary>
    [Fact]
    public async Task UnresolvedOperationStaysBlockedWhenParentsReturn()
    {
        using var sql = new Probe { TypeExists = false };
        var snapshot = RecoverySnapshot("Значение из попытки");
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, attemptId = "ppa-attempt", kind = "phone-pool-attribute.update", status = "unknown", revision = 8,
            data = snapshot, scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 8, null, operation,
            new Dictionary<string, string> { ["main"] = snapshot }));
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains("Обнаружена незавершённая операция", page.Markup));
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        sql.TypeExists = true;
        sql.Row = Value("Значение из попытки");
        await Button(page, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("Данные в базе совпадают с отправленными", page.Markup);
        Assert.Contains("ppa-attempt", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    /// <summary>Восстановление доступа не заменяет исходный снимок актуальной версией без сверки.</summary>
    [Fact]
    public async Task ReturnOfAccessWithChangedRowPreservesLocalDraftAndBlocksWrite()
    {
        using var sql = new Probe { Row = Value("Новое значение в БД") };
        await using var context = CreateContext(sql, new ClayDraftRestore("recovery", true, 7, null, null,
            new Dictionary<string, string> { ["main"] = RecoverySnapshot("Моё сохранённое значение") }));
        var page = context.Render<PhonePoolAttributeForm>();

        page.WaitForAssertion(() => Assert.Contains("Карточка изменилась", page.Markup));
        Assert.Contains("Моё сохранённое значение", page.Markup);
        Assert.True(Button(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(sql.Writes);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    /// <summary>Доступ к черновику требует валидной пары и доверенной идентичности.</summary>
    [Theory]
    [InlineData("invalid", true)]
    [InlineData(null, false)]
    public async Task InvalidKeyOrIdentityDoesNotRegisterDraft(string? id, bool authenticated)
    {
        using var sql = new Probe();
        await using var context = CreateContext(sql, id: id, authenticated: authenticated);
        var page = context.Render<PhonePoolAttributeForm>();
        page.WaitForAssertion(() => Assert.Contains(authenticated ? "Не удалось установить идентификатор" : "Не удалось подтвердить текущего сотрудника", page.Markup));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(sql.Writes);
    }

    private static string RecoverySnapshot(string value) => JsonSerializer.Serialize(new
    {
        Original = new PhonePoolAttributeForm.FormState("Исходное значение"),
        Current = new PhonePoolAttributeForm.FormState(value),
    });

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PhonePoolAttributeForm> page, string name) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == name);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private static PhonePoolAttributeValue Value(string value) => new(value, 4, new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), "Инженер");

    private static BunitContext CreateContext(Probe connection, ClayDraftRestore? restore = null, string? id = null,
        bool hasPendingRaw = false, bool authenticated = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(restore ?? new ClayDraftRestore("phone-pool-fixture", false, 0, null, null));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], hasPendingRaw ? ["data:pool-attribute.value"] : []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(!hasPendingRaw, hasPendingRaw ? ["main"] : [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 0, 0));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"fixture","kind":"phone-pool-attribute.insert","status":"running","revision":2,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.Services.AddScoped(_ => Db(connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider>(new FixtureAuthentication(authenticated));
        context.Services.AddMudServices();
        context.Services.AddMudExtensions();
        context.Render<MudPopoverProvider>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/Equipment/PhonePoolAtribute_form?id=" + Uri.EscapeDataString(id ?? Key.RouteId));
        return context;
    }

    private sealed class FixtureAuthentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(authenticated ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test") : new ClaimsIdentity())));
    }

    private sealed class Probe : DbConnection
    {
        public bool Allowed { get; set; } = true;
        public bool PoolExists { get; init; } = true;
        public bool TypeExists { get; set; } = true;
        public bool DenyRead { get; init; }
        public bool LoseCommit { get; init; }
        public int Affected { get; init; } = 1;
        public int Commits { get; set; }
        public PhonePoolAttributeValue? Row { get; set; }
        public List<string> Writes { get; } = [];
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
        internal PhonePoolAttributeValue? Row { get; set; } = owner.Row;
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
            if (CommandText.Contains("SUSER_SID()")) return Rows([("КодСотрудника", typeof(int))], [4]).CreateDataReader();
            if (CommandText.Contains("fn_ТекущиеРоли"))
                return Rows([("RoleId", typeof(int)), ("PersonId", typeof(int))],
                    owner.Allowed ? [(int)Роли.Дежурный_инженер, 0] : null).CreateDataReader();
            if (CommandText.Contains("HAS_PERMS_BY_NAME"))
                return Rows([("CanRead", typeof(bool)), ("CanInsert", typeof(bool)), ("CanUpdate", typeof(bool)),
                    ("CanDelete", typeof(bool))], [true, true, true, true]).CreateDataReader();

            Assert.Equal(Key.PoolId, Parameter("poolId"));
            Assert.Equal(Key.Type, Parameter("type"));
            if (CommandText.Contains("@@ROWCOUNT"))
            {
                var transaction = Assert.IsType<ProbeTransaction>(DbTransaction);
                var operation = CommandText[..CommandText.IndexOf(' ')];
                owner.Writes.Add(operation);
                if (owner.Affected == 1) transaction.Row = operation == "DELETE" ? null : Value((string)Parameter("value")!);
                return Rows([("Affected", typeof(int))], [owner.Affected]).CreateDataReader();
            }

            if (owner.DenyRead && DbTransaction is null) throw SqlExceptionFactory.Create(229, "Тестовый отказ SELECT");
            var row = DbTransaction is ProbeTransaction tx ? tx.Row : owner.Row;
            if (DbTransaction is not null) Assert.Contains("UPDLOCK", CommandText);
            return new DataTableReader([
                Rows([("ПулТелефона", typeof(string))], owner.PoolExists ? ["Тестовый пул"] : null),
                Rows([("Type", typeof(string)), ("Description", typeof(string))], owner.TypeExists ? [Key.Type, "Описание"] : null),
                Rows([("Value", typeof(string)), ("ChangedBy", typeof(int)), ("ChangedAt", typeof(DateTime)), ("ChangedByName", typeof(string))],
                    row is null ? null : [row.Value, row.ChangedBy, row.ChangedAt, row.ChangedByName]),
            ]);
        }

        private object? Parameter(string name) =>
            Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == name).Value;

        private static DataTable Rows((string Name, Type Type)[] columns, object?[]? values)
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            if (values is not null) table.Rows.Add(values.Select(v => v ?? DBNull.Value).ToArray());
            return table;
        }
    }
}

