using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Регрессии EQ10 для DOM ownership, rejected registration cleanup и scoped readiness.</summary>
public sealed class Eq10DraftServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Блокировка прежнего контекста действует до окончания импорта, включая отказ сервера.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyEvidenceLeaseIsReleasedOnlyAfterImport(bool failImport)
    {
        var evidence = new ClayLegacyDraftOperationEvidence(Guid.NewGuid().ToString("N"), "net.poe", "17");
        var js = new WireJsRuntime { LegacyEvidence = [evidence] };
        await using var service = new ClayDraftService(js);
        var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transfer = service.TransferLegacyEvidenceAsync("inventory.net", "17", "42", async (items, _) =>
        {
            Assert.Equal(evidence, Assert.Single(items));
            await releaseImport.Task;
            if (failImport) throw new InvalidOperationException("Import failed.");
        });

        var begin = Assert.Single(js.Calls);
        Assert.Equal("clayDraftStore.beginLegacyOperationEvidenceTransfer", begin.Identifier);
        var leaseId = Assert.IsType<string>(begin.Arguments[3]);

        releaseImport.SetResult();
        if (failImport) await Assert.ThrowsAsync<InvalidOperationException>(() => transfer);
        else await transfer;

        AssertCall(js, "clayDraftStore.finishLegacyOperationEvidenceTransfer", leaseId);
    }

    /// <summary>Даже при потере ответа чтения освобождается та же блокировка, а импорт не запускается.</summary>
    [Fact]
    public async Task LegacyEvidenceLeaseReleasesKnownIdAfterLostReadReply()
    {
        var js = new WireJsRuntime { FailLegacyEvidenceRead = true };
        await using var service = new ClayDraftService(js);

        await Assert.ThrowsAsync<JSException>(() => service.TransferLegacyEvidenceAsync("inventory.net", "17", "42",
            (_, _) => throw new InvalidOperationException("Evidence was not read.")));

        var begin = Assert.Single(js.Calls, call => call.Identifier == "clayDraftStore.beginLegacyOperationEvidenceTransfer");
        AssertCall(js, "clayDraftStore.finishLegacyOperationEvidenceTransfer", begin.Arguments[3]);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("import")]
    [InlineData("ram")]
    [InlineData("stale")]
    [InlineData("retire")]
    public async Task RetirementRequiresSuccessfulImportAndCurrentRamHandle(string outcome)
    {
        const string begin = "clayDraftStore.beginLegacyOperationEvidenceTransfer";
        const string attach = "clayDraftStore.registerRam";
        const string retire = "clayDraftStore.retireLegacyOperationEvidenceTransfer";
        const string finish = "clayDraftStore.finishLegacyOperationEvidenceTransfer";
        const string unregister = "clayDraftStore.unregister";
        var js = new WireJsRuntime { FailRamAttach = outcome == "ram", FailLegacyRetirement = outcome == "retire" };
        js.RegisterJsonQueue.Enqueue(RegisterJson("ram-verified", null));
        await using var service = new ClayDraftService(js);
        var current = true;
        var transfer = service.TransferLegacyAndRegisterRamAsync("inventory.net", "17", "42", async (_, ct) =>
        {
            if (outcome == "import") throw new InvalidOperationException("Import failed.");
            var owned = await service.RegisterRamHandleAsync("inventory.net", "17", "42", 1, "#editor", Guid.NewGuid(), ct);
            if (outcome == "stale") current = false;
            return owned;
        }, () => current);

        if (outcome == "success") Assert.NotNull(await transfer);
        else if (outcome is "ram" or "retire") await Assert.ThrowsAsync<JSException>(() => transfer);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => transfer);

        string[] expected = outcome switch
        {
            "success" => [begin, attach, retire, finish],
            "import" => [begin, finish],
            "ram" => [begin, attach, finish],
            "stale" => [begin, attach, finish, unregister],
            _ => [begin, attach, retire, finish, unregister],
        };
        Assert.Equal(expected, js.Calls.Select(call => call.Identifier).ToArray());
        var leaseId = js.Calls[0].Arguments[3];
        Assert.Equal(true, js.Calls[0].Arguments[4]);
        Assert.All(js.Calls.Where(call => call.Identifier == retire || call.Identifier == finish),
            call => Assert.Equal(leaseId, call.Arguments[0]));
        Assert.Equal(outcome == "success", service.HasActiveRegistration);
    }

    /// <summary>Техническое освобождение сохраняет контекст, а неудачное закрытие можно повторить.</summary>
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
        public ClayLegacyDraftOperationEvidence[] LegacyEvidence { get; init; } = [];
        public bool FailLegacyEvidenceRead { get; init; }
        public bool FailRamAttach { get; init; }
        public bool FailLegacyRetirement { get; init; }
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
            if (identifier == "clayDraftStore.beginLegacyOperationEvidenceTransfer" && FailLegacyEvidenceRead)
                return ValueTask.FromException<TValue>(new JSException("read reply lost"));
            if (identifier == "clayDraftStore.registerRam" && FailRamAttach)
                return ValueTask.FromException<TValue>(new JSException("Ram key refused"));
            if (identifier == "clayDraftStore.retireLegacyOperationEvidenceTransfer" && FailLegacyRetirement)
                return ValueTask.FromException<TValue>(new JSException("retirement did not commit"));
            object? result = identifier switch
            {
                "clayDraftStore.register" or "clayDraftStore.registerRam" => JsonSerializer.Deserialize<TValue>(
                    RegisterJsonQueue.Count > 0 ? RegisterJsonQueue.Dequeue() : RegisterJson!, JsonOptions),
                "clayDraftStore.applyRaw" => JsonSerializer.Deserialize<TValue>(
                    """{"applied":[],"pending":[],"scopes":{}}""", JsonOptions),
                "clayDraftStore.getRestoreReadiness" => JsonSerializer.Deserialize<TValue>(ReadinessJson, JsonOptions),
                "clayDraftStore.closeRam" when arguments.Length == 2 => (TValue)(object)false,
                "clayDraftStore.beginLegacyOperationEvidenceTransfer" => (TValue)(object)LegacyEvidence,
                "clayDraftStore.saveTyped" or "clayDraftStore.saveTypedScope" => (TValue)(object)1L,
                _ => default,
            };
            return new ValueTask<TValue>((TValue)result!);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments);
}
