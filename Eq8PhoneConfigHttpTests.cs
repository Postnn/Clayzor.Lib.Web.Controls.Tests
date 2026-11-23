using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Components.Pages.Equipment;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки PhoneConfig через обычный HTTP pipeline без AuthenticationStateProvider.</summary>
public sealed class Eq8PhoneConfigHttpTests
{
    /// <summary>Допустимые HTTP identity и роль выдают конфигурацию и вызывают процедуру один раз.</summary>
    [Fact]
    public async Task AuthenticatedRole_ReturnsConfiguration()
    {
        var connection = CreateConnection(Script.Rows(EmployeeRow()), Script.Rows(Script.Row(("Value", 1))), Script.NonQuery());
        await using var host = await StartAsync(connection);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Address + "/Equipment/PhoneConfigHandler.ashx?mac=001122334455");
        request.Headers.Add("X-Test-User", "DOMAIN\\devadmin");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(connection.CommandLog, sql => sql.Contains("sp_КонфигурацияIpТелефона", StringComparison.Ordinal));
    }

    /// <summary>Отсутствие роли или несовпадение principal запрещает вызов процедуры.</summary>
    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    public async Task MissingRoleOrPrincipal_FailsWithoutProcedure(bool mismatch, int expectedStatus)
    {
        var entries = mismatch
            ? new[] { Script.Rows() }
            : new[] { Script.Rows(EmployeeRow()), Script.Rows(Script.Row(("Value", 0))) };
        var connection = CreateConnection(entries);
        await using var host = await StartAsync(connection);
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Address + "/Equipment/PhoneConfigHandler.ashx?mac=001122334455");
        request.Headers.Add("X-Test-User", "DOMAIN\\devadmin");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.DoesNotContain(connection.CommandLog, sql => sql.Contains("sp_КонфигурацияIpТелефона", StringComparison.Ordinal));
    }

    /// <summary>Неаутентифицированный HTTP-запрос отклоняется до любого SQL.</summary>
    [Fact]
    public async Task MissingIdentity_FailsBeforeSql()
    {
        var connection = CreateConnection();
        await using var host = await StartAsync(connection);

        using var response = await host.Client.GetAsync(host.Address + "/Equipment/PhoneConfigHandler.ashx?mac=001122334455");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(connection.CommandLog);
    }

    private static ScriptedConnection CreateConnection(params ScriptEntry[] entries) =>
        new(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);

    private static Dictionary<string, object?> EmployeeRow() => Script.Row(
        ("КодСотрудника", 4), ("Сотрудник", "Тестов Администратор"),
        ("Employee", "Test"), ("КодЛицаЗаказчика", 1));

    private static async Task<TestHost> StartAsync(ScriptedConnection connection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(connection);
        builder.Services.AddScoped(_ => new DbManager("Server=test", new FakeSqlErrorHandler(), () => connection));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        EquipmentPhoneConfigEndpoint.Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new HttpClient(), address);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, string address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public string Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var name) || string.IsNullOrWhiteSpace(name))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }
}
