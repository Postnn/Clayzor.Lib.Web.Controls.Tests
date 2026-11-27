using System.Text.Json;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentInsTests
{
    [Fact]
    public async Task LegacyEvidenceAcrossSourcesBlocksWithoutRestoringEarlierInput()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([new(firstId.ToString("N"), "equipment.create", "7:0"),
                new(secondId.ToString("N"), "equipment.create", "0:-2147483648")]);
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("previous-input", true, 7, null, null));
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("В серверном журнале есть незавершённое создание", page.Markup));
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        await InvokeSave(page, false);
        Assert.Equal(0, fixture.Service.CreateCalls);

        Assert.True(fixture.Service.ImportedCreationAttempts.SetEquals([firstId, secondId]));
        Assert.Equal("new", Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginLegacyOperationEvidenceTransfer"]).Arguments[1]);
        Assert.Equal("7:0", Assert.Single(context.JSInterop.Invocations["clayDraftStore.registerRam"]).Arguments[1]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recoverListedDraft"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);
    }

    [Fact]
    public async Task InvalidLegacyCreationSourcePreventsRamRegistration()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayLegacyDraftOperationEvidence[]?>("clayDraftStore.beginLegacyOperationEvidenceTransfer", _ => true)
            .SetResult([new(Guid.NewGuid().ToString("N"), "equipment.create", "7:+0")]);

        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.Contains("Не удалось проверить защищённый черновик", page.Markup));
        Assert.True(FindButton(page, "Применить").HasAttribute("disabled"));
        Assert.DoesNotContain("Открыть сохранённую попытку", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(fixture.Service.ImportedCreationAttempts);
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
        fixture.Service.ServerCreationPending = true;

        await InvokeSave(page, false);

        Assert.Equal("Текущий ввод", CurrentModel(page.Instance).Description);
        Assert.Contains("В серверном журнале есть незавершённое создание", page.Markup);
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
