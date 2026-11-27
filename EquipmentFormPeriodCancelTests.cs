using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс перечитывает период, а следующий Save использует новую версию.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task PeriodResetRetainsInputUnlessScopeAndServerRowMatch(bool responsible, bool acceptsRevision)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var at = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        var freshAt = at.AddDays(1);
        var location = new EquipmentLocationPeriod
        {
            EquipmentId = 7, FromUtc = at, LocationId = 3, Notes = "old", ChangedAtUtc = at,
        };
        var employee = new EquipmentResponsiblePeriod
        {
            EquipmentId = 7, FromUtc = at, EmployeeId = 4, Notes = "old", ChangedAtUtc = at,
        };
        fixture.Service.StoredLocations.Add(location);
        fixture.Service.StoredResponsibles.Add(employee);
        fixture.Service.PeriodResetResult = new(
            [new EquipmentLocationPeriod { EquipmentId = 7, FromUtc = at, LocationId = 3, Notes = "fresh", ChangedAtUtc = freshAt }],
            [new EquipmentResponsiblePeriod { EquipmentId = 7, FromUtc = at, EmployeeId = 4, Notes = "fresh", ChangedAtUtc = freshAt }]);
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(acceptsRevision);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var opened = TrackNextDialog(context);
        var opening = responsible
            ? InvokeHandler(page, "OpenResponsibleDialogAsync", EquipmentResponsibleDialogMode.Edit, employee)
            : InvokeHandler(page, "OpenLocationDialogAsync", location);
        var reference = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var noteKey = responsible ? "responsible.notes" : "location.notes";
        await dialogs.InvokeAsync(() => dialogs.Find($"textarea[data-draft-key='{noteKey}']").InputAsync("local"));
        var cancel = dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Service.PeriodWriteCalls);
        var replacement = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"]);
        Assert.Equal(21L, replacement.Arguments[2]);
        var actual = responsible
            ? dialogs.FindComponent<EquipmentResponsibleDialog>().Instance.Notes
            : dialogs.FindComponent<EquipmentLocationDialog>().Instance.Notes;
        Assert.Equal(acceptsRevision ? "fresh" : "local", actual);
        if (acceptsRevision)
        {
            await dialogs.InvokeAsync(() => dialogs.Find($"textarea[data-draft-key='{noteKey}']").InputAsync("after reset"));
            await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync());
            Assert.Equal(1, fixture.Service.PeriodWriteCalls);
            Assert.Equal(freshAt, responsible ? fixture.Service.ResponsibleUpdated?.ChangedAtUtc : fixture.Service.SavedLocationVersion);
        }
        else
            await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal PeriodEditorReset? PeriodResetResult { get; set; }
        internal DateTime? SavedLocationVersion { get; set; }
        internal EquipmentAccounting? AccountingReset { get; set; }
        internal EquipmentLicense? LicenseReset { get; set; }

        public override Task<EquipmentAccounting?> ReadAccountingEditorResetAsync(int equipmentId, int accountingId,
            int employeeId, int? openingOwnerId, DateTime? openingDate, CancellationToken ct)
        {
            Assert.Equal((7, 17, 4), (equipmentId, accountingId, employeeId));
            return Task.FromResult(AccountingReset);
        }

        public override Task<EquipmentLicense?> ReadLicenseEditorResetAsync(int equipmentId, bool expectedExisting,
            int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, true, 4), (equipmentId, expectedExisting, employeeId));
            return Task.FromResult(LicenseReset);
        }

        internal override Task<PeriodEditorReset?> ReadPeriodEditorResetAsync(int equipmentId, int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, 4), (equipmentId, employeeId));
            return Task.FromResult(PeriodResetResult);
        }
    }

    /// <summary>Учёт и лицензия применяют свежую версию только после подтверждённой замены черновика.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountingAndLicenseResetAdoptFreshVersionWithoutSaving(bool license)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var version = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        fixture.Service.AccountingReset = new() { EquipmentId = 7, Id = 17, OwnerId = 1, Notes = "fresh", ChangedAt = version };
        fixture.Service.LicenseReset = new() { EquipmentId = 7, ExpectedExisting = true, VerificationUrl = "fresh", ChangedAt = version };
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        SetPrivate(page.Instance, "_canUseAccounting", true);
        SetPrivate(page.Instance, "_canEditLicense", true);
        if (license)
            SetPrivate(page.Instance, "_licenses", new List<EquipmentLicense>
            {
                new() { EquipmentId = 7, ExpectedExisting = true, VerificationUrl = "old", ChangedAt = version.AddDays(-1) },
            });

        var opened = TrackNextDialog(context);
        var opening = license
            ? InvokeHandler(page, "OpenLicenseDialogAsync")
            : InvokeHandler(page, "OpenAccountingDialogAsync", new EquipmentAccounting { EquipmentId = 7, Id = 17, OwnerId = 1, Notes = "old" });
        var reference = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var field = license ? "input[data-draft-key='license.url']" : "textarea";
        await dialogs.InvokeAsync(() => dialogs.Find(field).InputAsync("local"));
        var cancel = dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        var replacement = Assert.Single(context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"]);
        Assert.Equal(license ? "child:EquipmentLicenseDialog:new:root" : "child:EquipmentAccountingDialog:17:root", replacement.Arguments[1]);
        if (license)
        {
            var value = dialogs.FindComponent<EquipmentLicenseDialog>().Instance.Value;
            Assert.True(value.ExpectedExisting);
            Assert.Equal("fresh", value.VerificationUrl);
            Assert.Equal(version, value.ChangedAt);
        }
        else
        {
            var value = dialogs.FindComponent<EquipmentAccountingDialog>().Instance.Value;
            Assert.Equal("fresh", value.Notes);
            Assert.Equal(version, value.ChangedAt);
        }
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }
}
