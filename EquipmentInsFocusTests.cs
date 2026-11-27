using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentInsTests
{
    private const string FocusModulePath = "./_content/Clayzor.Lib.Web.Controls/js/clayEditorFocus.js";

    [Theory]
    [InlineData("ready")]
    [InlineData("pending-raw")]
    [InlineData("read-only")]
    public async Task CreationFocusScopeMirrorsEditorReadiness(string state)
    {
        const string uri = "http://localhost/Equipment/EquipmentIns?copyId=7";
        var pending = state == "pending-raw";
        var fixture = CreateFixture(uri, restoredCreation: pending ? EditorRestore(await CaptureEditorDraftAsync(uri)) : null,
            pendingEditorRaw: pending);
        fixture.Service.AllowCreate = state != "read-only";
        await using var context = fixture.Context;
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.JSInterop.SetupModule(FocusModulePath).SetupVoid("update", invocation =>
        {
            if (Equals(invocation.Arguments[1], state == "ready"))
                updated.TrySetResult();
            return true;
        }).SetVoidResult();
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() =>
        {
            var scope = page.FindComponent<ClayEditorFocusScope>();
            Assert.Equal(state == "ready", scope.Instance.Ready);
            Assert.True(scope.Instance.AdvanceOnEnter);
            Assert.NotNull(scope.Find(".equipment-ins-fields"));
            Assert.DoesNotContain(scope.FindAll("button"), button => button.TextContent.Trim() is "OK" or "Применить");
        });
        if (pending)
            page.WaitForElement("[data-testid='ins-input-readiness']");
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task RouteChangeOpensANewFocusScope()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        context.JSInterop.SetupModule(FocusModulePath);
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.True(page.FindComponent<ClayEditorFocusScope>().Instance.Ready));
        var first = page.FindComponent<ClayEditorFocusScope>().Instance;
        await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentIns?copyId=8"));
        page.WaitForAssertion(() =>
        {
            var next = page.FindComponent<ClayEditorFocusScope>().Instance;
            Assert.NotSame(first, next);
            Assert.True(next.Ready);
            Assert.Equal("Copy8", CurrentModel(page.Instance).Description);
        });
        Assert.NotEmpty(context.JSInterop.Invocations["detach"]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }

    [Fact]
    public async Task AcceptedLocationChoiceRequestsAdvanceAfterBinding()
    {
        var fixture = CreateFixture("http://localhost/Equipment/EquipmentIns?copyId=7");
        await using var context = fixture.Context;
        var module = context.JSInterop.SetupModule(FocusModulePath);
        module.Setup<long?>("captureSelectionAdvance", _ => true).SetResult(17);
        var page = context.Render<EquipmentIns>();
        page.WaitForAssertion(() => Assert.True(page.FindComponent<ClayEditorFocusScope>().Instance.Ready));
        var location = page.FindComponent<ClaySelect<LocationLookup, int?>>();
        Assert.True(location.Instance.Options!.AdvanceFocusOnSelection);
        Assert.True(page.FindComponent<ClaySelect<EquipmentType, int?>>().Instance.Options!.AdvanceFocusOnSelection);
        Assert.True(page.FindComponent<ClaySelect<Employee, int?>>().Instance.Options!.AdvanceFocusOnSelection);
        var persons = page.FindComponents<ClaySelect<PersonLookup, int?>>();
        Assert.True(persons.Single(control => control.Instance.Options!.DraftKey == "ins.shipper").Instance.Options!.AdvanceFocusOnSelection);
        Assert.False(persons.Single(control => control.Instance.Options!.DraftKey == "ins.owner").Instance.Options!.AdvanceFocusOnSelection);
        Assert.False(page.FindComponent<ClaySelect<EquipmentModel, int?>>().Instance.Options!.AdvanceFocusOnSelection);
        await location.Find("input").InputAsync("Room");
        await location.Find("button.clay-select-button").ClickAsync();
        await location.Find("[role='option']").ClickAsync();

        Assert.Equal(1, CurrentModel(page.Instance).LocationId);
        var transfer = Assert.Single(context.JSInterop.Invocations["completeSelectionAdvance"]);
        Assert.Equal(17L, transfer.Arguments[1]);
        Assert.Equal(0, fixture.Service.CreateCalls);
    }
}
