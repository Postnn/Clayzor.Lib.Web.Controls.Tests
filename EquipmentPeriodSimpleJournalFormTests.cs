using System.Globalization;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>После потери ответа перечитывает исходный период и сохраняет более поздний ввод основной карточки.</summary>
    [Theory]
    [InlineData("location-update")]
    [InlineData("issue")]
    [InlineData("location-add")]
    [InlineData("withdraw")]
    public async Task PeriodLostReplyReadsTheExactStartWithoutAnotherWrite(string mode)
    {
        var fixture = CreateFixture();
        fixture.Service.LosePeriodWriteReply = true;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.NotEmpty(context.JSInterop.Invocations["clayDraftStore.registerRam"]));
        var start = new DateTime(2026, 11, 18, 8, 30, 0, DateTimeKind.Utc);
        const string notes = "Пользовательские примечания периода";
        if (mode == "issue")
        {
            SetActiveChildDraft(page.Instance, nameof(EquipmentResponsibleDialog), "Issue:");
            await InvokeHandler(page, "SaveResponsibleDraftAsync", EquipmentResponsibleDialogMode.Issue, null,
                new EquipmentResponsibleDialogResult(8, start, null, null, notes, null));
        }
        else if (mode == "location-add")
        {
            SetActiveChildDraft(page.Instance, nameof(EquipmentLocationDialog), null);
            await InvokeHandler(page, "SaveLocationDraftAsync", null,
                new EquipmentLocationDialogResult(1005, start, null, notes, null));
        }
        else if (mode == "withdraw")
        {
            var current = new EquipmentResponsiblePeriod { EquipmentId = 7, EmployeeId = 8, FromUtc = start, ChangedAtUtc = start.AddMinutes(3) };
            SetPrivate(page.Instance, "_responsibles", new List<EquipmentResponsiblePeriod> { current });
            SetActiveChildDraft(page.Instance, nameof(EquipmentResponsibleDialog), "Withdraw:" + start.Ticks.ToString(CultureInfo.InvariantCulture));
            await InvokeHandler(page, "SaveResponsibleDraftAsync", EquipmentResponsibleDialogMode.Withdraw, current,
                new EquipmentResponsibleDialogResult(null, null, start.AddHours(1), null, notes, null)
                {
                    Original = EquipmentPeriodOriginal.FromResponsible(current, active: true),
                });
        }
        else
        {
            var location = new EquipmentLocationPeriod { EquipmentId = 7, FromUtc = start, LocationId = 3, ChangedAtUtc = start.AddHours(1) };
            SetPrivate(page.Instance, "_locations", new List<EquipmentLocationPeriod> { location });
            SetActiveChildDraft(page.Instance, nameof(EquipmentLocationDialog), start.Ticks.ToString(CultureInfo.InvariantCulture));
            await InvokeHandler(page, "SaveLocationDraftAsync", location, new EquipmentLocationDialogResult(3, start, null, notes, null)
            {
                Original = EquipmentPeriodOriginal.FromLocation(location, active: true),
            });
        }

        var attempt = fixture.Service.PeriodWriteAttempt;
        Assert.NotEqual(Guid.Empty, attempt);
        var expectedStart = EquipmentPeriodJournalWrite.NormalizeStartUtc(fixture.Service.PeriodWriteStart);
        var begin = Assert.Single(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        using var browser = JsonDocument.Parse(Assert.IsType<string>(begin.Arguments[1]));
        Assert.Equal(attempt.ToString("N"), browser.RootElement.GetProperty("attemptId").GetString());
        Assert.Single(browser.RootElement.GetProperty("acceptedScopes").EnumerateArray());
        using var data = ReadChildOperationEnvelope(context);
        var intent = data.RootElement.GetProperty("Intent");
        Assert.Equal(expectedStart.Ticks, intent.GetProperty("FromUtcTicks").GetInt64());
        Assert.Single(intent.EnumerateObject());

        fixture.Service.PeriodWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 8);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);

        FindSn(page).Input("CLAY_TEST_PERIOD_LATER");
        fixture.Service.PeriodWriteReceipt = new InventoryOperationReceipt(attempt, InventoryOperationState.Confirmed, 7);
        await InvokeHandler(page, "ReconcileDraftOperationAsync");

        Assert.Equal(attempt, fixture.Service.PeriodReadAttempt);
        Assert.Equal(expectedStart, fixture.Service.PeriodReadStart);
        Assert.Equal(1, fixture.Service.PeriodWriteCalls);
        Assert.Single(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal("CLAY_TEST_PERIOD_LATER", FindSn(page).GetAttribute("value"));
    }
}
