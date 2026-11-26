using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.Settings;
using Kesco.App.Web.Inventory.Components.Pages.Telefon;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Восстановление собственного черновика отсутствующего номера ТфОП.</summary>
public sealed class NumPoolMissingDraftTests
{
    private const string Number = "00023";
    private const string Note = "Локальное примечание";
    private const string Register = "clayDraftStore.register";

    /// <summary>Право чтения позволяет посмотреть данные восстановления без редактирования или записи.</summary>
    [Fact]
    public async Task ReadOnlyActorCanViewOwnDraftForMissingNumber()
    {
        using var connection = new MissingNumberConnection(employeeExists: true, canRead: true);
        await using var context = CreateContext(connection);
        var card = context.Render<NumPool>();

        card.WaitForAssertion(() => Assert.Contains("Черновик доступен для просмотра. Сейчас запись запрещена.", card.Markup));
        Assert.Equal(Note, card.Find("textarea").TextContent);
        Assert.True(card.Find("textarea").HasAttribute("readonly"));
        var buttons = card.FindAll("button");
        Assert.True(Assert.Single(buttons, button => button.TextContent.Trim() == "ОК").HasAttribute("disabled"));
        Assert.True(Assert.Single(buttons, button => button.TextContent.Trim() == "Сохранить").HasAttribute("disabled"));
        Assert.DoesNotContain(buttons, button => button.TextContent.Trim() == "Удалить");
        var registration = Assert.Single(context.JSInterop.Invocations[Register]);
        Assert.Equal("num-pool", registration.Arguments[0]);
        Assert.Equal("id:" + Number, registration.Arguments[1]);
        Assert.Equal("4", registration.Arguments[2]);
        var save = card.FindComponents<ClayToolbarButton>().Single(button => button.Instance.Options?.Text == "Сохранить");
        await card.InvokeAsync(() => save.Instance.OnClick.InvokeAsync());
        Assert.Equal(1, connection.NumberReadCount);
        Assert.Equal(0, connection.WriteAttempts);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.beginOperationIntent"]);
        Assert.Empty(context.JSInterop.Invocations["clayDraftStore.saveTypedScope"]);
    }

    /// <summary>Без подтверждённого сотрудника или права чтения хранилище черновика не открывается.</summary>
    [Theory]
    [InlineData(false, "Не удалось подтвердить текущего сотрудника.")]
    [InlineData(true, "Нет доступа к справочнику номеров ТфОП.")]
    public async Task MissingIdentityOrReadPermissionNeverOpensDraft(bool employeeExists, string message)
    {
        using var connection = new MissingNumberConnection(employeeExists, canRead: false);
        await using var context = CreateContext(connection);
        var card = context.Render<NumPool>();

        card.WaitForAssertion(() => Assert.Contains(message, card.Markup));
        Assert.Empty(card.FindAll("textarea"));
        Assert.Empty(context.JSInterop.Invocations[Register]);
        Assert.Equal(0, connection.NumberReadCount);
        Assert.Equal(0, connection.WriteAttempts);
    }

    private static BunitContext CreateContext(MissingNumberConnection connection)
    {
        var original = new NumPool.FormState(NumPoolSnapshot.Empty with
        {
            Header = new NumPoolHeader { Number = Number, Control = 0 },
        }, false);
        var current = original with { Data = original.Data with { Header = original.Data.Header with { Notes = Note } } };
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>(Register, call =>
                Equals(call.Arguments[0], "num-pool") && Equals(call.Arguments[1], "id:" + Number) && Equals(call.Arguments[2], "4"))
            .SetResult(new ClayDraftRestore("own-numpool-draft", true, 1, null, null,
                new Dictionary<string, string> { ["main"] = JsonSerializer.Serialize(new { Original = original, Current = current }) }));
        context.JSInterop.Setup<ClayDraftRestoreReadiness>("clayDraftStore.getRestoreReadiness", call => Equals(call.Arguments[0], "own-numpool-draft"))
            .SetResult(new ClayDraftRestoreReadiness(true, [], []));
        context.JSInterop.Setup<ClayDraftRawApplyResult>("clayDraftStore.applyRaw", call => Equals(call.Arguments[0], "own-numpool-draft") && Equals(call.Arguments[1], "main"))
            .SetResult(new ClayDraftRawApplyResult([], []));
        context.Services.AddMudServices();
        context.Services.AddSingleton(new ClayAppSettings());
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<ClayErrorService>();
        context.Services.AddSingleton<ClayFormWindowService>();
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddSingleton(new DbManager("Server=test", null, () => connection));
        context.Services.AddSingleton<AuthenticationStateProvider, NumPoolAuthentication>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/Telefon/NumPool.aspx?id=" + Number);
        return context;
    }

    private sealed class NumPoolAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, @"TEST\reader")], "Negotiate"))));
    }

    private sealed class MissingNumberConnection(bool employeeExists, bool canRead) : DbConnection
    {
        internal int NumberReadCount { get; private set; }
        internal int WriteAttempts { get; private set; }
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new ReadCommand(this);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw UnexpectedWrite();

        private Exception UnexpectedWrite()
        {
            WriteAttempts++;
            return new InvalidOperationException("NumPool attempted a write while restoring read-only input.");
        }

        private DbDataReader Read(string sql, DbParameterCollection parameters)
        {
            var table = new DataTable();
            if (sql.Contains("@WindowsPrincipal", StringComparison.Ordinal))
            {
                table.Columns.Add("Id", typeof(int));
                if (employeeExists) table.Rows.Add(4);
            }
            else if (sql.Contains("HAS_PERMS_BY_NAME", StringComparison.Ordinal))
            {
                foreach (var name in new[] { "CanRead", "CanInsert", "CanUpdate", "CanDelete" })
                    table.Columns.Add(name, typeof(bool));
                table.Rows.Add(canRead, false, false, false);
            }
            else if (sql.Contains("vwНомераТфОП n", StringComparison.Ordinal))
            {
                Assert.Equal(Number, parameters["Id"].Value);
                NumberReadCount++;
                table.Columns.Add("Number", typeof(string));
                var links = new DataTable();
                links.Columns.Add("Id", typeof(int));
                links.Columns.Add("Name", typeof(string));
                links.Columns.Add("Private", typeof(byte));
                links.Columns.Add("ChangedBy", typeof(int));
                links.Columns.Add("ChangedByName", typeof(string));
                links.Columns.Add("ChangedAt", typeof(DateTime));
                return new DataTableReader([table, links, links.Clone(), links.Clone(), links.Clone()]);
            }
            else if (sql.Contains("AS RoleId", StringComparison.OrdinalIgnoreCase))
            {
                table.Columns.Add("RoleId", typeof(int));
                table.Columns.Add("PersonId", typeof(int));
            }
            else
                throw new InvalidOperationException("Unexpected NumPool read: " + sql);
            return table.CreateDataReader();
        }

        private sealed class ReadCommand(MissingNumberConnection owner) : DbCommand
        {
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => owner.Read(CommandText, DbParameterCollection);
            public override int ExecuteNonQuery() => throw owner.UnexpectedWrite();
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            public override void Cancel() { }
        }
    }
}
