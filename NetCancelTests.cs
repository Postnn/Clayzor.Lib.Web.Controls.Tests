using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using NetPage = Kesco.App.Web.Inventory.Components.Pages.Net.Net;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class NetFormTests
{
    [Theory]
    [InlineData("continue")]
    [InlineData("close")]
    [InlineData("reset")]
    public async Task NetCancelRawOnlyHandlesThreeChoices(string choice)
    {
        using var fixture = new NetFixture(description: "Original");
        var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        InstallNetCancelScope(context);
        fixture.AddLoad(description: "From DB", changedAt: new DateTime(2026, 11, 26, 11, 0, 0));
        var cancelling = page.InvokeAsync(() => NetCancelButton(page).ClickAsync());
        var action = choice == "continue" ? "Продолжить редактирование" :
            choice == "close" ? "Закрыть без сохранения" : "Отменить мои изменения и загрузить из БД";
        await PickNetCancelAsync(dialogs, action);
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(choice == "reset" ? "From DB" : "Original", Model(page.Instance).Description);
        Assert.Equal(choice == "close" ? 1 : 0, context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Equal(choice == "close" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        if (choice == "reset")
        {
            var replace = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal("net-input", replace.Arguments[1]);
            Assert.Equal(23L, replace.Arguments[2]);
            using var input = JsonDocument.Parse(Assert.IsType<string>(replace.Arguments[3]));
            Assert.Equal("2026-11-26T11:00:00", input.RootElement.GetProperty("ChangedAt").GetString());
            Assert.Equal("From DB", input.RootElement.GetProperty("Original").GetProperty("Description").GetString());
        }
        else Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        AssertNoNetCancelWrite(fixture);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("equipment")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("late")]
    public async Task NetCancelResetKeepsTypedInputUntilAcceptedFreshContext(string mode)
    {
        using var fixture = new NetFixture(description: "Original");
        var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        await page.Find("textarea[data-draft-key='net.description']").InputAsync("Local typed");
        page.WaitForAssertion(() => Assert.Equal("Local typed", Model(page.Instance).Description));
        InstallNetCancelScope(context, mode != "late");
        if (mode is "missing" or "denied")
        {
            fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."))));
            fixture.Enqueue(mode == "missing" ? Script.Rows() : Script.Error(SqlExceptionFactory.Create(229, "fixture denied")));
        }
        else
            fixture.AddLoad(description: "From DB", actorId: mode == "actor" ? 9 : 4,
                equipmentId: mode == "equipment" ? 9 : 8);

        var cancelling = page.InvokeAsync(() => NetCancelButton(page).ClickAsync());
        await PickNetCancelAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Local typed", Model(page.Instance).Description);
        Assert.Equal(mode == "late" ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        AssertNoNetCancelWrite(fixture);
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData("confirmed", true)]
    public async Task NetCancelKeepsOutstandingPoeOutcome(string status, bool closes)
    {
        using var fixture = new NetFixture(poe: 1, restoredOperation: OperationJson("net.poe", status));
        var page = fixture.Context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='net-input-notice']")));
        await page.InvokeAsync(() => NetCancelButton(page).ClickAsync());

        Assert.Equal(closes ? 1 : 0, fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.DoesNotContain(fixture.Context.JSInterop.Invocations, call => call.Identifier is
            "clayDraftStore.replaceEditorScope" or "clayDraftStore.commitOperation" or "clayDraftStore.discardScope");
        AssertNoNetCancelWrite(fixture);
    }

    [Fact]
    public async Task NetCancelResetDropsDelayedFreshReadAfterRouteChange()
    {
        using var fixture = new NetFixture(description: "Port10");
        var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        InstallNetCancelScope(context);
        var auth = (FakeAuthenticationStateProvider)context.Services.GetRequiredService<AuthenticationStateProvider>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.BeforeRead = () =>
        {
            if (!entered.TrySetResult()) return Task.CompletedTask;
            return release.Task;
        };
        fixture.AddLoad(id: 11, description: "Port11");
        var cancelling = page.InvokeAsync(() => NetCancelButton(page).ClickAsync());
        await PickNetCancelAsync(dialogs, "Отменить мои изменения и загрузить из БД");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Net/Net?id=11"));
            page.WaitForAssertion(() => Assert.Equal(11, page.Instance.Id));
        }
        finally { release.TrySetResult(); }
        await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => Assert.Equal("Port11", Model(page.Instance).Description));

        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        AssertNoNetCancelWrite(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetInternalNavigationUsesTheSameRawScopeGuard(bool discard)
    {
        using var fixture = new NetFixture(description: "Official");
        var context = fixture.Context;
        const string uri = "http://localhost/Net/Net?id=10";
        var navigation = new NetGuardNavigation(uri);
        context.Services.AddSingleton<NavigationManager>(navigation);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        InstallNetCancelScope(context);

        var navigating = page.InvokeAsync(() => navigation.CanNavigateAsync("http://localhost/Net/Net?id=11"));
        await PickNetCancelAsync(dialogs, discard ? "Закрыть без сохранения" : "Продолжить редактирование");

        Assert.Equal(discard, await navigating.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(discard ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Equal(uri, navigation.Uri);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        AssertNoNetCancelWrite(fixture);
    }

    [Fact]
    public async Task UnavailablePortClosesWithoutOpeningPriorRegistration()
    {
        using var fixture = new NetFixture(missingPort: true, restoredRaw: true);
        var context = fixture.Context;
        var page = context.Render<NetPage>();
        page.WaitForAssertion(() => Assert.Contains("не найден", page.Markup));

        await page.InvokeAsync(() => NetCancelButton(page).ClickAsync());

        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discardScope"]);
        AssertNoNetCancelWrite(fixture);
    }

    [Fact]
    public async Task FailedPhysicalCloseRetriesTheSameHandleAndRevisionWithoutDml()
    {
        using var fixture = new NetFixture(description: "Исходное значение");
        var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        InstallNetCancelScope(context);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true)
            .SetException(new JSException("fixture close failure"));

        var closing = page.InvokeAsync(() => NetCancelButton(page).ClickAsync());
        await PickNetCancelAsync(dialogs, "Закрыть без сохранения");
        await closing.WaitAsync(TimeSpan.FromSeconds(5));

        var first = Assert.Single(context.JSInterop.Invocations["clayDraftStore.closeRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Contains("Не удалось подтвердить удаление", page.Markup);
        context.JSInterop.Setup<bool>("clayDraftStore.closeRam", _ => true).SetResult(true);
        await page.InvokeAsync(() => NetCancelButton(page).ClickAsync());

        Assert.Equal(2, context.JSInterop.Invocations["clayDraftStore.closeRam"].Count);
        Assert.Equal(first.Arguments, context.JSInterop.Invocations["clayDraftStore.closeRam"].Last().Arguments);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        AssertNoNetCancelWrite(fixture);
    }

    private sealed class NetGuardNavigation : NavigationManager
    {
        public NetGuardNavigation(string uri) => Initialize("http://localhost/", uri);

        protected override void SetNavigationLockState(bool value) { }

        public Task<bool> CanNavigateAsync(string target) =>
            NotifyLocationChangingAsync(target, null, false).AsTask();
    }

    private static void InstallNetCancelScope(BunitContext context, bool acceptRevision = true)
    {
        context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
            .SetResult(new ClayDraftEditorScopeState(23, true, false));
        context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(acceptRevision);
    }

    private static AngleSharp.Dom.IElement NetCancelButton(IRenderedComponent<NetPage> page) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена");

    private static Task PickNetCancelAsync(IRenderedComponent<MudDialogProvider> dialogs, string action)
    {
        dialogs.WaitForAssertion(() => Assert.Contains(action, dialogs.Markup));
        return dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == action).ClickAsync());
    }

    private static void AssertNoNetCancelWrite(NetFixture fixture)
    {
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
    }
}
