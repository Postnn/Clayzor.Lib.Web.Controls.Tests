using Clayzor.Lib.Entities.Inventory;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class SlBlSynchronizationTests
{
    [Theory]
    [InlineData(false, "old", false, false, SlBlOutcome.Rejected, 0)]
    [InlineData(true, "changed", false, false, SlBlOutcome.Rejected, 0)]
    [InlineData(true, "old", false, false, SlBlOutcome.Verified, 1)]
    [InlineData(true, "old", true, false, SlBlOutcome.Partial, 1)]
    [InlineData(true, "old", false, true, SlBlOutcome.Unknown, 1)]
    public async Task VerifiesOneAuthorizedDispatch(bool allowed, string current, bool partial, bool timeout, SlBlOutcome expected, int calls)
    {
        var adapter = new Adapter(allowed, current, partial, timeout);
        var intent = new SlBlIntent(Guid.NewGuid(), "example@example.invalid", SlBlKind.Safe, "old", "new");
        var result = await SlBlSynchronization.ExecuteAsync(adapter, intent, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(calls, adapter.Dispatches);
        Assert.Equal(calls, adapter.Readbacks);
        if (partial) Assert.Equal("new", result.Differences?.NotSaved);
    }

    [Theory]
    [InlineData(true, SlBlOutcome.Partial)]
    [InlineData(false, SlBlOutcome.Verified)]
    public async Task ReconciliationOnlyReadsTheSavedIntent(bool partial, SlBlOutcome expected)
    {
        var adapter = new Adapter(false, "different", partial, false);
        var intent = new SlBlIntent(Guid.NewGuid(), "test@example.invalid", SlBlKind.Blocked, "old", "desired");
        var result = await SlBlSynchronization.ReconcileAsync(adapter, intent, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(intent, adapter.ComparedIntent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPreflightNeverRequiresUnknownOutcome(bool failAuthorization)
    {
        var adapter = new Adapter(true, "old", false, false) { FailAuthorization = failAuthorization, FailOriginal = !failAuthorization };
        var result = await SlBlSynchronization.ExecuteAsync(adapter,
            new SlBlIntent(Guid.NewGuid(), "test@example.invalid", SlBlKind.Blocked, "old", "desired"), default);
        Assert.Equal(SlBlOutcome.Rejected, result.Outcome);
        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, adapter.Readbacks);
    }

    [Fact]
    public async Task RevokedCapabilityImmediatelyBeforeDispatchIsAConfirmedRejection()
    {
        var adapter = new Adapter(true, "old", false, false) { RevokeAtDispatch = true };
        var result = await SlBlSynchronization.ExecuteAsync(adapter,
            new SlBlIntent(Guid.NewGuid(), "test@example.invalid", SlBlKind.Safe, "old", "desired"), default);
        Assert.Equal(SlBlOutcome.Rejected, result.Outcome);
        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, adapter.Readbacks);
    }

    private sealed class Adapter(bool allowed, string current, bool partial, bool timeout) : ISlBlSynchronization
    {
        public bool FailAuthorization { get; init; }
        public bool FailOriginal { get; init; }
        public bool RevokeAtDispatch { get; init; }
        public int Dispatches { get; private set; }
        public SlBlIntent? ComparedIntent { get; private set; }
        public int Readbacks { get; private set; }
        public Task<bool> CanWriteAsync(string address, SlBlKind kind, CancellationToken ct) => FailAuthorization ? Task.FromException<bool>(new TimeoutException()) : Task.FromResult(allowed);
        public Task<string> ReadOriginalAsync(string address, SlBlKind kind, CancellationToken ct) => FailOriginal ? Task.FromException<string>(new TimeoutException()) : Task.FromResult(current);
        public Task DispatchAsync(SlBlIntent intent, CancellationToken ct) { if (RevokeAtDispatch) throw new SlBlNotDispatchedException("revoked"); Dispatches++; return Task.CompletedTask; }
        public Task<SlBlDifferences> CompareAsync(SlBlIntent intent, CancellationToken ct)
        {
            ComparedIntent = intent;
            Readbacks++;
            if (timeout) throw new TimeoutException();
            return Task.FromResult(new SlBlDifferences { NotSaved = partial ? "new" : "" });
        }
    }
}
