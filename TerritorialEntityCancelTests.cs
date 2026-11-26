using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверка трёх решений на реальной карточке территории.</summary>
public sealed class TerritorialEntityCancelTests
{
    /// <summary>Свежее чтение и проверка ревизии предшествуют принятию сброса.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ResetReadsFirstAndKeepsDraftOnMissingOrNewInput(bool missing, bool accepted)
    {
        await using var fixture = new Fixture(missing, accepted);
        var opening = Button(fixture.Card, "Отмена").ClickAsync(new MouseEventArgs());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", fixture.Dialogs.Markup));
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        await Button(fixture.Dialogs, "Отменить мои изменения и загрузить из БД").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"].Count);
        Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.discard"]);
        Assert.DoesNotContain(fixture.Connection.CommandLog, sql => sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        if (missing)
            Assert.Empty(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
        else
        {
            var commit = Assert.Single(fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"]);
            Assert.Equal(7L, commit.Arguments[2]);
            using var saved = JsonDocument.Parse(Assert.IsType<string>(commit.Arguments[3]));
            Assert.Equal("FreshName", saved.RootElement.GetProperty("Original").GetProperty("Record").GetProperty("Name").GetString());
            Assert.Equal("FreshName", saved.RootElement.GetProperty("Current").GetProperty("Record").GetProperty("Name").GetString());
        }
        Assert.Contains(missing || !accepted ? "LocalName" : "FreshName", fixture.Card.Markup);
    }

    /// <summary>Продолжение работы не перечитывает БД, закрытие отбрасывает только принятую область.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueAndCloseDoNotReadTheDatabaseAgain(bool close)
    {
        await using var fixture = new Fixture();
        var before = fixture.Connection.CommandLog.Count;
        var opening = Button(fixture.Card, "Отмена").ClickAsync(new MouseEventArgs());
        fixture.Dialogs.WaitForAssertion(() => Assert.Contains("Несохранённые изменения", fixture.Dialogs.Markup));
        await Button(fixture.Dialogs, close ? "Закрыть без сохранения" : "Продолжить редактирование").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(before, fixture.Connection.CommandLog.Count);
        var replacing = fixture.Context.JSInterop.Invocations["clayDraftStore.replaceEditorScope"];
        if (!close)
        {
            Assert.Empty(replacing);
            Assert.Contains("LocalName", fixture.Card.Markup);
            return;
        }

        var discard = Assert.Single(replacing);
        Assert.Equal("main", discard.Arguments[1]);
        Assert.Null(discard.Arguments[3]);
        Assert.Single(fixture.Context.JSInterop.Invocations["clayModelEquipmentForm.close"]);
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> component, string text) where T : IComponent =>
        component.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(bool missing = false, bool acceptsRevision = true)
        {
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            DapperColumnMapper.Initialize();
            var employee = Script.Rows(Script.Row(("КодСотрудника", 4), ("Сотрудник", "Test")));
            var access = Script.Rows(Script.Row(("TerritoryUpdate", true)));
            var old = new TerritorialEntityRecord { Id = 4, Name = "OldName", ChangedAt = new DateTime(2026, 11, 25) };
            var script = new Queue<ScriptEntry>([
                employee, Script.Rows(), access, Territory("OldName", old.ChangedAt), Script.Rows(),
                employee, Script.Rows(), access, missing ? Script.Rows() : Territory("FreshName", old.ChangedAt.AddDays(1)),
                Script.Rows()]);
            Connection = new ScriptedConnection(script, emulateEquipmentIdentity: false);
            Context.Services.AddSingleton<DbManager>(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => Connection));
            Context.Services.AddScoped<ClayDraftService>();
            Context.Services.AddScoped<ClayFormWindowService>();
            Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            Context.Services.AddSingleton<AuthenticationStateProvider, FixtureAuthentication>();
            Context.Services.AddMudServices();
            var stored = JsonSerializer.Serialize(new
            {
                Original = new TerritorialEntityForm.FormState(old, null),
                Current = new TerritorialEntityForm.FormState(old with { Name = "LocalName" }, null)
            });
            Context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
                .SetResult(new ClayDraftRestore("territory-cancel", true, 7, null, null,
                    new Dictionary<string, string> { ["main"] = stored }));
            Context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", _ => true)
                .SetResult(new ClayDraftRawApplyResult([], []));
            Context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", _ => true)
                .SetResult(new ClayDraftRestoreReadiness(true, [], []));
            Context.JSInterop.Setup<ClayDraftEditorScopeState>("clayDraftStore.readEditorScope", _ => true)
                .SetResult(new ClayDraftEditorScopeState(7, true, false));
            Context.JSInterop.Setup<bool>("clayDraftStore.replaceEditorScope", _ => true).SetResult(acceptsRevision);
            Dialogs = Context.Render<MudDialogProvider>();
            Context.Render<MudPopoverProvider>();
            Context.Services.GetRequiredService<NavigationManager>().NavigateTo("/TerritorialEntity/TerritorialEntity_form?id=4");
            Card = Context.Render<TerritorialEntityForm>();
            Card.WaitForAssertion(() => Assert.Contains("LocalName", Card.Markup));
        }

        internal BunitContext Context { get; } = new();
        internal ScriptedConnection Connection { get; }
        internal IRenderedComponent<MudDialogProvider> Dialogs { get; }
        internal IRenderedComponent<TerritorialEntityForm> Card { get; }

        private static ScriptEntry Territory(string name, DateTime changed) =>
            Script.Rows(Script.Row(("Id", 4), ("Name", name), ("ChangedAt", changed)));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
}
