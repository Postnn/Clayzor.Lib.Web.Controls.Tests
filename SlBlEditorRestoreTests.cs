using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление и принятие сырого ввода через реальный редактор SlBl.</summary>
public sealed class SlBlEditorRestoreTests
{
    private const string Address = "mailbox@example.invalid";
    private const string Baseline = "base.example.invalid";
    private const string OldText = "older.example.invalid";
    private const string LatestText = "newer.example.invalid";

    /// <summary>Неоконченное применение raw блокирует Begin. Повтор использует точный последний текст.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveWaitsForRawAndUsesBrowserValue(bool throws, bool changesDuringConfirmation)
    {
        using var sql = new MailboxConnection();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var ready = false;
        var browserChanged = false;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("slbl-fixture", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        var initial = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => !ready);
        if (throws) initial.SetException(new JSException("Временная ошибка применения"));
        else initial.SetResult(new ClayDraftRawApplyResult([], ["data:slbl.text"]));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => ready)
            .SetResult(new ClayDraftRawApplyResult(["data:slbl.text"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => !ready)
            .SetResult(new ClayDraftRestoreReadiness(false, ["main"], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => ready)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-test","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        var browser = context.JSInterop.SetupModule("./js/slblDraftText.js");
        browser.Setup<string?>("readText", _ => !browserChanged).SetResult(LatestText);
        browser.Setup<string?>("readText", _ => browserChanged).SetResult("changed.example.invalid");

        var dialogs = ConfigureEditor(context, sql);

        var form = context.Render<SlBlForm>(parameters => parameters.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        var save = form.FindComponents<ClayToolbarButton>().Single(button => button.Instance.Options.Text == "Сохранить");
        await form.InvokeAsync(() => save.Instance.OnClick.InvokeAsync());
        Assert.Equal(0, sql.Dispatches);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);

        ready = true;
        await Button(form, "Повторить восстановление").ClickAsync(new MouseEventArgs());
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        browserChanged = changesDuringConfirmation;
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        if (changesDuringConfirmation)
        {
            await saving;
            Assert.Contains("Текст списка изменился", form.Markup);
            var saved = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
            using var rawDraft = JsonDocument.Parse((string)saved.Arguments[2]!);
            Assert.Equal("changed.example.invalid", rawDraft.RootElement.GetProperty("Current").GetString());
            Assert.Equal(0, sql.Dispatches);
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
            return;
        }
        dialogs.WaitForAssertion(() => Assert.Contains("Закрыть форму", dialogs.Markup));
        await Button(dialogs, "Нет").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(LatestText, sql.CurrentText);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var wire = JsonDocument.Parse((string)begin.Arguments[1]!);
        using var intent = JsonDocument.Parse(wire.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(LatestText, intent.RootElement.GetProperty("Desired").GetString());
        Assert.Equal("main", Assert.Single(wire.RootElement.GetProperty("acceptedScopes").EnumerateArray()).GetString());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Известный результат проходит cleanup и перечитывание без повторной отправки и сверки исхода.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedSaveRetriesOnlyTheUnfinishedPhase(bool failCleanup)
    {
        using var sql = new MailboxConnection { FailReadAfterDispatch = !failCleanup };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("slbl-verified", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-verified-attempt","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(LatestText);
        var failNextCommit = failCleanup;
        context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ =>
        {
            if (!failNextCommit) return false;
            failNextCommit = false;
            return true;
        }).SetException(new JSException("Тестовый отказ browser cleanup"));

        var dialogs = ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Contains("Синхронизация подтверждена", form.Markup);
        Assert.Contains(failCleanup ? "обработку черновика" : "список не перечитан", form.Markup);
        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(1, sql.ComparisonReads);
        Assert.Contains(LatestText, form.Markup);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));

        if (!failCleanup)
        {
            await Button(form, "Отмена").ClickAsync(new MouseEventArgs());
            Assert.Contains(LatestText, form.Markup);
            Assert.DoesNotContain(Baseline, form.Markup);
        }

        sql.FailReadAfterDispatch = false;
        sql.ExternalWrite("external.example.invalid");
        await Button(form, "Обновить").ClickAsync(new MouseEventArgs());

        Assert.Contains("external.example.invalid", form.Markup);
        Assert.Equal(1, sql.Dispatches);
        Assert.Equal(1, sql.ComparisonReads);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        var commits = context.JSInterop.Invocations["clayDraftStore.commitOperation"];
        Assert.Equal(failCleanup ? 2 : 1, commits.Count);
        Assert.All(commits, call => Assert.Equal(6L, call.Arguments[1]));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.unknownOperation"]);
    }

    /// <summary>Частичный результат сохраняет фактический baseline вместе с локальным текстом и intent.</summary>
    [Fact]
    public async Task PartialResultPersistsBothCanonicalBaselineAndLocalIntent()
    {
        const string canonical = "partial.example.invalid";
        using var sql = new MailboxConnection { PartialText = canonical };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("slbl-partial", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
            .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 5, 5));
        context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
            .SetResult("""{"version":1,"attemptId":"slbl-partial-attempt","kind":"slbl.sync","status":"running","revision":6,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(LatestText);

        var dialogs = ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.False(Button(form, "Сохранить").HasAttribute("disabled")));
        var saving = Button(form, "Сохранить").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("до двух минут", dialogs.Markup));
        await Button(dialogs, "Да").ClickAsync(new MouseEventArgs());
        await saving;

        Assert.Contains("Список сохранён частично", form.Markup);
        Assert.Contains(canonical, form.Markup);
        Assert.Contains(LatestText, form.Markup);
        var call = context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
        using var draft = JsonDocument.Parse((string)call.Arguments[2]!);
        Assert.Equal(canonical, draft.RootElement.GetProperty("Original").GetString());
        Assert.Equal(LatestText, draft.RootElement.GetProperty("Current").GetString());
        Assert.Equal(LatestText, draft.RootElement.GetProperty("PartialIntent").GetProperty("Desired").GetString());
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Equal(1, sql.Dispatches);
    }

    /// <summary>После расхождения baseline пользователь сам выбирает значение с защитой ревизии.</summary>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ConflictRebaseUsesExplicitChoiceAndExpectedRevision(bool keepLocal, bool revisionMatches, bool invalidRaw)
    {
        const string fresh = "fresh.example.invalid";
        var local = invalidRaw ? "<неполный>" : LatestText;
        using var sql = new MailboxConnection();
        sql.ExternalWrite(fresh);
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = JsonSerializer.Serialize(new { Address, Kind = SlBlKind.Safe, Original = Baseline, Current = OldText });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("slbl-conflict", true, 5, null, null, new Dictionary<string, string> { ["main"] = snapshot }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(7, ["data:slbl.text", "data:unrelated"]));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(revisionMatches);
        context.JSInterop.SetupModule("./js/slblDraftText.js").Setup<string?>("readText", _ => true).SetResult(local);

        ConfigureEditor(context, sql);
        var form = context.Render<SlBlForm>(p => p.Add(component => component.Id, Address + ":0"));
        form.WaitForAssertion(() => Assert.Contains("Список в базе изменился", form.Markup));

        await Button(form, "Обновить").ClickAsync(new MouseEventArgs()).WaitAsync(TimeSpan.FromSeconds(2));
        var comparison = form.Find("[aria-label='Сравнение списков']");
        Assert.Contains(Baseline, comparison.TextContent);
        Assert.Contains(local, comparison.TextContent);
        Assert.Contains(fresh, comparison.TextContent);
        Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);

        await Button(form, keepLocal ? "Оставить мой список" : "Принять список из базы").ClickAsync(new MouseEventArgs());

        var request = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        Assert.Equal("main", request.Arguments[1]);
        Assert.Equal(7L, request.Arguments[2]);
        using var rebased = JsonDocument.Parse((string)request.Arguments[3]!);
        Assert.Equal(fresh, rebased.RootElement.GetProperty("Original").GetString());
        Assert.Equal(keepLocal ? local : fresh, rebased.RootElement.GetProperty("Current").GetString());
        Assert.Equal(keepLocal ? Array.Empty<string>() : ["data:slbl.text"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(request.Arguments[4]));
        Assert.Equal(0, sql.Dispatches);
        Assert.Equal(invalidRaw ? 0 : 1, sql.ComparisonReads);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);

        if (revisionMatches)
        {
            Assert.Empty(form.FindAll("[aria-label='Сравнение списков']"));
            Assert.Equal(!keepLocal, Button(form, "Сохранить").HasAttribute("disabled"));
            Assert.Contains(keepLocal ? local : fresh, form.Find("textarea").TextContent);
        }
        else
        {
            Assert.Contains("Черновик изменился", form.Markup);
            Assert.True(Button(form, "Сохранить").HasAttribute("disabled"));
        }
    }

    private static IRenderedComponent<MudDialogProvider> ConfigureEditor(BunitContext context, MailboxConnection sql)
    {
        DapperColumnMapper.Initialize();
        context.Services.AddScoped(_ => new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => sql));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
        context.Services.AddSingleton<ISlBlMailboxWritePolicy, FixtureWritePolicy>();
        context.Services.AddMudServices();
        var dialogs = context.Render<MudDialogProvider>();
        context.Render<MudPopoverProvider>();
        return dialogs;
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == name);

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private sealed class FixtureWritePolicy : ISlBlMailboxWritePolicy
    {
        public Task<bool> AllowsAsync(Employee actor, string address, SlBlKind kind, CancellationToken ct) =>
            Task.FromResult(actor.Id == 4 && address == Address && kind == SlBlKind.Safe);
    }

    private sealed class MailboxConnection : DbConnection
    {
        internal int Dispatches { get; private set; }
        internal string CurrentText { get; private set; } = Baseline;
        internal int ComparisonReads { get; private set; }
        internal bool FailReadAfterDispatch { get; set; }
        internal string? PartialText { get; init; }
        internal void ExternalWrite(string text) => CurrentText = text;
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(this);

        private sealed class Command(MailboxConnection owner) : DbCommand
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
            public override int ExecuteNonQuery()
            {
                Assert.Equal("dbo.sp_MailboxesSlBlSynchronize", CommandText);
                owner.Dispatches++;
                owner.CurrentText = owner.PartialText ?? (string)Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "SlBl").Value!;
                return 1;
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                if (CommandText.Contains("SUSER_SID()")) return Row(("КодСотрудника", 4));
                if (CommandText.Contains("sys.parameters")) return Row(("Available", true));
                if (CommandText.Contains("SavedWords"))
                {
                    owner.ComparisonReads++;
                    var proposed = (string)Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "Text").Value!;
                    return owner.CurrentText == proposed
                        ? Row(("NotDeleted", ""), ("NotSaved", ""))
                        : Row(("NotDeleted", owner.CurrentText), ("NotSaved", proposed));
                }
                if (CommandText.Contains("AS SafeList"))
                {
                    if (owner.FailReadAfterDispatch && owner.Dispatches > 0)
                        throw new InvalidOperationException("Тестовый сбой перечитывания");
                    return Row(("SafeList", owner.CurrentText), ("BlockedList", ""));
                }
                Assert.Contains("vwMailBoxesAddresses", CommandText);
                return Row(("Address", Address));
            }

            private static DbDataReader Row(params (string Name, object Value)[] values)
            {
                var table = new DataTable();
                foreach (var (name, value) in values) table.Columns.Add(name, value.GetType());
                table.Rows.Add(values.Select(value => value.Value).ToArray());
                return table.CreateDataReader();
            }
        }
    }
}
