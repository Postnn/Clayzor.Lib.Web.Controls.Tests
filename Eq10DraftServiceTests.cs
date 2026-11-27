using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Регрессии EQ10 для DOM ownership, rejected registration cleanup и scoped readiness.</summary>
public sealed class Eq10DraftServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RamRegistrationDistinguishesTechnicalReleaseFromRetryableClose()
    {
        var js = new WireJsRuntime();
        js.RegisterJsonQueue.Enqueue(RegisterJson("ram-A", null));
        js.RegisterJsonQueue.Enqueue(RegisterJson("ram-B", null));
        await using var service = new ClayDraftService(js);
        var instance = Guid.NewGuid();
        var first = await service.RegisterRamHandleAsync("form", "8", "42", 2, "#form", instance);
        await first.DisposeAsync();
        AssertCall(js, "clayDraftStore.unregister", "ram-A");
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.closeRam");

        var reattached = await service.RegisterRamHandleAsync("form", "8", "42", 2, "#form", instance);
        Assert.All(js.Calls.Where(call => call.Identifier == "clayDraftStore.registerRam"),
            call => Assert.Equal(instance.ToString("D"), call.Arguments[5]));
        Assert.False(await reattached.CloseRamIfRevisionAsync(4));
        AssertCall(js, "clayDraftStore.closeRam", "ram-B", 4L);
        Assert.True(service.HasActiveRegistration);
        js.FailRamClose = true;
        await Assert.ThrowsAsync<JSException>(() => reattached.CloseRamAsync());
        Assert.True(service.HasActiveRegistration);

        js.FailRamClose = false;
        await reattached.CloseRamAsync();
        Assert.False(service.HasActiveRegistration);
        Assert.Equal(3, js.Calls.Count(call => call.Identifier == "clayDraftStore.closeRam"));
        await reattached.DisposeAsync();
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.unregister" &&
            Equals(call.Arguments[0], "ram-B"));
    }

    /// <summary>Два одинаковых DraftKey получают разные roots; scoped restore и failure не покидают handle.</summary>
    [Fact]
    public async Task TwoHandles_ForwardOwnRootsAndKeepScopedReadinessSeparate()
    {
        var js = new WireJsRuntime();
        js.RegisterJsonQueue.Enqueue(RegisterJson("reg-A", null));
        js.RegisterJsonQueue.Enqueue(RegisterJson("reg-B", null));
        await using var service = new ClayDraftService(js);
        var first = await service.RegisterHandleAsync(
            "equipment-form", "8", "inventory:42", 2, "[data-draft-owner='equipment-8']");
        var second = await service.RegisterHandleAsync(
            "equipment-form", "9", "inventory:42", 2, "[data-draft-owner='equipment-9']");

        await first.ReportRestoreScopeFailureAsync("child:account:A");
        await first.ApplyRawWithResultAsync("child:account:A");
        js.ReadinessJson = """
            {"complete":false,"pendingScopes":["child:sim:A"],"failedScopes":["child:account:A"]}
            """;
        var readiness = await first.GetRestoreReadinessAsync();

        var registrations = js.Calls.Where(call => call.Identifier == "clayDraftStore.register").ToArray();
        Assert.Equal("[data-draft-owner='equipment-8']", registrations[0].Arguments[4]);
        Assert.Equal("[data-draft-owner='equipment-9']", registrations[1].Arguments[4]);
        AssertCall(js, "clayDraftStore.reportRestoreScopeFailure", "reg-A", "child:account:A");
        AssertCall(js, "clayDraftStore.applyRaw", "reg-A", "child:account:A");
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.applyRaw" &&
            call.Arguments[0] as string == "reg-B");
        Assert.False(readiness.Complete);
        Assert.Equal("child:sim:A", Assert.Single(readiness.PendingScopes));
        Assert.Equal("child:account:A", Assert.Single(readiness.FailedScopes));
        Assert.NotNull(second);
    }

    /// <summary>Дубликат registration ID отклоняется без замены активной регистрации.</summary>
    [Fact]
    public async Task DuplicateRegistration_IsRejectedWithoutReplacingActiveHandle()
    {
        var js = new WireJsRuntime();
        js.RegisterJsonQueue.Enqueue(RegisterJson("same-reg", null, "durable-before"));
        js.RegisterJsonQueue.Enqueue(RegisterJson("same-reg", null, "other-state"));
        await using var service = new ClayDraftService(js);
        var first = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2, "#equipment-8");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterHandleAsync(
            "equipment-form", "8", "inventory:42", 2, "#equipment-8"));

        Assert.True(service.HasActiveRegistration);
        Assert.Equal("durable-before", first.Restore.TypedState);
        Assert.Equal(1, await first.SaveTypedAsync("corrected"));
        AssertCall(js, "clayDraftStore.saveTyped", "same-reg", "corrected");
    }

    /// <summary>Readiness deserialization сохраняет pending/failed раздельно и не требует raw values.</summary>
    [Fact]
    public async Task RestoreReadiness_PendingAndFailedScopesRemainIncomplete()
    {
        var js = new WireJsRuntime { RegisterJson = RegisterJson("reg-ready", null) };
        await using var service = new ClayDraftService(js);
        var handle = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2);
        js.ReadinessJson = """
            {"complete":false,"pendingScopes":["main"],"failedScopes":["child:account:A"]}
            """;

        var readiness = await handle.GetRestoreReadinessAsync();

        Assert.False(readiness.Complete);
        Assert.Equal(["main"], readiness.PendingScopes);
        Assert.Equal(["child:account:A"], readiness.FailedScopes);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(readiness), StringComparison.OrdinalIgnoreCase);
    }

    private static string RegisterJson(string registrationId, string? rejectedState, string typedState = "typed") =>
        JsonSerializer.Serialize(new
        {
            registrationId,
            hasDraft = true,
            revision = rejectedState is null ? 4 : 11,
            typedState = rejectedState ?? typedState,
            typedScopes = new Dictionary<string, string>(),
        }, JsonOptions);

    private static void AssertCall(WireJsRuntime js, string identifier, params object?[] arguments) =>
        Assert.Contains(js.Calls, call => call.Identifier == identifier && call.Arguments.SequenceEqual(arguments));

    private sealed class WireJsRuntime : IJSRuntime
    {
        public List<JsCall> Calls { get; } = [];
        public Queue<string> RegisterJsonQueue { get; } = new();
        public string? RegisterJson { get; set; }
        public bool FailRamAttach { get; init; }
        public bool FailRamClose { get; set; }
        public string ReadinessJson { get; set; } =
            """{"complete":true,"pendingScopes":[],"failedScopes":[]}""";

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
        {
            var arguments = args ?? [];
            Calls.Add(new JsCall(identifier, arguments));
            if (identifier == "clayDraftStore.closeRam" && FailRamClose)
                return ValueTask.FromException<TValue>(new JSException("delete did not commit"));
            if (identifier == "clayDraftStore.registerRam" && FailRamAttach)
                return ValueTask.FromException<TValue>(new JSException("Ram key refused"));
            object? result = identifier switch
            {
                "clayDraftStore.register" or "clayDraftStore.registerRam" => JsonSerializer.Deserialize<TValue>(
                    RegisterJsonQueue.Count > 0 ? RegisterJsonQueue.Dequeue() : RegisterJson!, JsonOptions),
                "clayDraftStore.applyRaw" => JsonSerializer.Deserialize<TValue>(
                    """{"applied":[],"pending":[],"scopes":{}}""", JsonOptions),
                "clayDraftStore.getRestoreReadiness" => JsonSerializer.Deserialize<TValue>(ReadinessJson, JsonOptions),
                "clayDraftStore.closeRam" when arguments.Length == 2 => (TValue)(object)false,
                "clayDraftStore.saveTyped" or "clayDraftStore.saveTypedScope" => (TValue)(object)1L,
                _ => default,
            };
            return new ValueTask<TValue>((TValue)result!);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments);
}
