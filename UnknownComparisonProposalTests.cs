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

public sealed class UnknownComparisonProposalTests
{
    [Theory]
    [InlineData("unknown", false)]
    [InlineData("running", true)]
    public async Task RefreshComparesImmutableAttemptAndKeepsOutcomeBlocked(string status, bool missingAtLoad)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var operation = JsonSerializer.Serialize(new
        {
            version = 1, kind = "update", status, revision = 2, attemptId = "operation-42",
            data = """{"Original":"before","Current":"sent"}""",
            scopes = Array.Empty<object>(), acceptLegacyTyped = false, legacyTypedRevision = 0,
        });
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("unknown-test", true, 3, null, operation,
                new Dictionary<string, string> { ["main"] = """{"Original":"before","Current":"newer local edit"}""" }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, ComparisonAuthentication>();
        context.Services.AddMudServices();

        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста")));
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([employee, Script.Rows(), employee, Script.Rows()]),
            emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var editor = context.Render<ComparisonCard>(p => p.Add(x => x.MissingAtLoad, missingAtLoad));
        editor.WaitForAssertion(() => Assert.False(editor.Find("#refresh").HasAttribute("disabled")));
        Assert.Equal("newer local edit", editor.Find("output").TextContent);
        Assert.False(editor.Instance.WriteAllowed);

        await editor.Find("#refresh").ClickAsync(new MouseEventArgs());

        Assert.Equal("sent", editor.Instance.ComparedInput);
        Assert.Equal(1, editor.Instance.ComparisonCount);
        Assert.Contains("Данные в базе совпадают с отправленными.", editor.Markup);
        Assert.Contains("operation-42", editor.Markup);
        Assert.Equal("newer local edit", editor.Find("output").TextContent);
        Assert.False(editor.Instance.WriteAllowed);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rejectOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
    }

    public sealed class ComparisonCard : TerritorialEntityCardBase<string>
    {
        [Parameter] public bool MissingAtLoad { get; set; }
        internal string? ComparedInput { get; private set; }
        internal int ComparisonCount { get; private set; }
        internal bool WriteAllowed => CanCommit;

        protected override string FormKey => "unknown-probe";
        protected override string EntityKey => "saved";
        protected override string RouteKey => EntityKey;
        protected override bool Editable => !MissingAtLoad;
        protected override bool CanReadMissingDraft => MissingAtLoad;
        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<string?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult(MissingAtLoad ? null : "before");

        protected override Task<CompareRead> ReadForComparisonAsync(string proposed, Employee actor, CancellationToken token)
        {
            ComparedInput = proposed;
            ComparisonCount++;
            return Task.FromResult(new CompareRead("sent"));
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "output");
            builder.AddContent(1, Draft);
            builder.CloseElement();
            builder.OpenElement(2, "button");
            builder.AddAttribute(3, "id", "refresh");
            builder.AddAttribute(4, "disabled", !CanRefresh);
            builder.AddAttribute(5, "onclick", EventCallback.Factory.Create(this, RefreshAsync));
            builder.CloseElement();
            builder.AddContent(6, Message);
        }
    }

    private sealed class ComparisonAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
