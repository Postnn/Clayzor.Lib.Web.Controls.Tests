using System.Data;
using System.Reflection;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private const string PriorMainSaveText = "Предыдущее сохранение оборудования подтверждено сервером.";

    /// <summary>Серверный запрос истории ограничивает сотрудника, оборудование и два вида сохранения.</summary>
    [Fact]
    public async Task LatestMainSaveQueryBindsEmployeeEquipmentAndBothCommandKinds()
    {
        var attemptId = Guid.NewGuid();
        var commands = new Queue<ScriptEntry>(
        [
            new ScriptEntry
            {
                ReaderFactory = () =>
                {
                    var row = new DataTable();
                    row.Columns.Add("AttemptId", typeof(Guid));
                    row.Columns.Add("EmployeeId", typeof(int));
                    row.Columns.Add("ConflictKey", typeof(string));
                    row.Columns.Add("CommandKind", typeof(string));
                    row.Columns.Add("State", typeof(byte));
                    row.Columns.Add("ResultEntityId", typeof(int));
                    row.Rows.Add(attemptId, 4, "equipment/7", "equipment-phone-save", (byte)InventoryOperationState.Confirmed, 7);
                    return row.CreateDataReader();
                },
            },
        ]);
        var connection = new ScriptedConnection(commands, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var result = await InventoryOperationJournal.ReadLatestAsync(db, new Employee { Id = 4 }, "equipment/7",
            new[] { "equipment-save", "equipment-phone-save" }, default);

        Assert.Equal(new InventoryOperationReceipt(attemptId, InventoryOperationState.Confirmed, 7), result);
        var sql = Assert.Single(connection.CommandLog);
        Assert.Contains("WHERE EmployeeId = @EmployeeId AND ConflictKey = @ConflictKey AND CommandKind IN (", sql);
        var parameters = Assert.Single(connection.ParameterLog);
        Assert.Equal(4, parameters["EmployeeId"]);
        Assert.Equal("equipment/7", parameters["ConflictKey"]);
        Assert.Equal("equipment-save", parameters["CommandKinds1"]);
        Assert.Equal("equipment-phone-save", parameters["CommandKinds2"]);
    }

    /// <summary>Отдельная история появляется только для подтверждённого результата этой записи.</summary>
    [Theory]
    [InlineData("confirmed")]
    [InlineData("wrong-result")]
    [InlineData("rejected")]
    [InlineData("unknown-receipt")]
    [InlineData("pending")]
    [InlineData("read-only")]
    public async Task EquipmentOwnLatestMainSaveIsReadOnlyAndRequiresAnExactResult(string scenario)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var state = scenario switch
        {
            "rejected" => InventoryOperationState.Rejected,
            "unknown-receipt" => InventoryOperationState.Unknown,
            _ => InventoryOperationState.Confirmed,
        };
        var receipt = new InventoryOperationReceipt(Guid.NewGuid(), state,
            scenario == "wrong-result" ? 8 : 7);
        fixture.Service.LatestMainWriteReceiptRead = (_, _, _) => Task.FromResult<InventoryOperationReceipt?>(receipt);
        if (scenario == "pending")
            fixture.Service.LegacyEquipmentPending = true;
        if (scenario == "read-only")
            fixture.Service.Roles.Remove(43);

        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        if (scenario is "pending" or "read-only")
        {
            if (scenario == "pending")
                page.WaitForAssertion(() => Assert.True(PrivateBool(page.Instance, "_mainServerWritePending")));
            else
                Assert.False(PrivateBool(page.Instance, "_canEdit"));
            Assert.Empty(fixture.Service.LatestMainWriteReceiptRequests);
        }
        else
        {
            page.WaitForAssertion(() => Assert.Equal((7, 4), Assert.Single(fixture.Service.LatestMainWriteReceiptRequests)));
            if (scenario == "confirmed")
                page.WaitForAssertion(() => Assert.Contains(PriorMainSaveText, page.Markup));
        }

        if (scenario != "confirmed")
            Assert.DoesNotContain(PriorMainSaveText, page.Markup);
        Assert.Equal("CLAY_TEST_7", CurrentModel(page.Instance).SerialNumber);
        Assert.Equal("CLAY_TEST_7", OriginalEquipment(page).SerialNumber);
        Assert.Null(GetPrivate(page.Instance, "_draftOperation"));
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Equal(0, fixture.Service.UpdateWithPhonesCalls);
        Assert.Equal(0, fixture.Service.SyncCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.recordConfirmedOperation"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
    }

    /// <summary>Поздний ответ относится только к исходному маршруту и сотруднику.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EquipmentLateOwnLatestMainSaveCannotPublishAcrossRouteOrEmployeeChange(bool routeChanges)
    {
        var fixture = CreateFixture();
        await using var context = fixture.Context;
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        page.WaitForAssertion(() => Assert.Single(fixture.Service.LatestMainWriteReceiptRequests));
        var registration = Assert.IsType<ClayDraftRegistration>(GetPrivate(page.Instance, "_draftRegistration"));
        var version = Assert.IsType<int>(GetPrivate(page.Instance, "_operationVersion"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<InventoryOperationReceipt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.LatestMainWriteReceiptRead = (id, _, _) =>
        {
            if (id != 7)
                return Task.FromResult<InventoryOperationReceipt?>(null);
            started.TrySetResult();
            return response.Task;
        };

        var oldRead = InvokeHandler(page, "ReadPriorMainSaveAsync", registration, 7, 4, version);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (routeChanges)
        {
            await page.InvokeAsync(() => fixture.Navigation.NavigateTo("http://localhost/Equipment/EquipmentForm?id=8"));
            WaitForSn(page, "CLAY_TEST_8");
        }
        else
        {
            await page.InvokeAsync(() => typeof(EquipmentForm).GetProperty("CurrentUser",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page.Instance, new Employee { Id = 9 }));
        }

        response.SetResult(new InventoryOperationReceipt(Guid.NewGuid(), InventoryOperationState.Confirmed, 7));
        await oldRead;
        Assert.False(PrivateBool(page.Instance, "_priorMainSaveConfirmed"));
        Assert.DoesNotContain(PriorMainSaveText, page.Markup);
        Assert.Equal(routeChanges ? 8 : 7, CurrentModel(page.Instance).Id);
        Assert.Equal(0, fixture.Service.UpdateCalls);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
    }

    private sealed partial class FakeEquipmentFormService
    {
        internal List<(int EquipmentId, int EmployeeId)> LatestMainWriteReceiptRequests { get; } = [];
        internal Func<int, int, CancellationToken, Task<InventoryOperationReceipt?>>? LatestMainWriteReceiptRead { get; set; }

        public override Task<InventoryOperationReceipt?> ReadLatestMainWriteReceiptAsync(int equipmentId,
            int employeeId, CancellationToken ct)
        {
            LatestMainWriteReceiptRequests.Add((equipmentId, employeeId));
            return LatestMainWriteReceiptRead?.Invoke(equipmentId, employeeId, ct)
                ?? Task.FromResult<InventoryOperationReceipt?>(null);
        }
    }
}
