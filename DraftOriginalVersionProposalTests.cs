using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class DraftOriginalVersionProposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditOnlyConflictAndMissingLegacyVersionSurviveAnotherEdit(bool legacy)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var original = legacy
            ? """{"Original":"visible database value","Current":"first local edit"}"""
            : """{"Original":"visible database value","Current":"first local edit","Version":{"Key":"001","ChangedBy":4,"ChangedByName":"User","ChangedAt":"2026-11-20T08:00:00Z"}}""";
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("version-test", true, 3, null, null,
                new Dictionary<string, string> { ["main"] = original }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));

        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, VersionAuthentication>();
        context.Services.AddMudServices();
        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста")));
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([employee, Script.Rows()]), emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var editor = context.Render<VersionCard>();
        editor.WaitForAssertion(() => Assert.Contains("Карточка изменилась", editor.Markup));
        Assert.False(editor.Instance.WriteAllowed);
        Assert.Contains("first local edit", editor.Markup);

        await editor.Find("button").ClickAsync(new MouseEventArgs());
        Assert.False(editor.Instance.WriteAllowed);
        var persist = Assert.Single(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        using var saved = JsonDocument.Parse(Assert.IsType<string>(persist.Arguments[2]));
        Assert.Equal("visible database value", saved.RootElement.GetProperty("Original").GetString());
        Assert.Equal("new local edit", saved.RootElement.GetProperty("Current").GetString());
        var version = saved.RootElement.GetProperty("Version");
        if (legacy)
            Assert.Equal(JsonValueKind.Null, version.ValueKind);
        else
            Assert.Equal(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), version.GetProperty("ChangedAt").GetDateTime());
    }

    public sealed class VersionCard : TerritorialEntityCardBase<string>
    {
        internal bool WriteAllowed => CanCommit;
        protected override string FormKey => "version-probe";
        protected override string EntityKey => "001";
        protected override string RouteKey => EntityKey;
        protected override bool Editable => true;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<string?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult<string?>("visible database value");
        protected override DraftRowVersion? ReadDraftRowVersion() =>
            new("001", 4, "User", new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => UpdateDraftAsync("new local edit")));
            builder.CloseElement();
            builder.AddContent(2, Message);
            builder.AddContent(3, Draft);
        }
    }

    private sealed class VersionAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
