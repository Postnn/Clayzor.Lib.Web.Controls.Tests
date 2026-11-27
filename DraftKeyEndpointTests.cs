using System.Net;
using System.Security.Claims;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.MedicalTests;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed class DraftKeyEndpointTests
{
    private const string Principal = @"DOMAIN\employee";

    [Fact]
    public async Task KeyIsRandomAndBoundToTheResolvedEmployee()
    {
        using var connection = MakeConnection(
            Script.Rows(Script.Row(("КодСотрудника", 27))),
            Script.Rows(Script.Row(("КодСотрудника", 27))));
        var first = MakeRequest();
        first.Request.Headers[DraftKeyEndpoint.EmployeeHeader] = "999";
        var second = MakeRequest();

        var firstKey = Assert.IsType<FileContentHttpResult>(await SendAsync(first, connection));
        var secondKey = Assert.IsType<FileContentHttpResult>(await SendAsync(second, connection));

        Assert.Equal("application/octet-stream", firstKey.ContentType);
        Assert.Equal(32, firstKey.FileContents.Length);
        Assert.Equal(32, secondKey.FileContents.Length);
        Assert.False(firstKey.FileContents.Span.SequenceEqual(secondKey.FileContents.Span));
        Assert.Equal("27", first.Response.Headers[DraftKeyEndpoint.EmployeeHeader].ToString());
        Assert.Equal("27", second.Response.Headers[DraftKeyEndpoint.EmployeeHeader].ToString());
        Assert.All(connection.ParameterLog, parameters => Assert.Equal(Principal, parameters["WindowsPrincipal"]));
        Assert.Equal(2, connection.CommandLog.Count);
        AssertNoStore(first);
        AssertNoStore(second);
    }

    [Theory]
    [InlineData("http", "http://inventory.example", "same-origin", "1", true, "", 0, 403)]
    [InlineData("https", "https://inventory.example", "same-origin", "1", false, "", 0, 401)]
    [InlineData("https", "https://elsewhere.example", "same-origin", "1", true, "", 0, 403)]
    [InlineData("https", "https://inventory.example", "same-site", "1", true, "", 0, 403)]
    [InlineData("https", "", "same-origin", "1", true, "", 0, 403)]
    [InlineData("https", "https://inventory.example", "same-origin", "", true, "", 0, 403)]
    [InlineData("https", "https://inventory.example", "same-origin", "1", true, "?employeeId=27", 0, 400)]
    [InlineData("https", "https://inventory.example", "same-origin", "1", true, "", 10, 400)]
    public async Task InvalidRequestsNeverResolveAnEmployee(string scheme, string origin, string fetchSite,
        string marker, bool authenticated, string query, long length, int status)
    {
        using var connection = MakeConnection();
        var request = MakeRequest();
        request.Request.Scheme = scheme;
        request.Request.Headers.Origin = origin;
        request.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        request.Request.Headers[DraftKeyEndpoint.RequestHeader] = marker;
        request.Request.QueryString = new QueryString(query);
        request.Request.ContentLength = length;
        if (!authenticated)
            request.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Principal)]));

        var result = await SendAsync(request, connection);

        Assert.Equal(status, result is null ? request.Response.StatusCode : Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(connection.CommandLog);
        Assert.False(request.Response.Headers.ContainsKey(DraftKeyEndpoint.EmployeeHeader));
        AssertNoStore(request);
    }

    [Theory]
    [InlineData("Development", true, 200)]
    [InlineData("Development", false, 403)]
    [InlineData("Production", true, 403)]
    public async Task HttpKeyIsLimitedToDevelopmentLoopback(string environment, bool loopback, int status)
    {
        using var connection = MakeConnection(Script.Rows(Script.Row(("КодСотрудника", 27))));
        var request = MakeRequest();
        request.Request.Scheme = "http";
        request.Request.Host = new HostString("localhost:5020");
        request.Request.Headers.Origin = "http://localhost:5020";
        request.Connection.RemoteIpAddress = loopback ? IPAddress.IPv6Loopback : IPAddress.Parse("192.0.2.1");
        using var services = new ServiceCollection()
            .AddSingleton<IWebHostEnvironment>(new TestHostEnvironment(environment))
            .BuildServiceProvider();
        request.RequestServices = services;

        var result = await SendAsync(request, connection);

        if (status == 200)
        {
            var key = Assert.IsType<FileContentHttpResult>(result);
            Assert.Equal(32, key.FileContents.Length);
            Assert.Single(connection.CommandLog);
        }
        else
        {
            Assert.Equal(status, result is null
                ? request.Response.StatusCode
                : Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(connection.CommandLog);
        }
        AssertNoStore(request);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 503)]
    public async Task UnconfirmedEmployeeNeverReceivesKeyMaterial(bool lookupFailed, int status)
    {
        using var connection = MakeConnection(lookupFailed
            ? Script.Error(new InvalidOperationException("Test lookup failure."))
            : Script.Rows());
        var request = MakeRequest();
        request.Request.Headers[DraftKeyEndpoint.EmployeeHeader] = "27";

        var result = await SendAsync(request, connection);

        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Single(connection.CommandLog);
        Assert.False(request.Response.Headers.ContainsKey(DraftKeyEndpoint.EmployeeHeader));
        AssertNoStore(request);
    }

    [Fact]
    public async Task NoStoreAlsoAppliesToRejectionsBeforeTheEndpoint()
    {
        var request = MakeRequest();

        await DraftKeyEndpoint.ProtectResponseAsync(request, context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, request.Response.StatusCode);
        AssertNoStore(request);
    }

    private static DefaultHttpContext MakeRequest()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Principal)], "Negotiate")),
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("inventory.example");
        context.Request.Path = DraftKeyEndpoint.Route;
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Origin = "https://inventory.example";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Request.Headers[DraftKeyEndpoint.RequestHeader] = "1";
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    private static ScriptedConnection MakeConnection(params ScriptEntry[] entries)
    {
        DapperColumnMapper.Initialize();
        return new ScriptedConnection(new Queue<ScriptEntry>(entries), emulateEquipmentIdentity: false);
    }

    private static async Task<IResult?> SendAsync(HttpContext context, ScriptedConnection connection)
    {
        var db = new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
        IResult? result = null;
        await DraftKeyEndpoint.ProtectResponseAsync(context, async request => result = await DraftKeyEndpoint.HandleAsync(request, db));
        return result;
    }

    private static void AssertNoStore(HttpContext context) => Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());

    private sealed class TestHostEnvironment(string environment) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "Clayzor.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
