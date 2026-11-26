using System.Text.Json;
using Bunit;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>При восстановлении сохранённого конфликта исходная дата списания остаётся исходной.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task WriteOffConflictSurvivesSecondRestore(bool originalMissing, bool localMissing, bool freshMissing)
    {
        var date = new DateTime(2026, 11, 21, 9, 14, 23, 654, DateTimeKind.Utc);
        DateTime? original = originalMissing ? null : date;
        DateTime? fresh = freshMissing ? null : date.AddDays(2);
        DateTime? local = localMissing ? null : date.AddDays(1).AddHours(3);
        string persisted;
        var first = CreateFixture();
        await using (first.Context)
        {
            first.Service.WriteOffDates[7] = original;
            var page = first.Context.Render<EquipmentForm>();
            WaitForSn(page, "CLAY_TEST_7");
            var originalSnapshot = (string)GetPrivate(page.Instance, "_originalSnapshot")!;
            var draft = JsonSerializer.Serialize(new
            {
                Model = OriginalEquipment(page),
                WriteOffDate = local?.Date,
                WriteOffTime = local?.TimeOfDay,
                OriginalSnapshot = originalSnapshot,
            });
            CurrentModel(page.Instance).WriteOffAtUtc = fresh;

            await InvokeHandler(page, "RestoreDraftAsync", draft);

            Assert.Contains("WriteOffAtUtc", (string)GetPrivate(page.Instance, "_mainConflictMessage")!);
            var serialize = typeof(EquipmentForm).GetMethod("SerializeDraftState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            persisted = (string)serialize.Invoke(page.Instance, null)!;
        }

        var second = CreateFixture();
        await using var context = second.Context;
        second.Service.WriteOffDates[7] = fresh;
        var reopened = context.Render<EquipmentForm>();
        WaitForSn(reopened, "CLAY_TEST_7");
        await InvokeHandler(reopened, "RestoreDraftAsync", persisted);

        Assert.NotNull(GetPrivate(reopened.Instance, "_mainConflictMessage"));
        Assert.Equal(original, OriginalEquipment(reopened).WriteOffAtUtc);
        Assert.Equal(local?.Date, GetPrivate(reopened.Instance, "_writeOffDate"));
        Assert.Equal(local?.TimeOfDay, GetPrivate(reopened.Instance, "_writeOffTime"));
        await InvokeSave(reopened, false);
        Assert.Equal(0, second.Service.UpdateCalls);

        await InvokeHandler(reopened, "ResolveMainConflict", false);

        Assert.Null(GetPrivate(reopened.Instance, "_mainConflictMessage"));
        Assert.Equal(fresh, OriginalEquipment(reopened).WriteOffAtUtc);
        Assert.Equal(fresh, CurrentModel(reopened.Instance).WriteOffAtUtc);
    }

    /// <summary>Изменение другого поля сохраняет UTC-время списания с точностью базы.</summary>
    [Fact]
    public async Task SaveOtherFieldPreservesWriteOffMilliseconds()
    {
        var fixture = CreateFixture();
        var writeOff = new DateTime(2026, 11, 21, 9, 14, 23, 654, DateTimeKind.Utc);
        fixture.Service.WriteOffDates[7] = writeOff;
        fixture.Service.WriteOffDocumentId = 9;
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        FindSn(page).Input("CLAY_TEST_INPUT");

        await InvokeSave(page, false);

        Assert.Equal(1, fixture.Service.UpdateCalls);
        Assert.Equal(writeOff, fixture.Service.Updated?.WriteOffAtUtc);
        Assert.Empty(context.JSInterop.Invocations["clayGridTimeZone.localDateTimeToUtcMilliseconds"]);
    }
}
