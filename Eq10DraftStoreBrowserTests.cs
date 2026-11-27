using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Production clayDraftStore.js в настоящем headless browser с IndexedDB/WebCrypto/Web Locks.</summary>
public sealed class Eq10DraftStoreBrowserTests
{
    /// <summary>DOM ownership и scoped readiness обычных черновиков.</summary>
    [Theory]
    [InlineData("roots")]
    [InlineData("legacy")]
    [InlineData("readiness")]
    [InlineData("pendingRetry")]
    public async Task ProductionStore_PreservesEq10Contracts(string scenario)
    {
        var result = await RunBrowserAsync(scenario);
        Assert.True(result.Passed, result.Error);
    }

    private static async Task<BrowserResult> RunBrowserAsync(string scenario)
    {
        var browser = FindBrowser();
        var source = File.ReadAllText(FindStorePath());
        var work = Path.Combine(AppContext.BaseDirectory, "eq10-draft-browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var page = Path.Combine(work, "fixture.html");
        var html = $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"></head><body>
            <script>
            window.__draftScriptError = null;
            window.addEventListener('error', event => { window.__draftScriptError = `${event.message} ${event.filename}:${event.lineno}:${event.colno}`; });
            </script>
            <script>
            {{source}}
            </script>
            <script>
            const scenario = {{JsonSerializer.Serialize(scenario)}};
            const store = window.clayDraftStore;
            const assert = (condition, message) => { if (!condition) throw new Error(message); };
            const mount = html => { document.body.innerHTML = html; };
            const enter = (id, value) => {
                const field = document.getElementById(id);
                field.value = value;
                field.dispatchEvent(new Event('input', { bubbles: true }));
            };
            const intent = (kind, scopes = ['main']) => JSON.stringify({
                version: 1, kind, data: '{"equipmentId":8}', acceptedScopes: scopes,
                attemptId: crypto.randomUUID(), acceptLegacyTyped: false
            });
            const register = (entity = '8', root = "[data-owner='root']") =>
                store.register('equipment-form', entity, 'inventory:42', 2, root);
            const tests = {
                async roots() {
                    mount(`
                        <div id="root-a" class="equipment-form" data-owner="A"><input id="a" data-draft-key="shared"></div>
                        <div id="portal-a" class="mud-dialog" data-owner="A" data-draft-scope="child:A"><input id="pa" data-draft-key="portal"></div>
                        <div id="root-b" class="equipment-form" data-owner="B"><input id="b" data-draft-key="shared"></div>
                        <div id="portal-b" class="mud-dialog" data-owner="B" data-draft-scope="child:B"><input id="pb" data-draft-key="portal"></div>`);
                    let a = await register('8', "[data-owner='A']");
                    let b = await register('9', "[data-owner='B']");
                    enter('a', 'ENTITY_8'); enter('pa', 'PORTAL_8');
                    enter('b', 'ENTITY_9'); enter('pb', 'PORTAL_9');
                    await store.canReload();
                    await store.unregister(a.registrationId);
                    await store.unregister(b.registrationId);
                    for (const id of ['a', 'pa', 'b', 'pb']) document.getElementById(id).value = 'DB';
                    a = await register('8', "[data-owner='A']");
                    b = await register('9', "[data-owner='B']");
                    const appliedA = await store.applyRaw(a.registrationId);
                    assert(document.getElementById('a').value === 'ENTITY_8', 'Entity 8 main raw was not restored.');
                    assert(document.getElementById('pa').value === 'PORTAL_8', 'Entity 8 portal raw was not restored.');
                    assert(document.getElementById('b').value === 'DB', 'Entity A restore crossed into entity B.');
                    assert(appliedA.pending.length === 0, 'Entity A has unexpected pending keys.');
                    await store.applyRaw(b.registrationId);
                    assert(document.getElementById('b').value === 'ENTITY_9', 'Entity 9 main raw crossed registrations.');
                    assert(document.getElementById('pb').value === 'PORTAL_9', 'Entity 9 portal raw crossed registrations.');
                    await store.unregister(a.registrationId);
                    await store.saveTyped(b.registrationId, '{"entity":9}');
                },
                async legacy() {
                    mount('<div class="equipment-form"><input></div><div class="equipment-form"><input></div>');
                    let ambiguous = false;
                    try { await store.register('equipment-form', '8', 'inventory:42', 2); } catch { ambiguous = true; }
                    assert(ambiguous, 'Legacy registration accepted ambiguous DOM roots.');
                    mount(`
                        <div class="equipment-form"><input id="main" data-draft-key="main.sn"></div>
                        <div class="mud-dialog" data-draft-scope="child:A"><input id="portal" data-draft-key="portal"></div>`);
                    let r = await store.register('equipment-form', '8', 'inventory:42', 2);
                    await store.openScope(r.registrationId, 'child:A');
                    enter('main', 'MAIN'); enter('portal', 'PORTAL');
                    await store.canReload();
                    await store.unregister(r.registrationId);
                    document.getElementById('main').value = 'DB';
                    document.getElementById('portal').value = 'DB';
                    r = await store.register('equipment-form', '8', 'inventory:42', 2);
                    await store.openScope(r.registrationId, 'child:A');
                    await store.applyRaw(r.registrationId);
                    assert(document.getElementById('main').value === 'MAIN' &&
                        document.getElementById('portal').value === 'PORTAL', 'Legacy root/portal registration regressed.');
                },
                async pendingRetry() {
                    mount(`
                        <div class="equipment-form" data-owner="root">
                            <input id="first" data-draft-key="main.first">
                            <input id="later" data-draft-key="main.later">
                        </div>
                        <div class="mud-dialog" data-owner="root" data-draft-scope="child:A">
                            <input id="child" data-draft-key="child.value">
                        </div>`);
                    let r = await register();
                    enter('first', 'stored first');
                    enter('later', 'stored later');
                    enter('child', 'separate draft');
                    await store.unregister(r.registrationId);
                    const first = document.getElementById('first');
                    const later = document.getElementById('later');
                    const child = document.getElementById('child');
                    first.value = later.value = child.value = '';
                    later.disabled = true;

                    r = await register();
                    const initial = await store.applyPendingRaw(r.registrationId, 'main');
                    assert(initial.applied.length === 1 && initial.pending.length === 1, 'Initial restore did not distinguish an unavailable field.');
                    assert(first.value === 'stored first' && later.value === '', 'Unexpected initial field values.');
                    first.value = 'newer text';
                    later.disabled = false;
                    let inputEvents = 0;
                    later.addEventListener('input', () => inputEvents++);
                    await store.reportRestoreScopeFailure(r.registrationId, 'child:A');

                    const retry = await store.applyPendingRaw(r.registrationId, 'main', false);
                    const readiness = await store.getRestoreReadiness(r.registrationId);
                    assert(retry.applied.length === 1 && retry.pending.length === 0, 'Retry did not accept the previously unavailable field.');
                    assert(first.value === 'newer text' && later.value === 'stored later', 'Retry replayed an already restored value.');
                    assert(child.value === '' && readiness.pendingScopes.includes('child:A') && readiness.failedScopes.includes('child:A'),
                        'Retry changed a different scope.');
                    assert(inputEvents === 0, 'Silent restore dispatched an input event.');
                },
                async readiness() {
                    mount(`
                        <div class="equipment-form" data-owner="root"><input id="main" data-draft-key="main.sn"></div>
                        <div class="mud-dialog" data-owner="root" data-draft-scope="child:account:A">
                            <input id="notes" data-draft-key="account.notes">
                            <input id="secret" type="password" data-draft-key="account.password">
                        </div>`);
                    let r = await register();
                    enter('main', 'SERIAL'); enter('notes', 'NOTE'); enter('secret', 'PROTECTED');
                    await store.canReload();
                    await store.unregister(r.registrationId);
                    document.getElementById('main').value = 'DB';
                    document.getElementById('notes').value = '';
                    document.getElementById('secret').value = '';
                    r = await register();
                    let readiness = await store.getRestoreReadiness(r.registrationId);
                    assert(!readiness.complete && readiness.pendingScopes.includes('main') &&
                        readiness.pendingScopes.includes('child:account:A'), 'Restored raw scopes started as ready.');
                    await store.applyRaw(r.registrationId, 'main');
                    assert(document.getElementById('secret').value === '', 'Main restore exposed a child secret.');
                    await store.reportRestoreScopeFailure(r.registrationId, 'child:account:A');
                    readiness = await store.getRestoreReadiness(r.registrationId);
                    assert(!readiness.complete && readiness.failedScopes.includes('child:account:A'),
                        'Failed child readiness was reported as complete.');
                    assert(!JSON.stringify(readiness).includes('PROTECTED'), 'Readiness exposed a raw secret.');
                    const restored = await store.applyRaw(r.registrationId, 'child:account:A');
                    readiness = await store.getRestoreReadiness(r.registrationId);
                    assert(readiness.complete && document.getElementById('main').value === 'SERIAL' &&
                        document.getElementById('notes').value === 'NOTE' &&
                        document.getElementById('secret').value === '', 'Scoped readiness retry did not finish exactly its non-secret fields.');
                    assert(restored.applied.length === 1 && restored.pending.length === 0,
                        'Password input entered the raw capture or restore set.');
                }
            };
            (async () => {
                const result = { passed: false, error: null };
                try { await tests[scenario](); result.passed = true; }
                catch (error) { result.error = String(window.__draftScriptError || error?.stack || error); }
                const output = document.createElement('pre');
                output.id = 'results';
                output.textContent = JSON.stringify(result);
                document.body.replaceChildren(output);
            })();
            </script>
            </body></html>
            """;
        await File.WriteAllTextAsync(page, html);
        var profile = Path.Combine(work, "profile");
        var start = new ProcessStartInfo
        {
            FileName = browser,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--headless=new");
        start.ArgumentList.Add("--disable-gpu");
        start.ArgumentList.Add("--disable-background-networking");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--remote-allow-origins=*");
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add(new Uri(page).AbsoluteUri);
        var port = 0;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Browser did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            port = await WaitForDevToolsPortAsync(profile, timeout.Token);
            var address = $"http://127.0.0.1:{port}";
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var pages = JsonDocument.Parse(await http.GetStringAsync("/json/list", timeout.Token));
            var socket = pages.RootElement.EnumerateArray()
                .First(item => item.GetProperty("type").GetString() == "page" &&
                               item.GetProperty("url").GetString()?.Contains("fixture.html", StringComparison.Ordinal) == true)
                .GetProperty("webSocketDebuggerUrl").GetString()!;
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(socket), timeout.Token);
            await InvokeCdpAsync(client, 1, "Page.enable", new { }, timeout.Token);
            for (var attempt = 0; attempt < 400; attempt++)
            {
                using var response = await InvokeCdpAsync(client, attempt + 2, "Runtime.evaluate", new
                {
                    expression = "document.getElementById('results')?.textContent || ''",
                    returnByValue = true,
                }, timeout.Token);
                if (response.RootElement.TryGetProperty("error", out var protocolError))
                {
                    if (protocolError.GetProperty("code").GetInt32() != -32000 ||
                        protocolError.GetProperty("message").GetString() != "Execution context was destroyed.")
                        throw new InvalidOperationException($"Browser protocol failed: {protocolError}");
                }
                else
                {
                    var result = response.RootElement.GetProperty("result");
                    if (result.TryGetProperty("exceptionDetails", out var exception))
                        throw new InvalidOperationException($"Browser evaluation failed: {exception}");
                    var json = result.GetProperty("result").GetProperty("value").GetString();
                    if (!string.IsNullOrEmpty(json))
                        return JsonSerializer.Deserialize<BrowserResult>(json)
                            ?? throw new InvalidOperationException("Empty browser result.");
                }

                await Task.Delay(50, timeout.Token);
            }
            throw new TimeoutException("Draft browser scenario did not publish a result.");
        }
        finally
        {
            if (port > 0) await CloseBrowserAsync(port);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<int> WaitForDevToolsPortAsync(string profile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(profile, "DevToolsActivePort");
        for (var attempt = 0; attempt < 300; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path) &&
                    int.TryParse((await File.ReadAllLinesAsync(path, cancellationToken)).FirstOrDefault(), out var port))
                    return port;
            }
            catch (IOException) { }
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException("Headless browser did not expose its DevTools endpoint.");
    }

    private static async Task<JsonDocument> InvokeCdpAsync(
        ClientWebSocket client,
        int id,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await client.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, cancellationToken);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"Browser closed the DevTools socket during {method}.");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            var document = JsonDocument.Parse(message.ToArray());
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return document;
            document.Dispose();
        }
    }

    private static async Task CloseBrowserAsync(int port)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var version = JsonDocument.Parse(await http.GetStringAsync("/json/version", timeout.Token));
            var socket = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(socket), timeout.Token);
            using var response = await InvokeCdpAsync(client, 1, "Browser.close", new { }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or OperationCanceledException
            or InvalidOperationException or JsonException or IOException)
        {
        }
    }

    private static string FindStorePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName,
                "src", "Clayzor.Lib.Web.Controls", "wwwroot", "js", "clayDraftStore.js");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Production clayDraftStore.js was not found from the test output directory.");
    }

    private static string FindBrowser()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("CLAYZOR_BROWSER"),
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            "/usr/bin/chromium",
            "/usr/bin/google-chrome",
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new InvalidOperationException("Chrome/Edge is required for production draft browser tests.");
    }

    private sealed record BrowserResult(
        [property: JsonPropertyName("passed")] bool Passed,
        [property: JsonPropertyName("error")] string? Error);
}
