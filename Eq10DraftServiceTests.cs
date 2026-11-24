using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Регрессии EQ10 для DOM ownership, rejected registration cleanup и scoped readiness.</summary>
public sealed class Eq10DraftServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    /// <summary>После C# rejection живой JS handle освобождается без discard; retry видит прежние durable bytes.</summary>
    [Fact]
    public async Task RejectedRegistration_UnregistersLiveHandleWithoutDiscardAndAllowsCorrectedRetry()
    {
        var js = new WireJsRuntime();
        js.RegisterJsonQueue.Enqueue(RegisterJson("bad-reg", """{"version":999}""", "durable-before"));
        js.RegisterJsonQueue.Enqueue(RegisterJson("good-reg", null, "durable-before"));
        await using var service = new ClayDraftService(js);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterHandleAsync(
            "equipment-form", "8", "inventory:42", 2, "#equipment-8"));

        AssertCall(js, "clayDraftStore.unregister", "bad-reg");
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.discard");
        Assert.False(service.HasActiveRegistration);

        var retry = await service.RegisterHandleAsync(
            "equipment-form", "8", "inventory:42", 2, "#equipment-8");
        Assert.Equal("durable-before", retry.Restore.TypedState);
        Assert.Equal(1, await retry.SaveTypedAsync("corrected"));
        AssertCall(js, "clayDraftStore.saveTyped", "good-reg", "corrected");
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

    private static string RegisterJson(string registrationId, string? operationJson, string typedState = "typed") =>
        JsonSerializer.Serialize(new
        {
            registrationId,
            hasDraft = true,
            revision = operationJson is null ? 4 : 11,
            typedState,
            operation = operationJson,
            typedScopes = new Dictionary<string, string>(),
        }, JsonOptions);

    private static void AssertCall(WireJsRuntime js, string identifier, params object?[] arguments) =>
        Assert.Contains(js.Calls, call => call.Identifier == identifier && call.Arguments.SequenceEqual(arguments));

    private sealed class WireJsRuntime : IJSRuntime
    {
        public List<JsCall> Calls { get; } = [];
        public Queue<string> RegisterJsonQueue { get; } = new();
        public string? RegisterJson { get; set; }
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
            object? result = identifier switch
            {
                "clayDraftStore.register" => JsonSerializer.Deserialize<TValue>(
                    RegisterJsonQueue.Count > 0 ? RegisterJsonQueue.Dequeue() : RegisterJson!, JsonOptions),
                "clayDraftStore.applyRaw" => JsonSerializer.Deserialize<TValue>(
                    """{"applied":[],"pending":[],"scopes":{}}""", JsonOptions),
                "clayDraftStore.getRestoreReadiness" => JsonSerializer.Deserialize<TValue>(ReadinessJson, JsonOptions),
                "clayDraftStore.saveTyped" or "clayDraftStore.saveTypedScope" => (TValue)(object)1L,
                _ => default,
            };
            return new ValueTask<TValue>((TValue)result!);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments);
}
