using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки production JSON, registration handles и fail-closed gates EQ9.</summary>
public sealed class Eq9DraftProtocolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Реальный JS operation JSON проходит production deserializer со scopes и Unknown.</summary>
    [Fact]
    public async Task RestoreOperation_RealJsonRoundTrip_PreservesUnknownAttemptAndAcceptedRevisions()
    {
        const string operationJson = """
            {"version":1,"attemptId":"attempt-7","kind":"account-save","status":"unknown","revision":11,"data":"{\"equipmentId\":8,\"row\":\"A\",\"originalVersion\":4}","scopes":[{"name":"child:account:A","typedRevision":8,"rawRevisions":{"data:account.password:0":9}}],"acceptLegacyTyped":false,"legacyTypedRevision":4}
            """;
        var js = new WireJsRuntime
        {
            RegisterJson = RegisterJson("reg-7", operationJson),
        };
        await using var service = new ClayDraftService(js);

        var restore = await service.RegisterAsync("equipment-form", "8", "inventory:42", 2);
        var operation = restore!.OperationSnapshot;

        Assert.NotNull(operation);
        Assert.Equal("attempt-7", operation.AttemptId);
        Assert.Equal(ClayDraftOperationStatus.Unknown, operation.Status);
        Assert.Equal(11, operation.Revision);
        var scope = Assert.Single(operation.Scopes);
        Assert.Equal("child:account:A", scope.Name);
        Assert.Equal(8, scope.TypedRevision);
        Assert.Equal(9, scope.RawRevisions["data:account.password:0"]);
        Assert.Contains("\"originalVersion\":4", operation.Data);
    }

    /// <summary>Typed intent сериализуется в JSON, а ответ JS десериализуется строгим production контрактом.</summary>
    [Fact]
    public async Task BeginOperationIntent_UsesVersionedJsonAndReturnsTypedDurableMarker()
    {
        var js = new WireJsRuntime
        {
            RegisterJson = RegisterJson("reg-intent", null),
            OperationJson = """
                {"version":1,"attemptId":"stable-attempt","kind":"ups-link","status":"running","revision":12,"data":"{\"upsId\":5}","scopes":[{"name":"child:ups:5","typedRevision":6,"rawRevisions":{}}],"acceptLegacyTyped":false,"legacyTypedRevision":3}
                """,
        };
        await using var service = new ClayDraftService(js);
        var handle = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2);

        var operation = await handle.BeginOperationAsync(new ClayDraftOperationIntent(
            "ups-link", "{\"upsId\":5}", ["child:ups:5"], "stable-attempt"));

        using var intent = JsonDocument.Parse(js.IntentJson!);
        Assert.Equal(1, intent.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("stable-attempt", intent.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal("child:ups:5", intent.RootElement.GetProperty("acceptedScopes")[0].GetString());
        Assert.Equal(12, operation.Revision);
        Assert.Equal(6, Assert.Single(operation.Scopes).TypedRevision);
        Assert.Equal("stable-attempt", operation.AttemptId);
    }

    /// <summary>Неподдерживаемый operation JSON fail-closed; исправленная загрузка может быть повторена.</summary>
    [Fact]
    public async Task RegisterFailure_DoesNotRegisterAndCanBeRetriedWithoutMasking()
    {
        var js = new WireJsRuntime
        {
            RegisterJson = RegisterJson("bad-reg", """{"version":999}"""),
        };
        await using var service = new ClayDraftService(js);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterAsync("equipment-form", "8", "inventory:42", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveTypedAsync("blocked"));

        js.RegisterJson = RegisterJson("good-reg", null);
        var restore = await service.RegisterAsync("equipment-form", "8", "inventory:42", 2);
        Assert.Equal("good-reg", restore!.RegistrationId);
        Assert.Equal(1, await service.SaveTypedAsync("after-retry"));
    }

    /// <summary>Null registration не разрешает продолжить работу без durable BeginOperation.</summary>
    [Fact]
    public async Task NullRegistration_FailsClosed()
    {
        var js = new WireJsRuntime { RegisterJson = "null" };
        await using var service = new ClayDraftService(js);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterAsync("equipment-form", "8", "inventory:42", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginOperationAsync("save"));
    }

    /// <summary>Два handles независимы: release одного сохраняет durable record и не мешает другому.</summary>
    [Fact]
    public async Task MultipleHandles_ReleaseOnePreservesOtherAndNeverDiscards()
    {
        var js = new WireJsRuntime();
        js.RegisterJsonQueue.Enqueue(RegisterJson("reg-A", null));
        js.RegisterJsonQueue.Enqueue(RegisterJson("reg-B", null));
        await using var service = new ClayDraftService(js);
        var first = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2);
        var second = await service.RegisterHandleAsync("equipment-form", "9", "inventory:42", 2);

        await first.ReleaseAsync();
        await second.SaveTypedScopeAsync("main", "{\"serial\":\"B\"}");

        Assert.Contains(js.Calls, call => call.Identifier == "clayDraftStore.unregister" && call.Arguments[0] as string == "reg-A");
        Assert.Contains(js.Calls, call => call.Identifier == "clayDraftStore.saveTypedScope" &&
            call.Arguments[0] as string == "reg-B" && call.Arguments[1] as string == "main");
        Assert.DoesNotContain(js.Calls, call => call.Identifier == "clayDraftStore.discard");
    }

    /// <summary>Storage health и unresolved marker независимо блокируют командный gate.</summary>
    [Fact]
    public async Task CommandGate_BlocksStorageFailurePendingWritesAndUnknownOperation()
    {
        var js = new WireJsRuntime { RegisterJson = RegisterJson("reg-health", null) };
        await using var service = new ClayDraftService(js);
        var handle = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2);
        js.HealthJson = """
            {"healthy":false,"pendingWrites":2,"lastError":"Quota exceeded","hasOperation":false,"revision":8,"persistedRevision":6}
            """;

        var storageError = await Assert.ThrowsAsync<InvalidOperationException>(() => handle.EnsureCommandAllowedAsync());
        Assert.Contains("Quota exceeded", storageError.Message);

        js.HealthJson = """
            {"healthy":true,"pendingWrites":0,"lastError":null,"hasOperation":true,"revision":8,"persistedRevision":8}
            """;
        var operationError = await Assert.ThrowsAsync<InvalidOperationException>(() => handle.EnsureCommandAllowedAsync());
        Assert.Contains("не сверена", operationError.Message);

        js.HealthJson = """
            {"healthy":true,"pendingWrites":0,"lastError":null,"hasOperation":false,"revision":8,"persistedRevision":8}
            """;
        await handle.EnsureCommandAllowedAsync();
    }

    /// <summary>C# действительно ожидает JS raw restore и возвращает pending readiness ключи.</summary>
    [Fact]
    public async Task ApplyRaw_AwaitsDeferredJsResultAndReturnsPendingKeys()
    {
        var js = new WireJsRuntime { RegisterJson = RegisterJson("reg-raw", null) };
        await using var service = new ClayDraftService(js);
        var handle = await service.RegisterHandleAsync("equipment-form", "8", "inventory:42", 2);
        js.DeferRaw = true;

        var apply = handle.ApplyRawWithResultAsync();
        Assert.False(apply.IsCompleted);
        js.CompleteRaw("""{"applied":["data:main.sn:0"],"pending":["data:sim.pin:0"]}""");

        var result = await apply;
        Assert.Equal("data:main.sn:0", Assert.Single(result.Applied));
        Assert.Equal("data:sim.pin:0", Assert.Single(result.Pending));
    }

    private static string RegisterJson(string registrationId, string? operationJson) =>
        JsonSerializer.Serialize(new
        {
            registrationId,
            hasDraft = operationJson is not null,
            revision = operationJson is null ? 0 : 11,
            typedState = "{\"main\":\"dirty\"}",
            operation = operationJson,
            typedScopes = new Dictionary<string, string>(),
        }, JsonOptions);

    private sealed class WireJsRuntime : IJSRuntime
    {
        private TaskCompletionSource<string>? _rawCompletion;

        public List<JsCall> Calls { get; } = [];
        public Queue<string> RegisterJsonQueue { get; } = new();
        public string? RegisterJson { get; set; }
        public string? OperationJson { get; set; }
        public string? IntentJson { get; private set; }
        public string? HealthJson { get; set; }
        public bool DeferRaw { get; set; }

        public void CompleteRaw(string json) => _rawCompletion!.TrySetResult(json);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            Invoke<TValue>(identifier, args);

        private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
        {
            var arguments = args ?? [];
            Calls.Add(new JsCall(identifier, arguments));
            if (identifier == "clayDraftStore.register")
            {
                var json = RegisterJsonQueue.Count > 0 ? RegisterJsonQueue.Dequeue() : RegisterJson!;
                return new ValueTask<TValue>(JsonSerializer.Deserialize<TValue>(json, JsonOptions)!);
            }
            if (identifier == "clayDraftStore.beginOperationIntent")
            {
                IntentJson = (string)arguments[1]!;
                return new ValueTask<TValue>((TValue)(object)OperationJson!);
            }
            if (identifier == "clayDraftStore.getHealth")
                return new ValueTask<TValue>(JsonSerializer.Deserialize<TValue>(HealthJson!, JsonOptions)!);
            if (identifier == "clayDraftStore.applyRaw")
            {
                if (DeferRaw)
                {
                    _rawCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return new ValueTask<TValue>(AwaitRaw<TValue>());
                }
                return new ValueTask<TValue>(JsonSerializer.Deserialize<TValue>("{\"applied\":[],\"pending\":[]}", JsonOptions)!);
            }
            if (identifier is "clayDraftStore.saveTyped" or "clayDraftStore.saveTypedScope" or "clayDraftStore.beginOperation")
                return new ValueTask<TValue>((TValue)(object)1L);
            return new ValueTask<TValue>(default(TValue)!);
        }

        private async Task<TValue> AwaitRaw<TValue>() =>
            JsonSerializer.Deserialize<TValue>(await _rawCompletion!.Task, JsonOptions)!;
    }

    private sealed record JsCall(string Identifier, object?[] Arguments);
}
