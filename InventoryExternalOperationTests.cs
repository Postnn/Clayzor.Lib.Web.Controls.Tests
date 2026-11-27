using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class MaintenanceTests
{
    /// <summary>Свидетельство прежнего переключения удерживает оба порта без создания подтверждения SQL.</summary>
    [Fact]
    public async Task LegacyCoupledEvidenceBlocksPortsWithoutConfirmingSql()
    {
        var connection = new MaintenanceConnection();
        using var first = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var actor = new Employee { Id = 4 };
        var move = InventoryOperationJournal.ForEmployee(actor, Guid.NewGuid(), "net/10", "net.switch", ["net/20"]);
        await InventoryOperationJournal.ImportLegacyAsync(first, [move], default);
        await InventoryOperationJournal.ImportLegacyAsync(first, [move], default);

        using var reopened = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        Assert.True(await InventoryOperationJournal.IsScopePendingAsync(reopened, "net/10", default));
        Assert.True(await InventoryOperationJournal.IsScopePendingAsync(reopened, "net/20", default));
        Assert.Null(await InventoryOperationJournal.ReadAttemptAsync(reopened, move, default));
        Assert.Equal(1, connection.LegacyEvidenceCount);

        var poe = InventoryOperationJournal.ForEmployee(actor, Guid.NewGuid(), "net/20", "net.poe");
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(reopened, poe,
                _ => throw new InvalidOperationException("Unresolved legacy scope was bypassed."), default));
    }

    /// <summary>Составное действие блокирует обе цели до фиксации результата и проверяет точный набор при сверке.</summary>
    [Fact]
    public async Task CoupledScopesBlockEitherPortAndRejectChangedTargetOnReplay()
    {
        var connection = new MaintenanceConnection();
        using var first = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        using var other = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var actor = new Employee { Id = 4 };
        var move = InventoryOperationJournal.ForEmployee(actor, Guid.NewGuid(), "net/10", "net.switch", ["net/20"]);
        var writes = 0;
        var result = await InventoryOperationJournal.RunSqlAsync<MaintenanceRejectedException>(first, move,
            async (_, _, token) =>
            {
                foreach (var scope in new[] { "net/10", "net/20" })
                {
                    Assert.True(await InventoryOperationJournal.IsScopePendingAsync(other, scope, token));
                    var restart = InventoryOperationJournal.ForEmployee(actor, Guid.NewGuid(), scope, "net.poe");
                    await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
                        InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(other, restart,
                            _ => throw new InvalidOperationException("Conflict was dispatched."), token));
                }
                writes++;
                return 20;
            }, default);

        Assert.Equal(InventoryOperationState.Confirmed, result.State);
        Assert.False(await InventoryOperationJournal.IsScopePendingAsync(other, "net/10", default));
        Assert.False(await InventoryOperationJournal.IsScopePendingAsync(other, "net/20", default));
        Assert.Equal(result, await InventoryOperationJournal.ReadAttemptAsync(other, move, default));
        await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
            InventoryOperationJournal.ReadAttemptAsync(other, move with { OtherScopes = ["net/30"] }, default));
        Assert.Equal(1, writes);
    }

    /// <summary>Состояние Dispatching записано до внешнего вызова, даже если ответ не придёт.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalDispatchIsDurableAndNeverRepeatedAfterReopening(bool loseReply)
    {
        var connection = new MaintenanceConnection();
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var actor = new Employee { Id = 4 };
        var attempt = InventoryOperationJournal.ForEmployee(actor, Guid.NewGuid(), "net/17", "net.poe");
        var sends = 0;
        Task<int?> Send(CancellationToken _)
        {
            Assert.Equal(InventoryOperationState.Dispatching, connection.JournalState);
            sends++;
            return loseReply
                ? Task.FromException<int?>(new TimeoutException("external reply lost"))
                : Task.FromResult<int?>(17);
        }

        if (loseReply)
            await Assert.ThrowsAsync<TimeoutException>(() =>
                InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(db, attempt, Send, default));
        else
            Assert.Equal(InventoryOperationState.Confirmed,
                (await InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(db, attempt, Send, default)).State);

        using var reopened = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var previous = await InventoryOperationJournal.ReadAttemptAsync(reopened, attempt, default);
        Assert.NotNull(previous);
        Assert.Equal(loseReply ? InventoryOperationState.Dispatching : InventoryOperationState.Confirmed, previous.State);

        if (loseReply)
        {
            await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
                InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(reopened, attempt, Send, default));
            await Assert.ThrowsAsync<InventoryOperationPendingException>(() =>
                InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(reopened,
                    attempt with { AttemptId = Guid.NewGuid() }, Send, default));
        }
        else
            Assert.Equal(previous,
                await InventoryOperationJournal.RunExternalAsync<UnauthorizedAccessException>(reopened, attempt, Send, default));

        Assert.Equal(1, sends);
    }
}
