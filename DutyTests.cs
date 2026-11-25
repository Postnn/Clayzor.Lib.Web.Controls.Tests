using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Bunit;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Duty;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.MedicalTests;
using Xunit;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Календарная модель и транзакция расписания с тестовым SQL-транспортом.</summary>
public sealed class DutyTests
{
    private static readonly DateOnly DutyDate = new(2026, 11, 21);
    private static readonly DateOnly OldDay = new(2026, 11, 28);
    private static readonly DateOnly NewDay = new(2026, 11, 29);
    private static readonly DateTime Today = new(2026, 11, 25);
    private static readonly DutyRow Original = new(DutyDate.ToDateTime(TimeOnly.MinValue), 8, 8, null, 0, null,
        OldDay.ToDateTime(TimeOnly.MinValue), 0, 4, new DateTime(2026, 11, 22), null, null, null);

    /// <summary>Разбирает однозначные ссылки грида без UTC-преобразования.</summary>
    [Theory]
    [InlineData("20261121", true)]
    [InlineData("2026-11-21", true)]
    [InlineData("21.11.2026 0:00:00", true)]
    [InlineData("11/21/2026 12:00:00 AM", true)]
    [InlineData("11/21/2026 00:00:00", true)]
    [InlineData("2026-11-21T00:00:00", true)]
    [InlineData("20260229", false)]
    [InlineData("04/05/2026", false)]
    [InlineData("2026-11-21T12:00:00", false)]
    public void RouteKeyUsesExplicitFormats(string value, bool accepted)
    {
        Assert.Equal(accepted, DutyKey.TryParse(value, out var date));
        if (accepted) Assert.Equal(DutyDate, date);
    }

    /// <summary>Старый незакрытый снимок без факта не назначает вариант «никто».</summary>
    [Fact]
    public void MissingActualRequiresExplicitConfirmation()
    {
        var draft = DutyDraft.FromRow(Original with { ActualId = null, TimeOff = null });
        Assert.Null(draft.Confirmation);
        Assert.Contains("выбрать вариант", draft.Validate(true, null));
        Assert.Contains("без подтверждения", (draft with { Confirmation = DutyConfirmation.Nobody }).Validate(true, null));
        Assert.Null((draft with { Confirmation = DutyConfirmation.Nobody, Close = true }).Validate(true, null));
    }

    /// <summary>Скрытый отгул не попадает в запись при рабочем дне или неподтверждённом флаге.</summary>
    [Fact]
    public void TimeOffFollowsEffectiveBranch()
    {
        var draft = new DutyDraft(DutyDate, 8, DutyConfirmation.Other, 9, true, DutyDate, true);
        Assert.Equal(9, draft.EffectiveEngineer(true));
        Assert.Contains("больше", draft.Validate(true, false));
        var changed = draft with { Confirmation = DutyConfirmation.Planned };
        Assert.Equal(8, changed.EffectiveEngineer(true));
        Assert.Null(changed.EffectiveTimeOff(true));
        Assert.Null(changed.Validate(true, true));
        Assert.Null((changed with { WantsTimeOff = false }).Validate(true, false));
        Assert.Contains("указать сотрудника", (draft with { ActualId = null }).Validate(true, false));
    }

    /// <summary>Перенос отгула удаляет интервал A и добавляет интервал B в одной транзакции.</summary>
    [Fact]
    public async Task EmployeeChangeTransfersOnlyMatchingOldInterval()
    {
        using var connection = new Probe();
        using var db = Db(connection);
        var result = await DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default);
        Assert.Equal(9, result.ActualId);
        Assert.Equal((byte)1, result.Closed);
        Assert.Equal(1, connection.Commits);
        Assert.Equal(8, connection.DeletedFor);
        Assert.Equal(9, connection.InsertedFor);
        Assert.Equal(new Interval(101, 9, NewDay), Assert.Single(connection.Intervals));
        Assert.Equal(NewDay.ToDateTime(TimeOnly.MinValue), connection.Row!.TimeOff);
    }

    /// <summary>При отказе одной из процедур не остаётся изменений ни в одной части сценария.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("insert")]
    public async Task ProcedureFailureRollsBackScheduleAndIntervals(string failing)
    {
        using var connection = new Probe { FailProcedure = failing };
        using var db = Db(connection);
        await Assert.ThrowsAsync<DutyRejectedException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Equal(0, connection.Commits);
        Assert.Equal(1, connection.Rollbacks);
        Assert.Equal(Original, connection.Row);
        Assert.Equal(new Interval(100, 8, OldDay), Assert.Single(connection.Intervals));
    }

    /// <summary>Неоднозначная связь интервала со старым дежурством блокирует изменение.</summary>
    [Fact]
    public async Task MultipleOriginalIntervalsArePreserved()
    {
        using var connection = new Probe();
        connection.Intervals.Add(new Interval(200, 8, OldDay));
        using var db = Db(connection);
        await Assert.ThrowsAsync<DutyRejectedException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Null(connection.DeletedFor);
        Assert.Null(connection.InsertedFor);
        Assert.Equal(2, connection.Intervals.Count);
        Assert.Equal(Original, connection.Row);
    }

    /// <summary>Закрытое, ещё не прошедшее и конкурентно изменённое дежурство отвергают прямой вызов.</summary>
    [Theory]
    [InlineData("closed")]
    [InlineData("today")]
    [InlineData("changed")]
    [InlineData("rights")]
    public async Task ServerRevalidatesStateUnderLock(string reason)
    {
        using var connection = new Probe();
        var original = Original;
        var draft = ClosingDraft();
        if (reason == "closed") connection.Row = original = Original with { Closed = 1 };
        if (reason == "today")
        {
            connection.Row = original = Original with { DutyDate = Today };
            draft = draft with { Date = DateOnly.FromDateTime(Today) };
        }
        if (reason == "changed") connection.Row = Original with { ChangedAt = Original.ChangedAt.AddSeconds(1) };
        if (reason == "rights") connection.CanWrite = false;
        using var db = Db(connection);
        await Assert.ThrowsAsync<DutyRejectedException>(() => DutyData.SaveAsync(db, "fixture", 4, original, draft, default));
        Assert.Equal(0, connection.Commits);
        Assert.Equal(0, connection.ScheduleWrites);
        Assert.Null(connection.InsertedFor);
        Assert.Null(connection.DeletedFor);
    }

    /// <summary>Незакрытый факт сохраняет дату, а рабочий день эффективного инженера исключает отгул.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftFactAndEffectiveCalendarUseCorrectEmployee(bool workDay)
    {
        using var connection = new Probe { WorkDay = workDay };
        using var db = Db(connection);
        var draft = ClosingDraft() with { Close = false, Confirmation = DutyConfirmation.Planned };
        var result = await DutyData.SaveAsync(db, "fixture", 4, Original, draft, default);
        Assert.Equal(8, connection.CalendarFor);
        Assert.Equal((byte)0, result.Closed);
        Assert.Equal(workDay ? (DateTime?)null : NewDay.ToDateTime(TimeOnly.MinValue), result.TimeOff);
        Assert.Null(connection.InsertedFor);
    }

    /// <summary>Плановая вставка и следующее редактирование используют разные SQL-команды.</summary>
    [Fact]
    public async Task PlanInsertAndUpdateAvoidFactAndAbsenceCommands()
    {
        using var connection = new Probe { Row = null };
        using var db = Db(connection);
        var draft = new DutyDraft(DateOnly.FromDateTime(Today), 8);
        var inserted = await DutyData.SaveAsync(db, "fixture", 4, null, draft, default);
        var updated = await DutyData.SaveAsync(db, "fixture", 4, inserted, draft with { PlannedId = 9 }, default);
        Assert.Equal(2, connection.Commits);
        Assert.Equal(9, updated.PlannedId);
        Assert.Equal(new[] { "insert", "plan" }, connection.Branches);
        Assert.Null(connection.DeletedFor);
        Assert.Null(connection.InsertedFor);
    }

    /// <summary>Потерянный ответ COMMIT не выдаётся за доказанный отказ и не повторяется.</summary>
    [Fact]
    public async Task LostCommitResponseKeepsBusinessOutcomeUnknown()
    {
        using var connection = new Probe { LoseCommitResponse = true };
        using var db = Db(connection);
        await Assert.ThrowsAsync<TimeoutException>(() => DutyData.SaveAsync(db, "fixture", 4, Original, ClosingDraft(), default));
        Assert.Equal(1, connection.Commits);
        Assert.Equal(0, connection.Rollbacks);
        Assert.Equal(1, connection.ScheduleWrites);
        Assert.Single(connection.Intervals);
    }

    /// <summary>Страница создаёт черновик с текущим днём и блокирует кнопку при недоступной записи.</summary>
    [Fact]
    public async Task NewReadOnlyEditorDisplaysDefaultWithoutWriting()
    {
        using var connection = new Probe { Row = null, CanWrite = false };
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<ClayDraftRestore?>("clayDraftStore.register", _ => true)
            .SetResult(new ClayDraftRestore("duty-test", false, 0, null, null));
        context.Services.AddScoped(_ => Db(connection));
        context.Services.AddScoped<ClayDraftService>();
        context.Services.AddScoped<ClayFormWindowService>();
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<AuthenticationStateProvider, DutyAuthentication>();
        context.Services.AddMudServices();
        context.Render<MudBlazor.MudPopoverProvider>();
        var page = context.Render<DutyPage>();
        page.WaitForAssertion(() => Assert.Contains("Редактирование недоступно", page.Markup));
        Assert.Contains("25.11.2026", page.Markup);
        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Сохранить").HasAttribute("disabled"));
        Assert.Equal(0, connection.ScheduleWrites);
    }

    private sealed class DutyAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "test"))));
    }

    private static DutyDraft ClosingDraft() => new(DutyDate, 8, DutyConfirmation.Other, 9, true, NewDay, true);

    private static DbManager Db(Probe connection)
    {
        DapperColumnMapper.Initialize();
        return new DbManager("Server=fixture", new FakeSqlErrorHandler(), () => connection);
    }

    private sealed record Interval(int Id, int Employee, DateOnly Date);

    private sealed class Probe : DbConnection
    {
        public DutyRow? Row { get; set; } = Original;
        public List<Interval> Intervals { get; set; } = [new(100, 8, OldDay)];
        public bool CanWrite { get; set; } = true;
        public bool WorkDay { get; init; }
        public string? FailProcedure { get; init; }
        public bool LoseCommitResponse { get; init; }
        public int Commits { get; set; }
        public int Rollbacks { get; set; }
        public int? DeletedFor { get; set; }
        public int? InsertedFor { get; set; }
        public int? CalendarFor { get; set; }
        public int ScheduleWrites { get; set; }
        public List<string> Branches { get; } = [];

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fixture";
        public override string DataSource => "fixture";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            Assert.Equal(IsolationLevel.Serializable, level);
            return new ProbeTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new ProbeCommand(this);
    }

    private sealed class ProbeTransaction(Probe connection) : DbTransaction
    {
        public DutyRow? Row = connection.Row;
        public List<Interval> Intervals = [.. connection.Intervals];
        private bool _finished;
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;
        public override void Commit()
        {
            Assert.False(_finished);
            connection.Row = Row;
            connection.Intervals = Intervals;
            connection.Commits++;
            _finished = true;
            if (connection.LoseCommitResponse) throw new TimeoutException();
        }
        public override void Rollback()
        {
            if (!_finished) connection.Rollbacks++;
            _finished = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Rollback();
            base.Dispose(disposing);
        }
    }

    private sealed class ProbeCommand(Probe owner) : DbCommand
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
        public override void Prepare() { }
        public override void Cancel() { }
        public override object ExecuteScalar() => throw new NotSupportedException();

        private T Param<T>(string key) => (T)Parameters.Cast<DbParameter>().Single(p => p.ParameterName.TrimStart('@') == key).Value!;
        private DateOnly Day(string key) => DateOnly.FromDateTime(Param<DateTime>(key));
        private ProbeTransaction Transaction => Assert.IsType<ProbeTransaction>(DbTransaction);

        public override int ExecuteNonQuery()
        {
            var tx = Transaction;
            if (CommandText.Contains("sp_НерабочиеДниDel"))
            {
                var old = tx.Intervals.Single(row => row.Id == Param<int>("intervalId"));
                owner.DeletedFor = old.Employee;
                if (owner.FailProcedure == "delete") throw new InvalidOperationException("fixture deletion error");
                tx.Intervals.Remove(old);
            }
            else if (CommandText.Contains("sp_НерабочиеДниIns"))
            {
                owner.InsertedFor = Param<int>("employeeId");
                if (owner.FailProcedure == "insert") throw new InvalidOperationException("fixture insertion error");
                tx.Intervals.Add(new Interval(101, owner.InsertedFor.Value, Day("timeOff")));
            }
            else
            {
                owner.ScheduleWrites++;
                if (CommandText.StartsWith("INSERT", StringComparison.Ordinal))
                {
                    owner.Branches.Add("insert");
                    tx.Row = Original with { DutyDate = Param<DateTime>("date"), PlannedId = Param<int>("engineerId"), ActualId = null, TimeOff = null };
                }
                else if (CommandText.Contains("SET КодСотрудника ="))
                {
                    owner.Branches.Add("plan");
                    tx.Row = tx.Row! with { PlannedId = Param<int>("engineerId") };
                }
                else
                {
                    owner.Branches.Add("fact");
                    tx.Row = tx.Row! with { ActualId = Param<int>("actualId"), Closed = Param<byte>("close"),
                        TimeOff = Param<object>("timeOff") is DateTime off ? off : null, WorkDay = (byte)(owner.WorkDay ? 1 : 0) };
                }
            }
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SUSER_SID()")) return Values(("КодСотрудника", typeof(int), 4));
            if (CommandText.Contains("WITH Schedule AS")) return Values(("Today", typeof(DateTime), Today));
            if (CommandText.Contains("CanChangeTimeOff"))
                return Values(("Today", typeof(DateTime), Today), ("CanInsert", typeof(bool), owner.CanWrite),
                    ("CanUpdate", typeof(bool), owner.CanWrite), ("CanChangeTimeOff", typeof(bool), owner.CanWrite));
            if (CommandText.Contains("parentLocation"))
            {
                owner.CalendarFor = Param<int>("employeeId");
                return Values(("Value", typeof(bool), owner.WorkDay));
            }
            if (CommandText.Contains("tally.Total"))
                return Values(("Id", typeof(int), Param<int>("id")), ("Name", typeof(string), "Инженер"),
                    ("Total", typeof(int), null), ("Weekends", typeof(int), null));
            if (CommandText.Contains("ЗаявкиIT.dbo.НерабочиеДни"))
            {
                var employee = Param<int>("employeeId");
                var day = Day("timeOff");
                var table = new DataTable();
                table.Columns.Add("Id", typeof(int));
                foreach (var interval in Transaction.Intervals.Where(row => row.Employee == employee && row.Date == day))
                    table.Rows.Add(interval.Id);
                return table.CreateDataReader();
            }
            if (CommandText.Contains("Дата <> @date")) return Values(("Value", typeof(bool), false));
            if (DbTransaction is not null) Assert.Contains("UPDLOCK", CommandText);
            var source = DbTransaction is ProbeTransaction transaction ? transaction.Row : owner.Row;
            if (source is not { } row)
            {
                var empty = new DataTable();
                empty.Columns.Add("DutyDate", typeof(DateTime));
                return empty.CreateDataReader();
            }
            return Values(("DutyDate", typeof(DateTime), row.DutyDate), ("PlannedId", typeof(int), row.PlannedId),
                ("ActualId", typeof(int), row.ActualId), ("EmployerId", typeof(int), row.EmployerId),
                ("WorkDay", typeof(byte), row.WorkDay), ("Payment", typeof(decimal), row.Payment),
                ("TimeOff", typeof(DateTime), row.TimeOff), ("Closed", typeof(byte), row.Closed),
                ("ChangedById", typeof(int), row.ChangedById), ("ChangedAt", typeof(DateTime), row.ChangedAt),
                ("PlannedName", typeof(string), row.PlannedName), ("ActualName", typeof(string), row.ActualName),
                ("ChangedByName", typeof(string), row.ChangedByName));
        }

        private static DbDataReader Values(params (string Name, Type Type, object? Value)[] values)
        {
            var table = new DataTable();
            foreach (var (name, type, _) in values) table.Columns.Add(name, type);
            table.Rows.Add(values.Select(item => item.Value ?? DBNull.Value).ToArray());
            return table.CreateDataReader();
        }
    }
}

