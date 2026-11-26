using System.Reflection;
using Bunit;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class Eq10DomainPhoneTests
{
    [Fact]
    public async Task Configure_RecordsExactPhaseBeforeAndAfterEachDispatch()
    {
        var connection = CheckpointConnection();
        var service = Service(connection);
        var observed = new List<(int Calls, PhoneSyncPhase First, PhoneSyncPhase Second)>();

        await service.ConfigurePhoneEquipmentAttemptAsync(7, attempt =>
        {
            var phases = attempt.Operations.ToArray();
            observed.Add((AtsDispatches(connection), phases[0].Phase, phases[1].Phase));
            return Task.CompletedTask;
        });

        Assert.Equal(new[]
        {
            (0, PhoneSyncPhase.Unknown, PhoneSyncPhase.NotSent),
            (1, PhoneSyncPhase.Succeeded, PhoneSyncPhase.NotSent),
            (1, PhoneSyncPhase.Succeeded, PhoneSyncPhase.Unknown),
            (2, PhoneSyncPhase.Succeeded, PhoneSyncPhase.Succeeded),
        }, observed);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public async Task Configure_JournalFailureStopsNextDispatch(int failedCheckpoint, int expectedDispatches)
    {
        var connection = CheckpointConnection();
        var service = Service(connection);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigurePhoneEquipmentAttemptAsync(7, _ =>
            ++calls == failedCheckpoint
                ? Task.FromException(new InvalidOperationException("Не удалось сохранить этап"))
                : Task.CompletedTask));

        Assert.Equal(failedCheckpoint, calls);
        Assert.Equal(expectedDispatches, AtsDispatches(connection));
    }

    private static ScriptedConnection CheckpointConnection() =>
        Connection(Script.Rows(EquipmentRow(ownerId: 1, typeId: 30, changedAt: Version())), Script.Scalar(1),
            Script.Rows(PhoneRow(1, 8), PhoneRow(2, 8)),
            Script.Rows(LookupRow(1, 1, "xorcom")), Script.Rows(LookupRow(2, 1, "xorcom")),
            Script.Scalar(1), Script.NonQuery(), Script.Scalar(1), Script.NonQuery());

    private static int AtsDispatches(ScriptedConnection connection) =>
        connection.CommandLog.Count(sql => sql.Contains("sp_Xorcom_PhoneNumberSync", StringComparison.Ordinal));
}

public sealed partial class EquipmentFormTests
{
    private const string PhoneProgressScope = "equipment-phone-progress";

    [Fact]
    public async Task InterruptedPhoneSave_ReopensDurableCheckpointWithoutAcceptingBusinessMatch()
    {
        var operation = new ClayDraftOperation(1, "draft-attempt", "equipment-phone-save",
            ClayDraftOperationStatus.Running, 7, "{}", [], false, 0);
        var intent = new EquipmentPhoneSyncIntent("ats-attempt", 7, EquipmentPhoneSyncOrigin.EquipmentSave,
            new DateTime(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc), 1, 30, [], []);
        var checkpoint = new PhoneSyncAttempt(intent,
        [
            new PhoneSyncOperationResult(intent.AttemptId, new PhoneSyncOperation(PhoneSyncKind.Xorcom, "1"),
                PhoneSyncPhase.Succeeded),
            new PhoneSyncOperationResult(intent.AttemptId, new PhoneSyncOperation(PhoneSyncKind.Xorcom, "2"),
                PhoneSyncPhase.Unknown),
        ]);

        var first = CreateFixture();
        string durable;
        await using (first.Context)
        {
            first.Context.JSInterop.Setup<long>("clayDraftStore.saveTypedScope", _ => true).SetResult(8);
            var page = first.Context.Render<EquipmentForm>();
            WaitForSn(page, "CLAY_TEST_7");
            SetPrivate(page.Instance, "_draftOperation", operation);
            await page.InvokeAsync(async () =>
            {
                var method = typeof(EquipmentForm).GetMethod("CreatePhoneSyncCheckpointWriter",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var write = (Func<PhoneSyncAttempt, Task>)method.Invoke(page.Instance, [])!;
                await write(checkpoint);
            });
            var write = Assert.Single(first.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
            Assert.Equal(PhoneProgressScope, write.Arguments[1]);
            durable = Assert.IsType<string>(write.Arguments[2]);
        }

        var second = CreateFixture();
        await using var context = second.Context;
        var reopened = context.Render<EquipmentForm>();
        WaitForSn(reopened, "CLAY_TEST_7");
        SetPrivate(reopened.Instance, "_draftOperation", operation);
        SetPrivate(reopened.Instance, "_draftOperationUnresolved", true);
        await InvokeHandler(reopened, "RestorePhoneSyncCheckpoint",
            new ClayDraftRestore("test-registration", true, 8, null, null,
                new Dictionary<string, string> { [PhoneProgressScope] = durable }));

        await InvokeHandler(reopened, "ReconcileDraftOperationAsync");

        Assert.Contains("подтверждено 1, неизвестно 1", reopened.Markup);
        Assert.True(PrivateBool(reopened.Instance, "_draftOperationUnresolved"));
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.commitOperation"]);
        Assert.Equal(0, second.Service.SyncCalls);
        Assert.Equal(0, second.Service.UpdateCalls);
    }
}
