using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки фактического типа оборудования перед записью дочерних секций.</summary>
public sealed class Eq8ChildTypeGuardsTests
{
    /// <summary>Подходящие роли не разрешают запись секции оборудованию неподходящего фактического типа.</summary>
    [Theory]
    [InlineData("account-add")]
    [InlineData("account-update")]
    [InlineData("account-delete")]
    [InlineData("software")]
    [InlineData("sim-save")]
    [InlineData("sim-delete")]
    [InlineData("sim-service")]
    public async Task WrongActualType_RejectsChildWriteBeforeDml(string operation)
    {
        var connection = WriteConnection(operation, suitableType: false, includeDml: false);
        var service = CreateService(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(service, operation));

        AssertNoChildDml(connection);
    }

    /// <summary>Подходящий фактический тип проверяется отдельным запросом, после чего выполняется исходный DML.</summary>
    [Theory]
    [InlineData("account-add", "INSERT ОборудованиеУчётныеЗаписи")]
    [InlineData("account-update", "UPDATE ОборудованиеУчётныеЗаписи")]
    [InlineData("account-delete", "DELETE ОборудованиеУчётныеЗаписи")]
    [InlineData("software", "INSERT vwОборудованиеПО")]
    [InlineData("sim-save", "UPDATE vwSIMКарты")]
    [InlineData("sim-delete", "DELETE vwSIMКарты")]
    [InlineData("sim-service", "INSERT vwSIMКартыДопУслугиСотОператора")]
    public async Task SuitableActualType_AllowsOriginalChildDml(string operation, string dmlMarker)
    {
        var connection = WriteConnection(operation, suitableType: true, includeDml: true);
        var service = CreateService(connection);

        await WriteAsync(service, operation);

        var roleQueries = operation.StartsWith("account-", StringComparison.Ordinal) ? 2 : 1;
        Assert.Equal(roleQueries + 2, connection.CommandLog.Count);
        Assert.Contains("vwОборудование E", connection.CommandLog[roleQueries]);
        Assert.Contains("КодОборудования = @EquipmentId", connection.CommandLog[roleQueries]);
        Assert.Contains(dmlMarker, connection.CommandLog[roleQueries + 1]);
    }

    /// <summary>Отсутствие сохранённого оборудования или модели закрывает прямую дочернюю запись.</summary>
    [Fact]
    public async Task MissingActualTypeContext_FailsClosed()
    {
        var connection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(),
        ]));
        var service = CreateService(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSoftwareInstallationAsync(ValidInstallation()));

        AssertNoChildDml(connection);
    }

    /// <summary>Отдельные чтения пароля и PIN/PUK не загружают write-only контекст типа.</summary>
    [Fact]
    public async Task SecretReads_DoNotLoadChildWriteType()
    {
        DapperColumnMapper.Initialize();
        var accountConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("Пароль", "secret"))),
        ]));
        var simConnection = new ScriptedConnection(new Queue<ScriptEntry>([
            Script.Scalar(1), Script.Rows(Script.Row(("PIN", "1234"), ("PIN2", "12345"),
                ("PUK", "12345678"), ("PUK2", "87654321"))),
        ]));

        Assert.Equal("secret", await CreateService(accountConnection).GetAccountPasswordAsync(7, "test"));
        Assert.Equal("1234", (await CreateService(simConnection).GetSimSecretsAsync(7)).Pin);

        foreach (var connection in new[] { accountConnection, simConnection })
        {
            Assert.Equal(2, connection.CommandLog.Count);
            Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("vwОборудование E"));
        }
    }

    private static ScriptedConnection WriteConnection(string operation, bool suitableType, bool includeDml)
    {
        var entries = new List<ScriptEntry>();
        if (operation.StartsWith("account-", StringComparison.Ordinal))
            entries.Add(Script.Scalar(1));
        entries.Add(Script.Scalar(1));
        entries.Add(Script.Rows(Script.Row(
            ("КодТипаОборудования", 30),
            ("ЕстьMACадрес", (byte)(suitableType && operation.StartsWith("account-", StringComparison.Ordinal) ? 1 : 0)),
            ("ЕстьХарактеристикиКомпьютера", (byte)(suitableType && operation == "software" ? 1 : 0)),
            ("ЕстьХарактеристикиSIM", (byte)(suitableType && operation.StartsWith("sim-", StringComparison.Ordinal) ? 1 : 0)))));
        if (includeDml)
            entries.Add(Script.NonQuery());
        return new ScriptedConnection(new Queue<ScriptEntry>(entries));
    }

    private static Task WriteAsync(EquipmentFormService service, string operation) => operation switch
    {
        "account-add" => service.AddAccountAsync(Account()),
        "account-update" => service.UpdateAccountAsync(Account()),
        "account-delete" => service.DeleteAccountAsync(Account()),
        "software" => service.SaveSoftwareInstallationAsync(ValidInstallation()),
        "sim-save" => service.SaveSimAsync(ValidSim()),
        "sim-delete" => service.DeleteSimAsync(7),
        "sim-service" => service.SaveSimServiceAsync(ValidSimService()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static EquipmentAccount Account() => new()
    {
        EquipmentId = 7,
        Login = "test",
        Password = "secret",
        Notes = "note",
    };

    private static EquipmentSoftwareInstallation ValidInstallation() => new()
    {
        EquipmentId = 7,
        SoftwareId = 14,
        InstalledAt = new DateTime(2026, 11, 1),
        UninstalledAt = new DateTime(2026, 11, 17),
        Installed = "Version",
        Distribution = "Dist",
    };

    private static EquipmentSimCard ValidSim() => new()
    {
        EquipmentId = 7,
        PhoneNumber = "79000000001",
        ActivatedAtUtc = new DateTime(2026, 11, 18, 8, 0, 0),
        ContractId = 10,
        Secrets = new EquipmentSimSecrets
        {
            Pin = "1234", Pin2 = "12345", Puk = "12345678", Puk2 = "87654321",
        },
    };

    private static EquipmentSimService ValidSimService() => new()
    {
        EquipmentId = 7,
        PhoneNumber = "79000000001",
        ServiceTypeId = "_TEST",
        ServiceName = "Test",
        DateFrom = DateTime.Today,
    };

    private static void AssertNoChildDml(ScriptedConnection connection)
    {
        Assert.DoesNotContain(connection.CommandLog, sql =>
            sql.Contains("INSERT ") || sql.Contains("UPDATE ") || sql.Contains("DELETE ") || sql.Contains("sp_"));
    }

    private static EquipmentFormService CreateService(ScriptedConnection connection)
    {
        DapperColumnMapper.Initialize();
        return new EquipmentFormService(
            new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection),
            new TestAuthenticationStateProvider());
    }
}
