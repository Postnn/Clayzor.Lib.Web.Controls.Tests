using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Themes;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class ClayFieldComputedStyleBrowserTests
{
    [ChromeFixtureFact]
    public async Task RequiredAndFocusStylesUseProductionMarkupInEveryTheme()
    {
        var browser = Environment.GetEnvironmentVariable("CLAYZOR_BROWSER");
        Assert.True(File.Exists(browser) &&
            FileVersionInfo.GetVersionInfo(browser!).ProductName == "Google Chrome",
            "CLAYZOR_BROWSER must explicitly name the installed Google Chrome executable.");
        var root = FindRoot();
        var mud = FindMudCss();
        var clay = await File.ReadAllTextAsync(Path.Combine(root, "src", "Clayzor.Lib.Web.Controls", "wwwroot", "css", "clay.css"));
        var fixtureRoot = Path.Combine(root, "TestResults", "form03-form05-computed");
        Directory.CreateDirectory(fixtureRoot);
        var failures = new List<string>();
        foreach (var theme in ClayThemeCatalog.Available)
        {
            var layout = await RenderFieldsAsync(theme);
            var file = Path.Combine(fixtureRoot, theme.ToString() + ".html");
            await File.WriteAllTextAsync(file, "<!doctype html><meta charset=utf-8><style>" + mud +
                "</style><style>" + clay + "</style>" + layout + Probe);
            var result = await ReadChromeAsync(browser!, file);
            await File.WriteAllTextAsync(Path.ChangeExtension(file, ".json"), result);
            using var parsed = JsonDocument.Parse(result);
            failures.AddRange(parsed.RootElement.GetProperty("errors").EnumerateArray()
                .Select(error => $"{theme}: {error.GetString()}"));
        }
        Assert.Empty(failures);
    }

    private static async Task<string> RenderFieldsAsync(ClayThemePreference theme)
    {
        await using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Render<MudPopoverProvider>();
        var tokens = ctx.Render<ClayThemeTokenProvider>(args => args
            .Add(x => x.Theme, ClayTheme.Create(theme))
            .Add(x => x.Preference, theme)
            .Add(x => x.IsDarkMode, theme == ClayThemePreference.ClassicDark)
            .Add(x => x.ObserveSystemDarkModeChange, false));
        var result = new StringBuilder(tokens.Markup);
        result.Append($"<template data-clay-theme='{theme.Serialize()}'></template>");
        result.Append("<main class='clay-entity-form' style='background:var(--mud-palette-surface)'>");
        var item = new Choice(1, "Выбран");
        var now = new DateTime(2026, 11, 26);
        foreach (var state in new[] { "empty", "filled", "cleared", "disabled", "readonly" })
        {
            var filled = state is "filled" or "cleared";
            var disabled = state == "disabled";
            var readOnly = state == "readonly";
            T Opt<T>() where T : ClayInputOptions, new() => new()
            { Required = true, Label = "Поле", Disabled = disabled, ReadOnly = readOnly };
            void Add(string name, string markup) =>
                result.Append($"<div data-case='{name}' data-state='{state}'>{markup}</div>");
            var textbox = ctx.Render<ClayTextBox>(a => a.Add(x => x.Options, Opt<ClayTextBoxOptions>()).Add(x => x.Value, filled ? "Текст" : ""));
            var textarea = ctx.Render<ClayTextArea>(a => a.Add(x => x.Options, Opt<ClayTextAreaOptions>()).Add(x => x.Value, filled ? "Текст" : ""));
            var nativeOptions = Opt<ClayTextAreaOptions>();
            nativeOptions.BrowserManaged = true;
            var native = ctx.Render<ClayTextArea>(a => a.Add(x => x.Options, nativeOptions).Add(x => x.Value, filled ? "Текст" : ""));
            var number = ctx.Render<ClayNumber>(a => a.Add(x => x.Options, Opt<ClayNumberOptions>()).Add(x => x.Value, filled ? 0m : (decimal?)null));
            var date = ctx.Render<ClayDatePicker>(a => a.Add(x => x.Options, Opt<ClayDatePickerOptions>()).Add(x => x.Value, filled ? now : (DateTime?)null));
            var time = ctx.Render<ClayTime>(a => a.Add(x => x.Options, Opt<ClayTimeOptions>()).Add(x => x.Value, filled ? TimeSpan.Zero : (TimeSpan?)null));
            var drop = ctx.Render<ClayDropDownList<Choice, int?>>(a => a.Add(x => x.Options, Opt<ClayDropDownListOptions>())
                .Add(x => x.Items, [item]).Add(x => x.TextSelector, value => value.Name)
                .Add(x => x.ValueSelector, value => value.Id).Add(x => x.Value, filled ? 1 : (int?)null));
            var combo = ctx.Render<ClayComboBox<Choice>>(a => a.Add(x => x.Required, true)
                .Add(x => x.Label, "Поле").Add(x => x.Items, [item]).Add(x => x.Value, filled ? 1 : 0));
            var select = ctx.Render<ClaySelect<Choice, int?>>(a => a
                .Add(x => x.Options, new ClaySelectOptions { Required = true, Label = "Поле", Disabled = disabled, ReadOnly = readOnly })
                .Add(x => x.Items, [item]).Add(x => x.TextSelector, value => value.Name)
                .Add(x => x.ValueSelector, value => value.Id).Add(x => x.Value, filled ? 1 : (int?)null));
            if (state == "cleared")
            {
                textbox.Render(a => a.Add(x => x.Value, ""));
                textarea.Render(a => a.Add(x => x.Value, ""));
                native.Render(a => a.Add(x => x.Value, ""));
                number.Render(a => a.Add(x => x.Value, (decimal?)null));
                date.Render(a => a.Add(x => x.Value, (DateTime?)null));
                time.Render(a => a.Add(x => x.Value, (TimeSpan?)null));
                drop.Render(a => a.Add(x => x.Value, (int?)null));
                combo.Render(a => a.Add(x => x.Value, 0));
                select.Render(a => a.Add(x => x.Value, (int?)null));
            }
            Add("textbox", textbox.Markup); Add("textarea", textarea.Markup); Add("native-textarea", native.Markup);
            Add("number", number.Markup); Add("date", date.Markup); Add("time", time.Markup);
            Add("dropdown", drop.Markup); Add("select", select.Markup);
            if (!disabled && !readOnly)
                Add("combo", combo.Markup);
        }
        result.Append("<div data-case='toolbar' data-state='button'>");
        result.Append(ctx.Render<ClayToolbarButton>(a => a.Add(x => x.Options, new ClayToolbarButtonOptions { Text = "Действие" })).Markup);
        result.Append("</div></main>");
        return result.ToString();
    }

    private static async Task<string> ReadChromeAsync(string chrome, string file)
    {
        var profile = Path.Combine(Path.GetTempPath(), "clay-fields-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        foreach (var option in new[] { "--headless=new", "--disable-background-networking", "--no-first-run",
            "--disable-gpu", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--user-data-dir=" + profile, new Uri(file).AbsoluteUri })
            start.ArgumentList.Add(option);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var port = 0;
            while (port == 0)
            {
                try
                {
                    if (File.Exists(portFile))
                        int.TryParse((await File.ReadAllLinesAsync(portFile, deadline.Token)).FirstOrDefault(), out port);
                }
                catch (IOException) { }
                if (port == 0)
                    await Task.Delay(50, deadline.Token);
            }
            using var http = new HttpClient();
            string? socket = null;
            var expected = new Uri(file).AbsoluteUri;
            while (socket is null)
            {
                using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", deadline.Token));
                foreach (var page in pages.RootElement.EnumerateArray())
                    if (page.GetProperty("type").GetString() == "page" && page.GetProperty("url").GetString() == expected)
                        socket = page.GetProperty("webSocketDebuggerUrl").GetString();
                if (socket is null)
                    await Task.Delay(50, deadline.Token);
            }
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(socket), deadline.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = """document.getElementById("result")?.textContent || window.fixtureError || null""",
                    returnByValue = true,
                },
            });
            var buffer = new byte[8192];
            while (true)
            {
                await client.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, deadline.Token);
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await client.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                        if (part.MessageType == WebSocketMessageType.Close)
                            throw new InvalidOperationException("Chrome closed the fixture connection.");
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    using var response = JsonDocument.Parse(message.ToArray());
                    if (!response.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != 1)
                        continue;
                    if (response.RootElement.TryGetProperty("result", out var evaluated) &&
                        evaluated.GetProperty("result").TryGetProperty("value", out var value) &&
                        value.ValueKind == JsonValueKind.String)
                        return value.GetString()!;
                    break;
                }
                await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (Directory.Exists(profile))
            {
                var resolved = Path.GetFullPath(profile);
                Assert.StartsWith(Path.Combine(Path.GetTempPath(), "clay-fields-"), resolved, StringComparison.OrdinalIgnoreCase);
                try { Directory.Delete(resolved, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Clayzor.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Clayzor.sln");
    }

    private static string FindMudCss()
    {
        var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Clayzor.Lib.Web.Controls.Tests.deps.json")));
        var library = dependencies.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(item => item.Name.StartsWith("MudBlazor/", StringComparison.Ordinal)).Value;
        var cached = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return File.ReadAllText(Path.Combine(cached, library.GetProperty("path").GetString()!, "staticwebassets", "MudBlazor.min.css"));
    }

    private sealed record Choice(int Id, string Name) : ILookupEntity;

    private const string Probe = """
        <script>
        window.addEventListener("error", event => window.fixtureError = event.message);
        const samples = [], errors = [];
        function rgb(value) { return (value.match(/[\d.]+/g) || []).map(Number); }
        function luminance(value) {
            const [r,g,b] = rgb(value).slice(0,3).map(channel => {
                const unit = channel/255;
                return unit < .04045 ? unit/12.92 : ((unit+.055)/1.055) ** 2.4;
            });
            return .2126*r + .7152*g + .0722*b;
        }
        function background(element) {
            for (let node=element; node; node=node.parentElement) {
                const color=getComputedStyle(node).backgroundColor;
                const channels=rgb(color);
                if (channels.length === 3 || channels[3] > 0) return color;
            }
            return "rgb(255, 255, 255)";
        }
        for (const tile of document.querySelectorAll("[data-case]")) {
            const {case: family,state} = tile.dataset;
            const input = tile.querySelector("textarea,input:not([type=hidden]),[role=combobox][tabindex],button");
            if (!input) { errors.push(family + " " + state + ": no input"); continue; }
            const field = input.closest(".mud-input") || input;
            const bg = background(field);
            const expectedYellow = state === "empty" || state === "cleared";
            const yellow = rgb(bg).join(",") === "255,255,153";
            if (expectedYellow !== yellow) errors.push(family + " " + state + ": background " + bg);
            const value = input.value ?? input.textContent.trim();
            if (state === "filled" && !value) errors.push(family + ": no displayed value");
            const focusTargets = ["empty","filled","cleared","button"].includes(state);
            let outline, contrast, keyboardVisible;
            if (focusTargets) {
                input.focus();
                keyboardVisible = input.matches(":focus-visible");
                const style = [field,input].map(item => getComputedStyle(item))
                    .find(item => item.outlineStyle !== "none" && parseFloat(item.outlineWidth) >= 2);
                outline = style?.outlineColor;
                const outlineLum = outline ? luminance(outline) : 0;
                const fillLum = luminance(bg);
                contrast = outline ? (Math.max(outlineLum,fillLum)+.05)/(Math.min(outlineLum,fillLum)+.05) : 0;
                if (!keyboardVisible || contrast < 3) errors.push(family + " " + state + ": focus " + outline + " / " + contrast);
            }
            samples.push({family,state,value,bg,keyboardVisible,outline,contrast});
        }
        const result = document.createElement("pre");
        result.id = "result";
        result.textContent = JSON.stringify({browser:navigator.userAgent,samples,errors},null,2);
        document.body.replaceChildren(result);
        </script>
        """;
}

public sealed class ChromeFixtureFactAttribute : FactAttribute
{
    public ChromeFixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAYZOR_BROWSER")))
            Skip = "Set CLAYZOR_BROWSER to the installed Google Chrome executable.";
    }
}
