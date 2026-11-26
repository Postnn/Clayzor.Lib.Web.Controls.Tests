using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Диагностика существующего запрета синхронизации без mailbox policy.</summary>
public sealed class SlBlMailboxPolicyDiagnosticTests
{
    /// <summary>Без авторитетной политики ни Safe, ни Blocked не отправляется в SQL.</summary>
    [Theory]
    [InlineData(SlBlKind.Safe)]
    [InlineData(SlBlKind.Blocked)]
    public async Task MissingMailboxPolicyStopsEveryDispatch(SlBlKind kind)
    {
        using var sql = new ScriptedConnection(new Queue<ScriptEntry>(), emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => sql);
        var adapter = new SlBlSqlSynchronization(db, @"TEST\operator", policy: null);
        var intent = new SlBlIntent(Guid.NewGuid(), "mailbox@example.test", kind, "", "entry@example.test");

        Assert.False(await adapter.CanWriteAsync(intent.Address, intent.Kind, default));
        await Assert.ThrowsAsync<SlBlNotDispatchedException>(() => adapter.DispatchAsync(intent, default));
        Assert.Empty(sql.CommandLog);
    }
}
