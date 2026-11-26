using Clayzor.Lib.Entities;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string ContinueMainEditing = "Продолжить редактирование";
    private const string CloseMainWithoutSaving = "Закрыть без сохранения";
    private const string ResetMainAndStay = "Отменить мои изменения и загрузить из БД";
    private const string ReplaceMainScope = "clayDraftStore.replaceEditorScope";

    [Fact]
    public async Task MainCancel_ContinueKeepsTypedInputWithoutReadingOrReplacing()
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_LOCAL"));

        await ChooseMainCancelAsync(page, dialogs, ContinueMainEditing);

        WaitForSn(page, "CLAY_TEST_LOCAL");
        Assert.Single(fixture.Service.Requests);
        Assert.Empty(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    [Fact]
    public async Task MainCancel_RawOnlyClosesByReplacingExactMainScopeWithoutResaving()
    {
        var fixture = CreateFixture(mainScope: new ClayDraftEditorScopeState(21, true, false));
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");

        await ChooseMainCancelAsync(page, dialogs, CloseMainWithoutSaving);

        var replace = Assert.Single(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Equal("test-registration", replace.Arguments[0]);
        Assert.Equal("main", replace.Arguments[1]);
        Assert.Equal(21L, replace.Arguments[2]);
        Assert.Null(replace.Arguments[3]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        await InvokeHandler(page, "PersistDraftAsync");
        var calls = context.JSInterop.Invocations.ToList();
        var replaceIndex = calls.FindIndex(call => call.Identifier == ReplaceMainScope);
        Assert.DoesNotContain(calls.Skip(replaceIndex + 1), call => call.Identifier == "clayDraftStore.saveTyped");
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MainCancel_ResetReadsFreshBeforeCasAndPublishesOnlyIfAccepted(bool acceptRevision)
    {
        var fixture = CreateFixture(mainScope: new ClayDraftEditorScopeState(21, true, false), acceptMainReplacement: acceptRevision);
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_LOCAL"));
        fixture.Service.NextModel = new EquipmentEntity { Id = 7, SerialNumber = "CLAY_TEST_FRESH", OwnerId = 1 };

        await ChooseMainCancelAsync(page, dialogs, ResetMainAndStay);

        Assert.Equal(new[] { 7, 7 }, fixture.Service.Requests);
        var replace = Assert.Single(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Equal(21L, replace.Arguments[2]);
        Assert.Contains("CLAY_TEST_FRESH", Assert.IsType<string>(replace.Arguments[3]));
        WaitForSn(page, acceptRevision ? "CLAY_TEST_FRESH" : "CLAY_TEST_LOCAL");
        if (!acceptRevision)
            Assert.Contains("Черновик изменился во время подтверждения", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("role")]
    [InlineData("batch")]
    public async Task MainCancel_FailedFreshReadKeepsInputAndNeverCallsCas(string failure)
    {
        var fixture = CreateFixture(mainScope: new ClayDraftEditorScopeState(21, true, false));
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_LOCAL"));
        if (failure == "missing")
            fixture.Service.NextModel = new EquipmentEntity { Id = 8, SerialNumber = "CLAY_TEST_WRONG" };
        else if (failure == "role")
            fixture.Service.Roles.Remove((int)Роли.Специалист_по_инвентаризации_оборудования);
        else
            fixture.Service.DetailsException = SqlExceptionFactory.Create(53, "details during reset");

        await ChooseMainCancelAsync(page, dialogs, ResetMainAndStay);

        WaitForSn(page, "CLAY_TEST_LOCAL");
        Assert.Contains("Черновик сохранён.", page.Markup);
        Assert.Empty(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Empty(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task MainCancel_OperationInBrowserKeepsJournalAndLocalInputOnClose()
    {
        var fixture = CreateFixture(mainScope: new ClayDraftEditorScopeState(21, true, true));
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_LOCAL"));

        await ChooseMainCancelAsync(page, dialogs, CloseMainWithoutSaving);

        Assert.Empty(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.Single(context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    [Fact]
    public async Task MainCancel_TypedInputArrivingDuringFreshReadKeepsTheNewerValue()
    {
        var fixture = CreateFixture(mainScope: new ClayDraftEditorScopeState(21, true, false));
        await using var context = fixture.Context;
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_FIRST"));
        fixture.Service.DeferredInitialLoad = true;

        var action = InvokeHandler(page, "CancelAsync");
        dialogs.WaitForAssertion(() => Assert.Contains(ResetMainAndStay, dialogs.Markup));
        await ClickMainDialogButtonAsync(dialogs, ResetMainAndStay);
        fixture.Service.WaitForGetRequest();
        await page.InvokeAsync(() => FindSn(page).Input("CLAY_TEST_LATER"));
        fixture.Service.InitialLoadCompletion.SetResult();
        await action.WaitAsync(TimeSpan.FromSeconds(4));

        WaitForSn(page, "CLAY_TEST_LATER");
        Assert.Empty(context.JSInterop.Invocations[ReplaceMainScope]);
        Assert.Contains("Черновик изменился во время подтверждения", page.Markup);
        Assert.Equal(0, fixture.Service.UpdateCalls);
    }

    private static async Task ChooseMainCancelAsync(IRenderedComponent<EquipmentForm> page,
        IRenderedComponent<MudDialogProvider> dialogs, string choice, string handler = "CancelAsync")
    {
        var action = InvokeHandler(page, handler);
        dialogs.WaitForAssertion(() => Assert.Contains(choice, dialogs.Markup));
        await ClickMainDialogButtonAsync(dialogs, choice);
        await action.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private static Task ClickMainDialogButtonAsync(IRenderedComponent<MudDialogProvider> dialogs, string choice) =>
        dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == choice).ClickAsync());
}
