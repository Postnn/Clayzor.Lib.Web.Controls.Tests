using Bunit;
using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки fail-closed чтения пароля в отрисованном диалоге учётной записи.</summary>
public sealed class EquipmentAccountDialogTests
{
    /// <summary>Ошибка чтения блокирует OK и не приводит к SQL UPDATE.</summary>
    [Fact]
    public async Task PasswordReadFailure_DisablesApplyAndDoesNotWrite()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Error(SqlExceptionFactory.Create(53, "connection error")),
        ]));
        await using var context = CreateContext(connection);
        var popovers = context.Render<MudPopoverProvider>();
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowAsync(context, provider);

        var dialog = provider.FindComponent<EquipmentAccountDialog>();
        dialog.WaitForAssertion(() => Assert.Contains("сохранение заблокировано", dialog.Markup));
        await dialog.FindAll("input")[2].InputAsync("несохранённое примечание");
        var readsBefore = connection.CommandLog.Count;
        await EquipmentPeriodDialogHelpTests.CheckHelpAndChangeThemeAsync(context, provider, popovers, "EquipmentLogin_form.aspx");
        Assert.Equal("несохранённое примечание", dialog.Instance.Notes);
        Assert.Equal(string.Empty, dialog.Instance.Password);
        Assert.Equal(readsBefore, connection.CommandLog.Count);
        Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        Assert.False(reference.Result.IsCompleted);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));

        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").Click();
        Assert.True((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2))).Canceled);
    }

    /// <summary>Изменение примечания возвращает ранее прочитанный пароль без его замены пустым.</summary>
    [Fact]
    public async Task NotesEdit_PreservesLoadedPassword()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("Password", "test-only-secret"), ("EquipmentId", 7), ("Login", "test"))),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowAsync(context, provider);
        var dialog = provider.FindComponent<EquipmentAccountDialog>();
        dialog.WaitForAssertion(() => Assert.Equal("test-only-secret", dialog.Instance.Password));

        dialog.Instance.Notes = "changed notes";
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").Click();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var value = Assert.IsType<EquipmentAccountDialogResult>(result.Data);
        Assert.Equal("changed notes", value.Notes);
        Assert.Equal("test-only-secret", value.Password);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
    }

    /// <summary>Ошибка валидации логина не блокирует повторное подтверждение после ввода.</summary>
    [Fact]
    public async Task EmptyLoginThenCorrection_AllowsApply()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>());
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentAccountDialog>(
            "Account", new DialogParameters<EquipmentAccountDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.OriginalLogin, null },
                { dialog => dialog.Login, string.Empty },
            });
        var dialog = provider.FindComponent<EquipmentAccountDialog>();

        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        dialog.WaitForAssertion(() => Assert.Contains("Укажите логин", dialog.Markup));
        await dialog.Find("input").InputAsync("svc-admin");
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").ClickAsync();
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));

        var value = Assert.IsType<EquipmentAccountDialogResult>(result.Data);
        Assert.Equal("svc-admin", value.Login);
    }

    /// <summary>Отсутствие записи и отсутствие права остаются fail-closed после редактирования полей.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordReadMissingOrUnauthorized_StaysDisabledAfterEdits(bool missingRow)
    {
        var entries = missingRow
            ? new[] { Script.Scalar(1), Script.Rows() }
            : new[] { Script.Scalar(0) };
        var connection = new ScriptedConnection(new Queue<ScriptEntry>(entries));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowAsync(context, provider);
        var dialog = provider.FindComponent<EquipmentAccountDialog>();

        dialog.WaitForAssertion(() => Assert.True(dialog.FindAll("button")
            .Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled")));
        await dialog.FindAll("input")[1].InputAsync("replacement-secret");
        await dialog.FindAll("input")[2].InputAsync("changed notes");

        Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        Assert.False(reference.Result.IsCompleted);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("UPDATE"));
        await dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Отмена").ClickAsync();
        Assert.True((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2))).Canceled);
    }

    /// <summary>Устаревший или старый формат черновика сохраняет raw и требует явной сверки перед записью.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("obsolete-opaque-version")]
    public async Task RestoredVersionMismatch_PreservesOrdinaryRawWithAuthorizedPasswordAndBlocksSave(string? originalVersion)
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(
                ("Password", "fixture-server-value"), ("EquipmentId", 7), ("Login", "test"), ("Notes", "server notes"))),
        ]));
        await using var context = CreateContext(connection);
        var provider = context.Render<MudDialogProvider>();
        var ready = new TaskCompletionSource<IRenderedComponent<EquipmentAccountDialog>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editorContext = new EquipmentEditorContext(7, 1, () => true)
        {
            DraftState = System.Text.Json.JsonSerializer.Serialize(new { OriginalVersion = originalVersion, Notes = "local notes" }),
            RawRestore = async () =>
            {
                var editor = await ready.Task;
                await editor.Find("input[data-draft-key='account.notes']").InputAsync("fixture-local-raw");
            },
        };
        var reference = await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentAccountDialog>(
            "Account", new DialogParameters<EquipmentAccountDialog>
            {
                { item => item.EditorContext, editorContext },
                { item => item.EquipmentId, 7 },
                { item => item.Login, "test" },
                { item => item.OriginalLogin, "test" },
            });
        var dialog = provider.FindComponent<EquipmentAccountDialog>();
        ready.SetResult(dialog);

        dialog.WaitForAssertion(() =>
        {
            Assert.Contains("Сверьте актуальные данные", dialog.Markup);
            Assert.Equal("fixture-local-raw", dialog.Instance.Notes);
            Assert.Equal("fixture-server-value", dialog.Instance.Password);
            Assert.True(dialog.FindAll("button").Single(button => button.TextContent.Trim() == "OK").HasAttribute("disabled"));
        });
        var typed = editorContext.CaptureDraftState();
        Assert.Contains("fixture-local-raw", typed);
        Assert.DoesNotContain("fixture-server-value", typed);
        Assert.False(reference.Result.IsCompleted);
        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
    }

    private static BunitContext CreateContext(ScriptedConnection connection)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        EquipmentPeriodDialogHelpTests.AddAppearance(context);
        context.Services.AddScoped<Clayzor.Lib.Web.Controls.Services.ClayDraftService>();
        context.Services.AddSingleton(new EquipmentFormService(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection), new TestAuthenticationStateProvider()));
        return context;
    }

    private static async Task<IDialogReference> ShowAsync(BunitContext context, IRenderedComponent<MudDialogProvider> provider)
    {
        _ = provider;
        return await context.Services.GetRequiredService<IDialogService>().ShowAsync<EquipmentAccountDialog>(
            "Account", new DialogParameters<EquipmentAccountDialog>
            {
                { dialog => dialog.EquipmentId, 7 },
                { dialog => dialog.OriginalLogin, "test" },
                { dialog => dialog.Login, "test" },
            });
    }
}
