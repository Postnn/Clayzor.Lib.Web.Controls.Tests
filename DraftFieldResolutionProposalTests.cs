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
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class DraftFieldResolutionProposalTests
{
    [Fact]
    public async Task ExplicitFreshChoicePreservesOtherEditsAndFreshServerFields()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var original = new FormState("old title", "old note", "old category", 10);
        var local = original with { Title = "local title", Note = "local note", Version = 9 };
        var fresh = original with { Title = "server title", Category = "server category", Version = 11 };
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("conflict-test", true, 3, null,
                new Dictionary<string, string> { ["main"] = JsonSerializer.Serialize(new { Original = original, Current = local }) }));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(3, ["data:test.title:0", "data:test.note:0"]));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(true);
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton<AuthenticationStateProvider, ConflictAuthentication>();
        context.Services.AddMudServices();

        DapperColumnMapper.Initialize();
        var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Хост теста")));
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([employee, Script.Rows()]), emulateEquipmentIdentity: false);
        context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));

        var dialogs = context.Render<MudDialogProvider>();
        var editor = context.Render<ConflictCard>(p => p.Add(x => x.Fresh, fresh));
        editor.WaitForAssertion(() => Assert.False(editor.Find("#refresh").HasAttribute("disabled")));
        Assert.False(editor.Instance.WriteAllowed);
        Assert.Equal(local, editor.Instance.WorkingCopy);

        var opening = editor.Find("#refresh").ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() => Assert.Contains("server title", dialogs.Markup));
        Assert.Contains("old title", dialogs.Markup);
        Assert.Contains("local title", dialogs.Markup);
        Assert.DoesNotContain("old note", dialogs.Markup);
        Assert.True(dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Сохранить выбор").HasAttribute("disabled"));

        await dialogs.FindAll("input[type=radio]")[1].ClickAsync(new MouseEventArgs());
        var apply = dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Сохранить выбор");
        Assert.False(apply.HasAttribute("disabled"));
        await apply.ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        var merged = fresh with { Note = "local note" };
        Assert.Equal(merged, editor.Instance.WorkingCopy);
        Assert.True(editor.Instance.WriteAllowed);
        var rebase = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        Assert.Equal(3L, rebase.Arguments[2]);
        using var normalized = JsonDocument.Parse(Assert.IsType<string>(rebase.Arguments[3]));
        Assert.Equal(fresh, normalized.RootElement.GetProperty("Original").Deserialize<FormState>());
        Assert.Equal(merged, normalized.RootElement.GetProperty("Current").Deserialize<FormState>());
        Assert.Equal(new[] { "data:test.title:0" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(rebase.Arguments[4]));
    }

    public sealed class ConflictCard : TerritorialEntityCardBase<FormState>
    {
        [Parameter] public FormState Fresh { get; set; } = null!;
        internal FormState? WorkingCopy => Draft;
        internal bool WriteAllowed => CanCommit;
        protected override string FormKey => "conflict-probe";
        protected override string EntityKey => "saved";
        protected override string RouteKey => EntityKey;
        protected override bool Editable => true;
        protected override IReadOnlyList<DraftMergeField> MergeFields =>
            [new("Title", "Заголовок", "test.title"), new("Note", "Примечание", "test.note"), new("Category", "Категория")];

        protected override Task LoadAccessAsync(Employee user, CancellationToken routeToken) => Task.CompletedTask;
        protected override Task<FormState?> LoadCardAsync(CancellationToken routeToken) => Task.FromResult<FormState?>(Fresh);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "id", "refresh");
            builder.AddAttribute(2, "disabled", !CanRefresh);
            builder.AddAttribute(3, "onclick", EventCallback.Factory.Create(this, RefreshAsync));
            builder.CloseElement();
            builder.AddContent(4, Message);
        }
    }

    public sealed record FormState(string Title, string Note, string Category, int Version);

    private sealed class ConflictAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
