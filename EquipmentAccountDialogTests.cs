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

    private static BunitContext CreateContext(ScriptedConnection connection)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new EquipmentFormService(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection)));
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
