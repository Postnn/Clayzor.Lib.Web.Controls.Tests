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
        var provider = context.Render<MudDialogProvider>();
        var reference = await ShowAsync(context, provider);

        var dialog = provider.FindComponent<EquipmentAccountDialog>();
        dialog.WaitForAssertion(() => Assert.Contains("сохранение заблокировано", dialog.Markup));
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
            Script.Scalar(1), Script.Rows(Script.Row(("Пароль", "test-only-secret"))),
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

    private static BunitContext CreateContext(ScriptedConnection connection)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
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
