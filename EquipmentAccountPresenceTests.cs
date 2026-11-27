using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    /// <summary>Диалог сохранённой записи получает непрозрачную цель при настроенном секрете.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountDialogPresenceUsesSavedAccountAndConfiguredKey(bool configured)
    {
        var fixture = CreateFixture(network: true);
        fixture.Service.Roles.Add((int)Роли.Дежурный_инженер);
        var account = new EquipmentAccount { EquipmentId = 7, Login = "fixture-account" };
        fixture.Service.StoredAccounts.Add(account);
        await using var context = fixture.Context;
        if (configured)
            context.Services.GetRequiredService<IConfiguration>()[EquipmentFormService.AccountPresenceKeySetting] = TestAccountPresenceKey();

        var provider = context.Render<MudDialogProvider>();
        var page = context.Render<EquipmentForm>();
        WaitForSn(page, "CLAY_TEST_7");
        var dialogAdded = TrackNextDialog(context);
        var pending = InvokeHandler(page, "OpenAccountDialogAsync", account);
        var reference = await dialogAdded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dialog = provider.FindComponent<EquipmentAccountDialog>();

        if (configured)
        {
            var target = Assert.IsType<ClayPresenceTarget>(dialog.Instance.PresenceTarget);
            Assert.Equal("equipment-account", target.Key.EntityType);
            Assert.DoesNotContain(account.Login, target.Key.SerializedKey, StringComparison.Ordinal);
            Assert.Same(target, dialog.FindComponent<ClayPresencePanel>().Instance.Target);
        }
        else
        {
            Assert.Null(dialog.Instance.PresenceTarget);
            Assert.Contains("Не удалось получить сведения о присутствии", dialog.Markup);
        }

        await provider.InvokeAsync(() => reference.Close(DialogResult.Cancel()));
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static string TestAccountPresenceKey() => Convert.ToBase64String(Enumerable.Repeat((byte)71, 32).ToArray());
}

/// <summary>Проверки ключа и серверных ограничений отдельного присутствия учётной записи.</summary>
public sealed class EquipmentAccountPresenceTests
{
    private const string CanonicalLogin = "fixture-account";
    private static string ConfiguredKey => Convert.ToBase64String(Enumerable.Repeat((byte)71, 32).ToArray());

    /// <summary>Одинаковый секрет связывает разные процессы и разделяет сохранённые записи.</summary>
    [Fact]
    public void AccountKeyIsOpaqueAndUsesBothStoredIdentityParts()
    {
        using var db = new DbManager("Server=fixture");
        var a = new EquipmentFormService(db);
        var b = new EquipmentFormService(db);
        var first = Assert.IsType<EquipmentChildPresence>(a.CreateAccountPresence(7, CanonicalLogin, ConfiguredKey));
        Assert.Equal(first.Key, b.CreateAccountPresence(7, CanonicalLogin, ConfiguredKey)!.Key);
        Assert.NotEqual(first.Key, b.CreateAccountPresence(8, CanonicalLogin, ConfiguredKey)!.Key);
        Assert.NotEqual(first.Key, b.CreateAccountPresence(7, "other-account", ConfiguredKey)!.Key);
        Assert.NotEqual(first.Key, b.CreateAccountPresence(7, CanonicalLogin, Convert.ToBase64String(new byte[32]))!.Key);
        Assert.DoesNotContain(CanonicalLogin, first.Key.SerializedKey, StringComparison.Ordinal);
        Assert.Null(a.CreateAccountPresence(7, CanonicalLogin, null));
        Assert.Null(a.CreateAccountPresence(7, CanonicalLogin, "invalid"));
        Assert.Null(a.CreateAccountPresence(0, CanonicalLogin, ConfiguredKey));
    }

    /// <summary>Heartbeat сверяет роли и точное серверное написание, запрашивая только логин строки.</summary>
    [Theory]
    [InlineData(true, CanonicalLogin, true)]
    [InlineData(true, "FIXTURE-ACCOUNT", false)]
    [InlineData(true, null, false)]
    [InlineData(false, CanonicalLogin, false)]
    public async Task HeartbeatRechecksCanonicalRowWithoutReadingPassword(bool globalEngineer, string? storedLogin, bool visible)
    {
        DapperColumnMapper.Initialize();
        var scripts = new Queue<ScriptEntry>(
        [
            Script.Rows(Script.Row(("КодСотрудника", 4))),
            Script.Rows(
                Script.Row(("RoleId", (int)Роли.Дежурный_инженер), ("PersonId", globalEngineer ? 0 : 31)),
                Script.Row(("RoleId", (int)Роли.Специалист_по_инвентаризации_оборудования), ("PersonId", 0))),
        ]);
        if (globalEngineer)
        {
            scripts.Enqueue(Script.Rows(Script.Row(("ЕстьMACадрес", 1))));
            scripts.Enqueue(storedLogin is null ? Script.Rows() : Script.Rows(Script.Row(("Логин", storedLogin))));
        }
        var connection = new ScriptedConnection(scripts, emulateEquipmentIdentity: false);
        using var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        var service = new EquipmentFormService(db, new TestAuthenticationStateProvider());
        var subject = service.CreateAccountPresence(7, CanonicalLogin, ConfiguredKey)!;

        var result = await EquipmentFormService.ReadChildPresenceAccessAsync(db, subject, "fixture", 4, default);

        Assert.Equal(visible, result is { EmployeeId: 4, Activity: ClayPresenceActivity.Editing });
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("Пароль", StringComparison.OrdinalIgnoreCase));
        if (globalEngineer)
        {
            Assert.Equal(EquipmentChildPresenceQueries.AccountPresenceLogin, connection.CommandLog[^1]);
            Assert.Equal(7, connection.ParameterLog[^1]["EquipmentId"]);
            Assert.Equal(CanonicalLogin, connection.ParameterLog[^1]["Login"]);
            Assert.Equal(50, connection.ParameterSizeLog[^1]["Login"]);
        }
        else
            Assert.DoesNotContain(EquipmentChildPresenceQueries.AccountPresenceLogin, connection.CommandLog);
    }
}
