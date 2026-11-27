using System.Text.Json;
using Bunit;
using NetPage = Kesco.App.Web.Inventory.Components.Pages.Net.Net;

using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class NetFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetInputPersistsAndRestoresAgainstOriginalVersion(bool stale)
    {
        var original = new NetFixture(description: "Исходные данные");
        string snapshot;
        await using (original.Context)
        {
            var first = original.Context.Render<NetPage>();
            WaitForLoad(first);
            await first.Find("textarea[data-draft-key='net.description']").InputAsync("Мой ввод до OK");
            first.WaitForAssertion(() => Assert.NotEmpty(original.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]));
            var saved = original.Context.JSInterop.Invocations["clayDraftStore.saveTypedScope"].Last();
            Assert.Equal("net-input", saved.Arguments[1]);
            snapshot = Assert.IsType<string>(saved.Arguments[2]);
            using var savedJson = JsonDocument.Parse(snapshot);
            Assert.Equal("Исходные данные", savedJson.RootElement.GetProperty("Original").GetProperty("Description").GetString());
            Assert.Equal("Мой ввод до OK", savedJson.RootElement.GetProperty("Current").GetProperty("Description").GetString());
        }

        var reopened = new NetFixture(description: stale ? "Чужие данные" : "Исходные данные", restoredInput: snapshot);
        await using var context = reopened.Context;
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() => Assert.Equal("Мой ввод до OK", Model(page.Instance).Description));

        if (stale)
        {
            Assert.Contains("Исходная версия черновика отличается", page.Markup);
            await Invoke(page, "SaveAsync", false);
        }
        else
        {
            page.WaitForAssertion(() => Assert.DoesNotContain("Восстановление ввода ещё не завершено", page.Markup));
        }
        Assert.DoesNotContain(reopened.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetRawOnlyInputAppliesToOwnedScopeAndKeepsSqlBlocked()
    {
        var fixture = new NetFixture(restoredRaw: true, rawPending: true);
        await using var context = fixture.Context;
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["clayDraftStore.applyRaw"]));
        Assert.Equal("net-input", context.JSInterop.Invocations["clayDraftStore.applyRaw"].Single().Arguments[1]);
        Assert.NotEmpty(page.FindAll("[data-draft-scope='net-input'] [data-draft-key='net.return-date'], [data-draft-scope='net-input'] [data-draft-key='net.description']"));

        await page.Find("textarea[data-draft-key='net.description']").InputAsync("Локальный ввод");
        await Invoke(page, "SaveAsync", false);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
    }
}

public sealed partial class NetFormTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task NetStaleDraftRebaseRequiresSameFreshVersionAndKeepsUnselectedRaw(
        bool keepLocal, bool freshChangedAgain, bool storageAccepts)
    {
        static object Input(string description) => new
        {
            CustomerId = 1,
            Connected = "",
            Description = description,
            HasReturnDate = false,
            SwitchToId = -1,
            ExchangeWithId = -1,
        };
        var initial = new DateTime(2026, 11, 21, 10, 0, 0);
        var stored = JsonSerializer.Serialize(new
        {
            PortId = 10,
            ChangedAt = initial.AddMinutes(-1),
            Original = Input("До правки"),
            Current = Input("Мой текст"),
        });
        var fixture = new NetFixture(description: "Чужая правка", restoredInput: stored, restoredRaw: true);
        await using var context = fixture.Context;
        context.JSInterop.Setup<ClayDraftRebaseState>("clayDraftStore.readRebaseState", _ => true)
            .SetResult(new ClayDraftRebaseState(9, ["data:net.description:0", "data:net.connected:0"]));
        context.JSInterop.Setup<bool>("clayDraftStore.rebaseScope", _ => true).SetResult(storageAccepts);
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => Equals(call.Arguments[1], "net-input")));

        page.WaitForAssertion(() => Assert.False(page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Сверить данные порта").HasAttribute("disabled")));
        ReadNetReviewFixture(fixture, "Чужая правка", initial);
        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Сверить данные порта").ClickAsync());

        Assert.Null(GetPrivate<string>(page.Instance, "_validationMessage"));
        var review = page.Find("[data-testid='net-input-review']");
        Assert.Contains("До правки", review.TextContent);
        Assert.Contains("Мой текст", review.TextContent);
        Assert.Contains("Чужая правка", review.TextContent);
        var choice = Assert.Single(page.FindComponents<ClayRadioGroup<bool?>>());
        await page.InvokeAsync(() => choice.Instance.ValueChanged.InvokeAsync(keepLocal));

        ReadNetReviewFixture(fixture, freshChangedAgain ? "Новые данные" : "Чужая правка",
            freshChangedAgain ? initial.AddSeconds(1) : initial);
        if (!freshChangedAgain && storageAccepts)
            fixture.Enqueue(Script.Rows(Script.Row(("КодЛица", 1), ("Кличка", "Тестовая организация"))));
        await page.InvokeAsync(() => page.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Применить результаты сверки").ClickAsync());

        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.Contains("UPDATE vwЛокальнаяСеть", StringComparison.Ordinal));
        if (freshChangedAgain)
        {
            Assert.Empty(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
        }
        else
        {
            var rebase = Assert.Single(context.JSInterop.Invocations["clayDraftStore.rebaseScope"]);
            Assert.Equal(9L, rebase.Arguments[2]);
            var discarded = Assert.IsAssignableFrom<IReadOnlyList<string>>(rebase.Arguments[4]);
            if (keepLocal) Assert.Empty(discarded);
            else Assert.Equal("data:net.description:0", Assert.Single(discarded));
            using var saved = JsonDocument.Parse(Assert.IsType<string>(rebase.Arguments[3]));
            Assert.Equal("Чужая правка", saved.RootElement.GetProperty("Original").GetProperty("Description").GetString());
            Assert.Equal(keepLocal ? "Мой текст" : "Чужая правка",
                saved.RootElement.GetProperty("Current").GetProperty("Description").GetString());
        }

        if (freshChangedAgain || !storageAccepts)
        {
            Assert.Equal("Мой текст", Model(page.Instance).Description);
            Assert.Contains("Исходная версия черновика отличается", page.Markup);
        }
        else
        {
            Assert.Equal(keepLocal ? "Мой текст" : "Чужая правка", Model(page.Instance).Description);
            Assert.DoesNotContain("Исходная версия черновика отличается", page.Markup);
            page.WaitForAssertion(() => Assert.True(context.JSInterop.Invocations["clayDraftStore.applyRaw"].Count > 1));
        }
    }

    [Fact]
    public async Task NetOkKeepsOpenWhenOnlyRawInputArrivesAfterDispatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRaw = false;
        var fixture = new NetFixture(hasLateRaw: () => lateRaw);
        await using var context = fixture.Context;
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."))));
        fixture.AddSaveDbCommand(new ScriptEntry { Scalar = 0, Started = started, Release = release });
        var fresh = new DateTime(2026, 11, 21, 11, 0, 0);
        fixture.AddLoad(description: "Отправлено", changedAt: fresh, includeCustomerLookup: false);
        var page = context.Render<NetPage>();
        WaitForLoad(page);
        await page.InvokeAsync(() => page.Find("textarea[data-draft-key='net.description']").InputAsync("Отправлено"));

        var save = Invoke(page, "SaveAsync", true);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            lateRaw = true;
        }
        finally { release.TrySetResult(); }
        await save.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(fresh, Model(page.Instance).ChangedAtUtc);
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "clayModelEquipmentForm.close");
        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations["clayDraftStore.applyRaw"],
            call => Equals(call.Arguments[1], "net-input")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NetUnavailablePortDoesNotAttachPreviousRegistration(bool denied, bool verified)
    {
        var fixture = new NetFixture(currentUser: verified, missingPort: !denied,
            portReadError: denied ? SqlExceptionFactory.Create(229, "access denied") : null,
            restoredInput: OwnNetDraft(), restoredRaw: true, rawPending: true);
        await using var context = fixture.Context;
        var page = context.Render<NetPage>();

        page.WaitForAssertion(() => Assert.Contains(denied ? "Нет доступа к записи порта" : "не найден", page.Markup));
        Assert.Empty(page.FindAll("[data-testid='net-port-caption']"));
        Assert.Empty(page.FindAll("[data-testid='net-readonly-recovery']"));
        Assert.DoesNotContain("Сохранённый локальный черновик", page.Markup);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.register"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.registerRam"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.applyRaw"]);

        await Invoke(page, "SaveAsync", true);
        await Invoke(page, "RestartPoeAsync");
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    private static string OwnNetDraft() => JsonSerializer.Serialize(new
    {
        PortId = 10,
        ChangedAt = new DateTime(2026, 11, 21),
        Original = new { CustomerId = 1, Connected = "", Description = "", HasReturnDate = false },
        Current = new { CustomerId = 1, Connected = "", Description = "Сохранённый локальный черновик", HasReturnDate = false },
    });

    private static void ReadNetReviewFixture(NetFixture fixture, string description, DateTime changedAt)
    {
        fixture.Enqueue(Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Тестов А."))));
        fixture.Enqueue(Script.Rows(Script.Row(
            ("КодЛокальнойСети", 10), ("КодОборудования", 8), ("Порт", (byte)2),
            ("КодЛицаЗаказчика", 1), ("Подключено", ""), ("Примечания", description), ("Изменено", changedAt))));
    }
}
