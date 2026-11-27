using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Сброс принимает свежую версию, сохраняя локальный ввод при отказе перечитывания.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ComputerAndSimServiceResetUseFreshVersionOnlyForExactContext(bool computer, bool freshAvailable)
    {
        var fixture = CreateFixture(computer: computer, sim: !computer);
        await using var context = fixture.Context;
        var day = new DateTime(2026, 11, 21);
        var version = day.AddHours(9);
        context.Services.AddSingleton<TimeProvider>(new EquipmentDayClock(new DateTimeOffset(day.AddHours(8), TimeSpan.Zero)));
        fixture.Service.StoredComputers.Add(new EquipmentComputerSnapshot
            { EquipmentId = 7, Date = day, RamDetail = "128", Notes = "old", ChangedAt = version.AddMinutes(-1) });
        var service = new EquipmentSimService
            { EquipmentId = 7, Id = 17, PhoneNumber = "79000000001", DateFrom = day, ServiceName = "old", ChangedAt = version.AddMinutes(-1) };
        if (freshAvailable)
        {
            fixture.Service.ComputerReset = (2, new EquipmentComputerSnapshot
                { EquipmentId = 7, Date = day, RamDetail = "512+256", Notes = "fresh", ChangedAt = version });
            fixture.Service.SimServiceReset = new EquipmentSimService
                { EquipmentId = 7, Id = 17, PhoneNumber = service.PhoneNumber, DateFrom = day, ServiceName = "fresh", ChangedAt = version };
        }
        context.JSInterop.Setup<bool>("clayDraftStore.replaceNestedEditorScope", _ => true).SetResult(true);
        var dialogs = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        if (!computer)
            SetPrivate(page.Instance, "_simCards", new List<EquipmentSimCard> { new() { EquipmentId = 7, PhoneNumber = service.PhoneNumber } });
        var added = TrackNextDialog(context);
        var opening = computer
            ? InvokeHandler(page, "OpenComputerDialogAsync")
            : InvokeHandler(page, "OpenSimServiceDialogAsync", service);
        var reference = await added.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var selector = computer ? "textarea[data-draft-key='computer.notes']" : "input[data-draft-key='sim-service.name']";
        await dialogs.InvokeAsync(() => dialogs.Find(selector).InputAsync("local"));
        var cancel = dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync());
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindComponents<ClayEditorCancelDialog>()));
        await dialogs.InvokeAsync(() => dialogs.FindComponent<ClayEditorCancelDialog>().FindAll("button")
            .Single(button => button.TextContent.Trim() == "Отменить мои изменения и загрузить из БД").ClickAsync());
        await cancel.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Service.SimWriteCalls + fixture.Service.ComputerSaveCalls);
        Assert.Equal(freshAvailable ? 1 : 0, context.JSInterop.Invocations["clayDraftStore.replaceNestedEditorScope"].Count);
        if (computer)
        {
            var editor = dialogs.FindComponent<EquipmentComputerDialog>().Instance;
            Assert.Equal(freshAvailable ? "fresh" : "local", editor.Notes);
            if (freshAvailable) Assert.Equal(new decimal?[] { 512, 256 }, editor.Ram);
        }
        else
            Assert.Equal(freshAvailable ? "fresh" : "local", dialogs.FindComponent<EquipmentSimServiceDialog>().Instance.Value.ServiceName);

        if (freshAvailable)
        {
            fixture.Service.ComputerSaveCompletion.TrySetResult();
            await dialogs.InvokeAsync(() => dialogs.Find(selector).InputAsync("after reset"));
            await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync());
            Assert.Equal(1, fixture.Service.SimWriteCalls + fixture.Service.ComputerSaveCalls);
            Assert.Equal(version, computer ? fixture.Service.StoredComputers.Last().ChangedAt : fixture.Service.SavedSimServiceVersion);
        }
        else
            await dialogs.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await opening.WaitAsync(TimeSpan.FromSeconds(4));
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal (int MemoryBanks, EquipmentComputerSnapshot? Snapshot)? ComputerReset { get; set; }
        internal EquipmentSimService? SimServiceReset { get; set; }
        internal DateTime? SavedSimServiceVersion { get; set; }

        public override Task<(int MemoryBanks, EquipmentComputerSnapshot? Snapshot)?> ReadComputerEditorResetAsync(
            int equipmentId, int employeeId, int? expectedModelId, DateTime day, bool expectedExisting, CancellationToken ct)
        {
            Assert.Equal((7, 4, true), (equipmentId, employeeId, expectedExisting));
            return Task.FromResult(ComputerReset);
        }

        public override Task<EquipmentSimService?> ReadSimServiceEditorResetAsync(int equipmentId, int serviceId,
            string openingPhone, DateTime businessDay, int employeeId, CancellationToken ct)
        {
            Assert.Equal((7, 17, "79000000001", 4), (equipmentId, serviceId, openingPhone, employeeId));
            return Task.FromResult(SimServiceReset);
        }
    }
}
