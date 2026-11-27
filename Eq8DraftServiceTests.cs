using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки correlation и lifecycle ClayDraftService без браузера.</summary>
public sealed class Eq8DraftServiceTests
{
    /// <summary>Все операции используют registration ID владельца и точные revision/payload.</summary>
    [Fact]
    public async Task RegisterSaveAndOperations_PreserveCorrelationIdsAndRevisions()
    {
        var js = new RecordingJsRuntime
        {
            Restore = new ClayDraftRestore("reg-1", true, 4, "typed-4", null),
            NextRevision = 5,
        };
        var service = new ClayDraftService(js);

        var restored = await service.RegisterAsync("equipment-form", "7", "inventory:42", 3);
        Assert.Equal("reg-1", restored.RegistrationId);
        Assert.Equal(5, await service.SaveTypedAsync("typed-5"));
        await service.ApplyRawAsync();
        js.NextRevision = 6;
        Assert.Equal(6, await service.BeginOperationAsync("operation-6"));
        await service.RejectOperationAsync(6);
        js.NextRevision = 7;
        Assert.Equal(7, await service.BeginOperationAsync("operation-7"));
        await service.UnknownOperationAsync(7);
        await service.RecordConfirmedOperationAsync(7);
        await service.CommitOperationAsync(7);

        AssertCall(js, 0, "clayDraftStore.register", "equipment-form", "7", "inventory:42", 3);
        AssertCall(js, 1, "clayDraftStore.saveTyped", "reg-1", "typed-5");
        AssertCall(js, 2, "clayDraftStore.applyRaw", "reg-1");
        AssertCall(js, 3, "clayDraftStore.beginOperation", "reg-1", "operation-6");
        AssertCall(js, 4, "clayDraftStore.rejectOperation", "reg-1", 6L);
        AssertCall(js, 5, "clayDraftStore.beginOperation", "reg-1", "operation-7");
        AssertCall(js, 6, "clayDraftStore.unknownOperation", "reg-1", 7L);
        AssertCall(js, 7, "clayDraftStore.recordConfirmedOperation", "reg-1", 7L);
        AssertCall(js, 8, "clayDraftStore.commitOperation", "reg-1", 7L);

        await service.DiscardAsync();
        AssertCall(js, 9, "clayDraftStore.discard", "reg-1");
        await service.DisposeAsync();
        Assert.Equal(10, js.Calls.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveTypedAsync("late"));
    }

    /// <summary>Dispose освобождает только live registration и не удаляет durable draft.</summary>
    [Fact]
    public async Task Dispose_UnregistersWithoutDiscardingDurableDraft()
    {
        var js = new RecordingJsRuntime { Restore = new ClayDraftRestore("reg-2", true, 8, "typed", null) };
        var service = new ClayDraftService(js);
        await service.RegisterAsync("equipment-form", "8", "inventory:42", 1);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(2, js.Calls.Count);
        AssertCall(js, 1, "clayDraftStore.unregister", "reg-2");
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.discard");
    }

    /// <summary>Ошибка durable beginOperation пробрасывается и registration остаётся пригодной для диагностики.</summary>
    [Fact]
    public async Task BeginOperationFailure_PropagatesWithoutMaskingOrDiscarding()
    {
        var js = new RecordingJsRuntime
        {
            Restore = new ClayDraftRestore("reg-3", false, 0, null, null),
            NextRevision = 1,
        };
        js.Failures["clayDraftStore.beginOperation"] = new JSException("IndexedDB unavailable");
        var service = new ClayDraftService(js);
        await service.RegisterAsync("equipment-form", "9", "inventory:42", 1);

        var error = await Assert.ThrowsAsync<JSException>(() => service.BeginOperationAsync("save"));
        Assert.Equal("IndexedDB unavailable", error.Message);

        js.Failures.Remove("clayDraftStore.beginOperation");
        Assert.Equal(1, await service.SaveTypedAsync("still-registered"));
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.discard");
    }

    private static void AssertCall(RecordingJsRuntime js, int index, string identifier, params object?[] args)
    {
        var call = js.Calls[index];
        Assert.Equal(identifier, call.Identifier);
        var actual = call.Arguments;
        if (actual.Length == args.Length + 1 && actual[^1] is null)
            actual = actual[..^1];
        Assert.Equal(args, actual);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<JsCall> Calls { get; } = [];
        public Dictionary<string, Exception> Failures { get; } = [];
        public ClayDraftRestore Restore { get; init; } = new("missing", false, 0, null, null);
        public long NextRevision { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(new JsCall(identifier, args ?? []));
            if (Failures.TryGetValue(identifier, out var failure))
                return ValueTask.FromException<TValue>(failure);
            object? result = identifier switch
            {
                "clayDraftStore.register" => Restore,
                "clayDraftStore.saveTyped" or "clayDraftStore.beginOperation" => NextRevision,
                _ => null,
            };
            return result is null
                ? new ValueTask<TValue>(default(TValue)!)
                : new ValueTask<TValue>((TValue)result);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments);
}
