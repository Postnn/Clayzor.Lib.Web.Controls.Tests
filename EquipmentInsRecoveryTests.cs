using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentInsTests
{
    private const string RecoveryListing = "clayDraftStore.listRecoveryCandidates";
    private const string RecoverStored = "clayDraftStore.recoverListedDraft";

    [Fact]
    public async Task CopyWithLostPointerRestoresItsOwnUnknownAndDoesNotCreateAgain()
    {
        const string draftId = "420d5270-6413-4983-86c6-e805838c7984";
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>(RecoveryListing, _ => true)
            .SetResult([new(draftId, 7, true, true)]);
        var recovery = context.JSInterop.Setup<ClayDraftRestore?>(RecoverStored, _ => true);
        recovery.SetResult(new ClayDraftRestore("equipment-ins-test", true, 7, null, """
            {"version":1,"attemptId":"e26d97000ad94a298c974a2a218b29870","kind":"equipment.create",
             "status":"unknown","revision":7,"data":"{}","scopes":[],"acceptLegacyTyped":false,"legacyTypedRevision":0}
            """));
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("Исход попытки", page.Markup));
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        await InvokeSave(page, false);
        Assert.Equal(0, fixture.Service.CreateCalls);

        Assert.DoesNotContain("Открыть сохранённую попытку", page.Markup);

        Assert.Equal(draftId, Assert.Single(recovery.Invocations).Arguments[1]);
        Assert.Contains("Исход попытки", page.Markup);
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task AmbiguousRecoveryDoesNotChooseAnAttempt()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>(RecoveryListing, _ => true)
            .SetResult([
                new("420d5270-6413-4983-86c6-e805838c7984", 7, true, true),
                new("21cc4bdf-a41b-4aa2-ae64-9a69d63ed307", 8, true, true),
            ]);

        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("Есть незавершённая попытка создания", page.Markup));
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.DoesNotContain("Открыть сохранённую попытку", page.Markup);
        Assert.Empty(context.JSInterop.Invocations[RecoverStored]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task EarlierAttemptFoundBeforeBeginBlocksDispatchAndKeepsInput()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));
        await page.InvokeAsync(() => page.Find("textarea[data-draft-key='ins.notes']").InputAsync("Текущий ввод"));
        context.JSInterop.Setup<ClayDraftRecoveryCandidate[]>(RecoveryListing, _ => true)
            .SetResult([new("420d5270-6413-4983-86c6-e805838c7984", 7, true, true)]);

        await InvokeSave(page, false);

        Assert.Equal("Текущий ввод", CurrentModel(page.Instance).Description);
        Assert.Contains("Есть незавершённая попытка создания", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task FreshCreationRequiresAnExclusiveJournalAttempt()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.False(FindButton(page, "Применить").HasAttribute("disabled")));

        await InvokeSave(page, false);

        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var intent = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.True(intent.RootElement.GetProperty("blockOtherDraftOperations").GetBoolean());
        Assert.Equal(1, fixture.Service.CreateCalls);
    }
}
