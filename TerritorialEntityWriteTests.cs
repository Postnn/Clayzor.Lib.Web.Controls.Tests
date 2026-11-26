using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class TerritorialEntityWriteTests
{
    private static readonly DateTime ChangedAt = new(2026, 11, 26);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateNavigatesToVerifiedReturnedKeyAfterKnownCleanup(bool failCleanup)
    {
        await using var fixture = new Fixture("id=0&parent=42",
        [
            Employee(), Script.Rows(), Access(insert: true),
            Parent(), Script.Rows(),
            Employee(), Script.Rows(), Access(insert: true),
            Script.Scalar(42),
            Script.Scalar(43), Record(43, 42),
        ]);
        fixture.Context.JSInterop.SetupVoid("clayDraftStore.commitOperation", _ => failCleanup)
            .SetException(new JSException("Тестовый отказ завершения."));
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[data-draft-key='territory.name']")));
        await page.InvokeAsync(() => page.FindComponent<ClayDropDownList<TerritorialEntityOption, int?>>()
            .Instance.ValueChanged.InvokeAsync((int)ТипыТерриитории.Страны));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.name']").InputAsync("Страна"));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.country-code']").InputAsync("1"));
        await page.InvokeAsync(() => page.Find("input[data-draft-key='territory.direction']").InputAsync("Север"));
        page.WaitForAssertion(() => Assert.False(Button(page, "Применить").HasAttribute("disabled")));
        await page.InvokeAsync(() => Button(page, "Применить").ClickAsync());

        Assert.True(fixture.Commits == 1, page.Markup + string.Join(Environment.NewLine, fixture.Connection.CommandLog));
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        var outcome = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"],
            call => Equals(call.Arguments[1], "card-result"));
        using var receipt = JsonDocument.Parse(Assert.IsType<string>(outcome.Arguments[2]));
        Assert.Equal(43, receipt.RootElement.GetProperty("ResultId").GetInt32());
        Assert.Equal(10, receipt.RootElement.GetProperty("Revision").GetInt64());

        if (failCleanup)
        {
            Assert.Contains("id=0", fixture.Navigation.Uri);
            failCleanup = false;
            await page.InvokeAsync(() => Button(page, "Обновить").ClickAsync());
        }
        Assert.Contains("id=43", fixture.Navigation.Uri);
        Assert.Single(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.refreshOpener"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReopenedCreateAcceptsOnlyTheReceiptTiedToTheConfirmedAttempt(bool matching)
    {
        var pending = """{"version":1,"attemptId":"create-ack","kind":"territory.create","status":"confirmed","revision":10,"data":"{}","scopes":[{"name":"main","typedRevision":9,"rawRevisions":{}}],"acceptLegacyTyped":false,"legacyTypedRevision":0}""";
        var receipt = JsonSerializer.Serialize(new { AttemptId = matching ? "create-ack" : "other-attempt", Revision = 10L, ResultId = 43 });
        await using var fixture = new Fixture("id=0&parent=42",
            [Employee(), Script.Rows(), Access(insert: true), Parent(), Script.Rows()],
            new ClayDraftRestore("territory-write", true, 10, null, pending,
                new Dictionary<string, string> { ["card-result"] = receipt }));
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.Contains(matching ? "Результат подтверждён" : "Обнаружена незавершённая операция", page.Markup));

        await page.InvokeAsync(() => Button(page, "Обновить").ClickAsync());

        Assert.Equal(matching, fixture.Navigation.Uri.Contains("id=43", StringComparison.Ordinal));
        Assert.Equal(matching ? 1 : 0, fixture.Context.JSInterop.Invocations["clayDraftStore.commitOperation"].Count);
        Assert.Equal(0, fixture.Commits);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteChecksOriginalThenCallsLegacyProcedureOnce()
    {
        await using var fixture = new Fixture("id=43",
        [
            Employee(), Script.Rows(), Access(delete: true), Record(43, 42), Script.Rows(),
            Employee(), Script.Rows(), Access(delete: true), Record(43, 42), Script.NonQuery(), Script.Rows(),
        ]);
        var page = fixture.Render();
        page.WaitForAssertion(() => Assert.False(Button(page, "Удалить").HasAttribute("disabled")));
        var removing = page.InvokeAsync(() => Button(page, "Удалить").ClickAsync());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Удалить территорию", fixture.Dialogs.Markup));
        await fixture.Dialogs.InvokeAsync(() => Button(fixture.Dialogs, "Да").ClickAsync());
        await removing.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.True(fixture.Commits == 1, page.Markup + string.Join(Environment.NewLine, fixture.Connection.CommandLog));
        Assert.Single(fixture.Connection.CommandLog, sql => sql == "master.dbo.sp_Delete_Территория");
        var args = Assert.Single(fixture.Connection.ParameterLog.Where(item => item.ContainsKey("КодТерриторииУдаляемой")));
        Assert.Equal(43, args["КодТерриторииУдаляемой"]);
        Assert.Equal(0, args["КодТерриторииЗамещающей"]);
        Assert.Equal(1, args["Выполнить"]);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    [Theory]
    [InlineData("denied", false)]
    [InlineData("missing-parent", false)]
    [InlineData("missing-id", true)]
    public async Task CreateFailsClosedWhenPermissionParentOrIssuedKeyCannotBeProven(string scenario, bool insert)
    {
        await using var fixture = new Fixture("", scenario switch
        {
            "denied" => [Employee(), Script.Rows(), Access()],
            "missing-parent" => [Employee(), Script.Rows(), Access(insert: true), Script.Rows()],
            _ => [Employee(), Script.Rows(), Access(insert: true), Script.Scalar(0)],
        });
        var draft = new TerritorialEntityRecord
        {
            ParentId = scenario == "missing-parent" ? 42 : null, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1", Direction = "Север",
        };
        if (scenario == "denied")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", draft));
        else
            await Assert.ThrowsAsync<TerritorialEntityRuleException>(() => TerritorialEntityData.CreateTerritoryAsync(fixture.Database, "test", draft));

        Assert.Equal(insert, fixture.Connection.CommandLog.Any(sql => sql.Contains("INSERT dbo.Территории", StringComparison.Ordinal)));
        Assert.Equal(0, fixture.Commits);
    }

    [Theory]
    [InlineData("denied", false)]
    [InlineData("stale", false)]
    [InlineData("not-deleted", true)]
    public async Task DeleteDoesNotCommitWithoutPermissionMatchingBaselineAndObservedRemoval(string scenario, bool dispatch)
    {
        await using var fixture = new Fixture("", scenario switch
        {
            "denied" => [Employee(), Script.Rows(), Access()],
            "stale" => [Employee(), Script.Rows(), Access(delete: true), Record(43, 41)],
            _ => [Employee(), Script.Rows(), Access(delete: true), Record(43, 42), Script.NonQuery(), Record(43, 42)],
        });
        var original = new TerritorialEntityRecord
        {
            Id = 43, ParentId = 42, Type = ТипыТерриитории.Страны, Name = "Страна", CountryCode = "1",
            Direction = "Север", ChangedAt = ChangedAt,
        };
        if (scenario == "denied")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => TerritorialEntityData.DeleteTerritoryAsync(fixture.Database, "test", original));
        else
            await Assert.ThrowsAsync<TerritorialEntityRuleException>(() => TerritorialEntityData.DeleteTerritoryAsync(fixture.Database, "test", original));

        Assert.Equal(dispatch, fixture.Connection.CommandLog.Any(sql => sql == "master.dbo.sp_Delete_Территория"));
        Assert.Equal(0, fixture.Commits);
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string title) where T : IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == title);

    private static ScriptEntry Employee() => Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Test")));

    private static ScriptEntry Access(bool insert = false, bool delete = false) =>
        Script.Rows(Script.Row(("TerritoryInsert", insert), ("TerritoryDelete", delete)));

    private static ScriptEntry Parent() => Script.Rows(Script.Row(("Id", 42), ("Type", 1), ("Name", "Родитель")));

    private static ScriptEntry Record(int id, int? parent) => Script.Rows(Script.Row(
        ("Id", id), ("ParentId", parent), ("Type", 2), ("Name", "Страна"), ("CountryCode", "1"),
        ("Direction", "Север"), ("ChangedAt", ChangedAt)));

    private sealed class Fixture : IAsyncDisposable
    {
        internal Fixture(string route, IEnumerable<ScriptEntry> commands, ClayDraftRestore? restore = null)
        {
            DapperColumnMapper.Initialize();
            Connection = new ScriptedConnection(new Queue<ScriptEntry>(commands), emulateEquipmentIdentity: false);
            Database = new DbManager("Server=test", new FakeSqlErrorHandler(),
                () => new TerritorialEntityCancelTests.TransactionConnection(Connection, () => Commits++));
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Context.Services.AddSingleton(Database);
            Context.Services.AddScoped(_ => new ClayDraftService(new BeginAcknowledgement(Context.JSInterop.JSRuntime)));
            Context.Services.AddScoped<ClayFormWindowService>();
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            Context.Services.AddSingleton<AuthenticationStateProvider, TrustedAuthentication>();
            Context.Services.AddMudServices();
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
                .SetResult(restore ?? new ClayDraftRestore("territory-write", false, 7, null, null));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], []));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
            Context.JSInterop.Setup<ClayDraftStorageHealth>("clayDraftStore.getHealth", _ => true)
                .SetResult(new ClayDraftStorageHealth(true, 0, null, false, 7, 7));
            Context.JSInterop.Setup<string>("clayDraftStore.beginOperationIntent", _ => true)
                .SetResult("""{"version":1,"attemptId":"placeholder","kind":"placeholder","status":"running","revision":10,"data":"{}","scopes":[{"name":"main","typedRevision":9,"rawRevisions":{}}],"acceptLegacyTyped":false,"legacyTypedRevision":0}""");
            Dialogs = Context.Render<MudDialogProvider>();
            Context.Render<MudPopoverProvider>();
            Navigation = Context.Services.GetRequiredService<NavigationManager>();
            Navigation.NavigateTo($"/TerritorialEntity/TerritorialEntity_form?{route}");
        }

        internal BunitContext Context { get; } = new();
        internal ScriptedConnection Connection { get; }
        internal DbManager Database { get; }
        internal NavigationManager Navigation { get; }
        internal IRenderedComponent<MudDialogProvider> Dialogs { get; }
        internal int Commits { get; private set; }

        internal IRenderedComponent<TerritorialEntityForm> Render() => Context.Render<TerritorialEntityForm>();

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class BeginAcknowledgement(IJSRuntime inner) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            InvokeAsync<T>(identifier, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var value = await inner.InvokeAsync<T>(identifier, cancellationToken, args);
            if (identifier != "clayDraftStore.beginOperationIntent" || value is not string json) return value;
            using var input = JsonDocument.Parse((string)args![1]!);
            var result = JsonNode.Parse(json)!;
            foreach (var name in new[] { "attemptId", "kind", "data" })
                result[name] = input.RootElement.GetProperty(name).GetString();
            return (T)(object)result.ToJsonString();
        }
    }

    private sealed class TrustedAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
