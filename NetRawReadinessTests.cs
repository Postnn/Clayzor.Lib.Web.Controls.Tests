using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;
using MudBlazor;
using NetPage = Kesco.App.Web.Inventory.Components.Pages.Net.Net;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class NetFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetPendingRawDisablesWriteCommands(bool failed)
    {
        using var fixture = new NetFixture(poe: 1, restoredInput: NetReadinessSnapshot(), restoredRaw: true);
        var context = fixture.Context;
        SetNetReadiness(context, failed ? new ClayDraftRestoreReadiness(false, [], ["net-input"]) :
            new ClayDraftRestoreReadiness(false, ["net-input"], []));
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForElement("[data-testid='net-input-notice']");
        Assert.True(NetReadinessButton(page, "ОК").HasAttribute("disabled"));
        Assert.True(NetReadinessButton(page, "Применить").HasAttribute("disabled"));
        Assert.True(NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
        page.WaitForAssertion(() => Assert.Contains(failed ? "Не удалось восстановить" : "ещё не завершено",
            page.Find("[data-testid='net-input-notice']").TextContent));
        await Invoke(page, "SaveAsync", false);
        await Invoke(page, "RestartPoeAsync");
        AssertNoNetRawDispatch(fixture);
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("failure")]
    [InlineData("pending")]
    public async Task NetAutomaticRetryAppliesOnlyPendingFieldsAndKeepsLiveInput(string outcome)
    {
        using var fixture = new NetFixture(poe: 1, restoredInput: NetReadinessSnapshot(), restoredRaw: true);
        var context = fixture.Context;
        SetNetReadiness(context, new(false, ["net-input"], []));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
            .SetResult(new(["data:net.connected:0"], ["data:net.description:0"]));
        var retry = context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyPendingRaw", _ => true);
        if (outcome == "failure") retry.SetException(new JSException("fixture failed scope"));

        var page = context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        await page.Find("textarea[data-draft-key='net.connected']").InputAsync("Текущий текст уже восстановленного поля");
        SetNetReadiness(context, new(true, [], []));

        if (outcome != "failure")
            retry.SetResult(outcome == "pending" ? new([], ["data:net.description:0"]) : new(["data:net.description:0"], []));
        page.WaitForAssertion(() => Assert.Equal(outcome != "applied", NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled")));
        Assert.DoesNotContain("Повторить восстановление ввода", page.Markup);

        page.WaitForAssertion(() => Assert.Equal("net-input", Assert.Single(retry.Invocations).Arguments[1]));
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal("Текущий текст уже восстановленного поля", Model(page.Instance).Connected);
        Assert.Equal(outcome != "applied", NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
        AssertNoNetRawDispatch(fixture);
    }

    [Fact]
    public async Task NetRechecksReadinessBeforeDispatch()
    {
        using var fixture = new NetFixture();
        var context = fixture.Context;
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        await page.Find("textarea[data-draft-key='net.description']").InputAsync("Изменённое описание");
        SetNetReadiness(context, new(false, [], ["net-input"]));
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));

        await Invoke(page, "SaveAsync", false);

        Assert.Single(page.FindAll("[data-testid='net-input-notice']"));
        AssertNoNetRawDispatch(fixture);
    }

    [Fact]
    public async Task NetPoeConfirmationRechecksNewPendingInputBeforeJournal()
    {
        using var fixture = new NetFixture(poe: 1);
        var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        var confirmation = Invoke(page, "RestartPoeAsync");
        dialogs.WaitForAssertion(() => Assert.Contains("Перезапустить PoE", dialogs.Markup));
        SetNetReadiness(context, new(false, ["net-input"], []));
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("КодЛицаЗаказчика", 1))));

        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Да").ClickAsync());
        await confirmation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(NetReadinessButton(page, "Перезапустить PoE").HasAttribute("disabled"));
        AssertNoNetRawDispatch(fixture);
    }

    private static string NetReadinessSnapshot() => JsonSerializer.Serialize(new
    {
        PortId = 10,
        ChangedAt = new DateTime(2026, 11, 21, 10, 0, 0),
        Original = NetInput(""),
        Current = NetInput("Сохранённый текст"),
    });

    private static object NetInput(string description) => new
    {
        CustomerId = 1, Connected = "", Description = description, HasReturnDate = false, SwitchToId = -1, ExchangeWithId = -1,
    };

    private static void SetNetReadiness(BunitContext context, ClayDraftRestoreReadiness state) =>
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true).SetResult(state);

    private static AngleSharp.Dom.IElement NetReadinessButton(IRenderedComponent<NetPage> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private static void AssertNoNetRawDispatch(NetFixture fixture)
    {
        Assert.Empty(fixture.Connection.TimeoutLog);
        Assert.DoesNotContain(fixture.Connection.CommandLog, command => command.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
    }
}
