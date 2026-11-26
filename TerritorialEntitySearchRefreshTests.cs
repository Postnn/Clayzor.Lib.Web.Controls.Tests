using System.Security.Claims;
using System.Text.Json;
using Clayzor.Lib.Web.Controls.Components.Select;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Tree;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверяет callback карточки на реальной форме поиска и дереве с подменённой БД.</summary>
public sealed class TerritorialEntitySearchRefreshTests
{
    [Fact]
    public async Task ChildSaveRefreshesLiveMetadataGridWithoutChangingParentFilter()
    {
        var (context, script, form, tree) = RenderSearch();
        await using var testContext = context;
        await form.InvokeAsync(() => tree.HandleNodeClick(tree.RootNodes.Single()));
        var originalUrl = form.Find("iframe").GetAttribute("src");
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(LookupRow("Переименованная область", 3)));
        script.Enqueue(Script.Rows(TreeRow("Переименованная область")));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));

        await form.InvokeAsync(form.Instance.RefreshFromChildAsync);

        Assert.Contains("Переименованная область", form.Find("iframe").GetAttribute("title"));
        Assert.Equal(originalUrl, form.Find("iframe").GetAttribute("src"));
        Assert.Contains("Parent=eq~5", Uri.UnescapeDataString(originalUrl!));
        Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "refreshMetadataFrame");
        Assert.Equal("Переименованная область", tree.RootNodes.Single().Text);
        Assert.Equal(["5"], ((IClayTreeView)tree).SelectedIds.ToArray());
        Assert.Empty(script);
    }

    [Fact]
    public async Task ChildRenameReloadsTheExpandedLevelAndTheUnchangedMetadataUrl()
    {
        var (context, script, form, tree) = RenderSearch(hasChildren: true);
        await using var testContext = context;
        script.Enqueue(Script.Rows(TreeRow("Район", id: 8, parentId: 5)));
        await form.InvokeAsync(() => ((IClayTreeView)tree).ExpandAsync("5"));
        await form.InvokeAsync(() => tree.HandleNodeClick(tree.RootNodes.Single()));
        var originalUrl = form.Find("iframe").GetAttribute("src");
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(LookupRow("Область", 3)));
        script.Enqueue(Script.Rows(TreeRow("Область", hasChildren: true)));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(TreeRow("Переименованный район", id: 8, parentId: 5)));

        await form.InvokeAsync(form.Instance.RefreshFromChildAsync);

        Assert.Equal("Переименованный район", tree.RootNodes.Single().Children.Single().Text);
        Assert.Contains("5", tree.ExpandedIds);
        Assert.Equal(["5"], ((IClayTreeView)tree).SelectedIds.ToArray());
        Assert.Equal(originalUrl, form.Find("iframe").GetAttribute("src"));
        var refresh = Assert.Single(context.JSInterop.Invocations, call => call.Identifier == "refreshMetadataFrame");
        Assert.Equal(originalUrl, refresh.Arguments[1]);
        Assert.Empty(script);
    }

    [Fact]
    public async Task RefreshUsesPhoneCodesWhenTheActiveTerritoryBecomesACity()
    {
        var (context, script, form, tree) = RenderSearch();
        await using var testContext = context;
        await form.InvokeAsync(() => tree.HandleNodeClick(tree.RootNodes.Single()));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(LookupRow("Город", 4)));
        script.Enqueue(Script.Rows(TreeRow("Город", type: 4)));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));

        await form.InvokeAsync(form.Instance.RefreshFromChildAsync);

        Assert.Equal("Телефонные коды в Город", form.Find("iframe").GetAttribute("title"));
        Assert.Contains("areaid=eq~5", Uri.UnescapeDataString(form.Find("iframe").GetAttribute("src")!));
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "refreshMetadataFrame");
        Assert.Empty(script);
    }

    [Fact]
    public async Task RefreshClosesThePaneWhenTheActiveTerritoryLeavesTheRootScope()
    {
        var (context, script, form, tree) = RenderSearch();
        await using var testContext = context;
        await form.InvokeAsync(() => tree.HandleNodeClick(tree.RootNodes.Single()));
        script.Enqueue(Script.Rows()); // Scoped check, before any unrestricted name/type lookup.
        script.Enqueue(Script.Rows()); // No visible roots remain.

        await form.InvokeAsync(form.Instance.RefreshFromChildAsync);

        Assert.Empty(form.FindAll("iframe"));
        Assert.Empty(tree.RootNodes);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "refreshMetadataFrame");
        Assert.Empty(script);
    }

    [Fact]
    public async Task FrameRefreshFailureIsReportedWhileTheTreeAndTitleStillRefresh()
    {
        var (context, script, form, tree) = RenderSearch(frameReady: false);
        await using var testContext = context;
        await form.InvokeAsync(() => tree.HandleNodeClick(tree.RootNodes.Single()));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(LookupRow("Новое название", 3)));
        script.Enqueue(Script.Rows(TreeRow("Новое название")));
        script.Enqueue(Script.Rows(Script.Row(("Id", 5))));

        await form.InvokeAsync(form.Instance.RefreshFromChildAsync);

        Assert.Equal("Территории в Новое название", form.Find("iframe").GetAttribute("title"));
        Assert.Equal("Новое название", tree.RootNodes.Single().Text);
        Assert.Contains("Не удалось обновить список и сведения о территории", form.Markup);
        Assert.DoesNotContain("Перенос не подтверждён", form.Markup);
        Assert.Empty(script);
    }

    /// <summary>Фактический поиск возвращает single, multiple и явно подтверждённый пустой набор.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SearchSendsExactSelectionToAcknowledgedOpener(bool multiple, bool initialChecked)
    {
        await using var opener = new BunitContext();
        opener.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var service = new ClaySelectAdvancedSearchService(opener.JSInterop.JSRuntime);
        int[]? accepted = null;
        Task<ClaySelectReturnDecision> Accept(int[] ids)
        {
            accepted = ids;
            return Task.FromResult(ClaySelectReturnDecision.Accepted);
        }

        const string url = "http://localhost/TerritorialEntity/TerritorialEntity_search";
        var selection = multiple
            ? service.OpenAcknowledgedMultipleIdsAsync(url, (ids, _) => Accept(ids))
            : service.OpenAcknowledgedAsync<TerritorialEntityLookup, int?>(url, (result, _) => Accept([result.Value!.Value]));
        var open = Assert.Single(opener.JSInterop.Invocations["claySelectAdvancedSearch.open"]);
        var requestId = Assert.IsType<string>(open.Arguments[1]);
        var query = $"&return={(multiple ? 2 : 1)}&requestId={requestId}&returnOrigin=http%3A%2F%2Flocalhost" +
            (initialChecked ? "&selectedids=5" : "");
        var (context, script, form, tree) = RenderSearch(returnQuery: query, initialChecked: initialChecked);
        await using var sender = context;
        const string method = "claySelectAdvancedSearch.returnValue";
        var returned = sender.JSInterop.Setup<bool>(method, _ => true);
        if (!multiple || initialChecked)
            script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        var submit = multiple
            ? form.FindAll("button").Single(button => button.TextContent.Trim() == "Сохранить")
                .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs())
            : form.InvokeAsync(() => tree.Options.CustomMenuItems.Single().OnExecute(tree.RootNodes.Single()));

        try
        {
            form.WaitForAssertion(() => Assert.Single(sender.JSInterop.Invocations[method]));
            var invocation = Assert.Single(sender.JSInterop.Invocations[method]);
            Assert.Equal(requestId, invocation.Arguments[0]);
            var value = invocation.Arguments[1]!;
            Assert.Null(accepted);
            Assert.False(selection.IsCompleted);
            var decision = await service.ApplyAsync(requestId, 1, JsonSerializer.SerializeToElement(value, value.GetType()));
            Assert.Equal("Accepted", decision);
            Assert.True(await selection);
            Assert.Equal(multiple && !initialChecked ? [] : new[] { 5 }, accepted);
            Assert.Empty(script);
        }
        finally
        {
            returned.SetResult(accepted is not null);
            await submit;
            await service.CancelAsync(requestId);
        }
    }

    private static (BunitContext Context, Queue<ScriptEntry> Script, IRenderedComponent<TerritorialEntitySearch> Form, ClayTreeView Tree) RenderSearch(
        bool frameReady = true, bool hasChildren = false, string? returnQuery = null, bool initialChecked = false)
    {
        var script = new Queue<ScriptEntry>();
        script.Enqueue(Script.Rows(Script.Row(("Id", 4))));
        script.Enqueue(Script.Rows());
        script.Enqueue(Script.Rows(Script.Row(("TerritoryUpdate", true))));
        if (initialChecked)
            script.Enqueue(Script.Rows(Script.Row(("Id", 5))));
        script.Enqueue(Script.Rows(TreeRow("Область", hasChildren: hasChildren)));
        var connection = new ScriptedConnection(script, emulateEquipmentIdentity: false);
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./js/territorialEntitySearch.js")
            .Setup<bool>("refreshMetadataFrame", _ => true).SetResult(frameReady);
        var errorHandler = new FakeSqlErrorHandler();
        context.Services.AddSingleton<ISqlErrorHandler>(errorHandler);
        context.Services.AddScoped(_ => new DbManager("Server=fixture", errorHandler, () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, SearchAuthentication>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddMudServices();
        context.Services.AddClayTree(new ConfigurationBuilder().Build());
        context.ComponentFactories.AddStub<InventoryMainMenu>();
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/TerritorialEntity/TerritorialEntity_search?root=5" + returnQuery);
        var form = context.Render<TerritorialEntitySearch>();
        form.WaitForAssertion(() =>
        {
            var loadedTree = form.FindComponent<ClayTreeView>().Instance;
            Assert.True(loadedTree.RootNodes.Count == 1, form.Markup);
        });
        return (context, script, form, form.FindComponent<ClayTreeView>().Instance);
    }

    private static Dictionary<string, object?> TreeRow(string name, int type = 3, int id = 5, int? parentId = null,
        bool hasChildren = false) =>
        Script.Row(("Id", id), ("Text", name), ("Type", type), ("Left", 1), ("Right", 4),
            ("ParentId", parentId), ("HasChildren", hasChildren));

    private static Dictionary<string, object?> LookupRow(string name, int type) =>
        Script.Row(("Id", 5), ("Name", name), ("Type", type));

    private sealed class SearchAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }
}
