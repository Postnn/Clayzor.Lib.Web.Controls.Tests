using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Services;

namespace Clayzor.Lib.Web.Controls.Tests;

/// <summary>Проверки атомарного diff и плана синхронизации телефона.</summary>
public sealed class PhoneSaveBuilderTests
{
    /// <summary>INSERT и связи используют один @@IDENTITY и одну транзакцию.</summary>
    [Fact]
    public void Build_InsertUsesIdentityForAllRelations()
    {
        var request = Request();
        request.Phone.Id = 0;
        request.Relations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 9 });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "+001" });
        var command = PhoneSaveBuilder.Build(request);
        Assert.Contains("SET @PhoneId = CONVERT(int, @@IDENTITY)", command.Sql);
        Assert.Contains("BEGIN TRANSACTION", command.Sql);
        Assert.Contains("COMMIT TRANSACTION", command.Sql);
        Assert.DoesNotContain("SCOPE_IDENTITY", command.Sql);
        Assert.Equal(8, command.Parameters.Get<int>("@newGroup0MemberPhoneId"));
        Assert.Equal(7, command.Parameters.Get<int>("@newEquipment0EquipmentId"));
        Assert.Equal(9, command.Parameters.Get<int>("@newEquipment0ConnectionEquipmentId"));
        Assert.Equal("+001", command.Parameters.Get<string>("@newParallel0CallNumber"));
        Assert.DoesNotContain("@@new", command.Sql);
    }

    /// <summary>Неизменённые поля и связи не порождают SQL.</summary>
    [Fact]
    public void Build_UnchangedSnapshotHasNoWrites()
    {
        var request = Request();
        request.Phone.Notes = "same";
        request.OriginalPhone.Notes = "same";
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "same" });
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "same" });
        request.Relations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        request.OriginalRelations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102" });
        request.OriginalRelations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102" });
        var command = PhoneSaveBuilder.Build(request);
        Assert.DoesNotContain("UPDATE vwТелефонныеНомераИзменение", command.Sql);
        Assert.DoesNotContain("INSERT INTO ТелефонныеНомераГруппы", command.Sql);
        Assert.DoesNotContain("INSERT INTO ТелефонныеНомераОборудование", command.Sql);
        Assert.DoesNotContain("UPDATE vwТелефонныеНомераПараллельныеВызовы", command.Sql);
        Assert.DoesNotContain("DELETE FROM", command.Sql);
    }

    /// <summary>Diff удаляет, обновляет метаданные и добавляет только изменённые строки.</summary>
    [Fact]
    public void Build_ComposesPreciseDiff()
    {
        var request = Request();
        request.Phone.Notes = "new";
        request.OriginalRelations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "old" });
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 9 });
        request.OriginalRelations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102", DelayBeforeCall = 1 });
        request.OriginalRelations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "104" });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "new", ReturnDate = new DateTime(2027, 1, 1) });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 10, ConnectionEquipmentId = 11 });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102", DelayBeforeCall = 2 });
        request.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "103", NotUsed = true });
        var command = PhoneSaveBuilder.Build(request);
        Assert.Contains("UPDATE vwТелефонныеНомераИзменение", command.Sql);
        Assert.Contains("DELETE FROM ТелефонныеНомераГруппы", command.Sql);
        Assert.Contains("UPDATE ТелефонныеНомераОборудование", command.Sql);
        Assert.Contains("INSERT INTO ТелефонныеНомераОборудование", command.Sql);
        Assert.Contains("UPDATE vwТелефонныеНомераПараллельныеВызовы", command.Sql);
        Assert.Contains("DELETE FROM vwТелефонныеНомераПараллельныеВызовы", command.Sql);
        Assert.Equal("new", command.Parameters.Get<string>("@updEquipment2Notes"));
        Assert.Equal(10, command.Parameters.Get<int>("@newEquipment1EquipmentId"));
        Assert.Equal(2, command.Parameters.Get<int>("@updParallel2DelayBeforeCall"));
    }

    /// <summary>Метаданные связи не вызывают синхронизацию оборудования.</summary>
    [Fact]
    public void Sync_MetadataOnlyChangesAreIgnored()
    {
        var request = Request();
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "old" });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, Notes = "new" });
        var operations = PhoneSyncPlanner.Build(request, new PhoneSyncStation(0, ""), new PhoneSyncStation(0, ""));
        Assert.Empty(operations);
    }

    /// <summary>Смена станции синхронизирует старый и новый Xorcom, а CCM получает diff оборудования.</summary>
    [Fact]
    public void Sync_IncludesOldAndNewStationsAndEquipmentDiff()
    {
        var request = Request();
        request.Phone.StationId = 2;
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 8 });
        var operations = PhoneSyncPlanner.Build(request, new PhoneSyncStation(1, "old"), new PhoneSyncStation(2, "new"));
        Assert.Equal(2, operations.Count(item => item.Kind == PhoneSyncKind.Xorcom));
        Assert.Contains(operations, item => item.Deleting);
        var ccmRequest = Request();
        ccmRequest.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 8 });
        var ccm = PhoneSyncPlanner.Build(ccmRequest, new PhoneSyncStation(0, ""), new PhoneSyncStation(0, ""));
        Assert.Equal([7, 8], ccm.Where(item => item.Kind == PhoneSyncKind.Ccm).Select(item => int.Parse(item.Key)).Order().ToArray());
    }

    /// <summary>Добавление, удаление и замена оборудования запускают соответствующую синхронизацию станции.</summary>
    [Theory]
    [InlineData(PhoneSyncKind.Ccm)]
    [InlineData(PhoneSyncKind.Meridian)]
    [InlineData(PhoneSyncKind.Xorcom)]
    public void Sync_StructuralEquipmentDiffIncludesStation(PhoneSyncKind kind)
    {
        for (var change = 0; change < 3; change++)
        {
            var request = Request();
            request.OriginalPhone.AtsPort = request.Phone.AtsPort = "99/70";
            if (change is 1 or 2) request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7 });
            if (change is 0 or 2) request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = change == 2 ? 8 : 7 });
            var station = kind switch
            {
                PhoneSyncKind.Meridian => new PhoneSyncStation(32, ""),
                PhoneSyncKind.Xorcom => new PhoneSyncStation(33, "pbx"),
                _ => new PhoneSyncStation(0, ""),
            };
            var operations = PhoneSyncPlanner.Build(request, station, station);

            Assert.Contains(operations, item => item.Kind == kind);
            if (kind == PhoneSyncKind.Ccm && change == 2)
                Assert.Equal([7, 8], operations.Where(item => item.Kind == kind).Select(item => int.Parse(item.Key)).Order().ToArray());
        }
    }

    /// <summary>Смена подключения заменяет связь по точному старому и новому составному ключу.</summary>
    [Fact]
    public void Build_ConnectionChangeUsesPreciseDeleteAndInsert()
    {
        var request = Request();
        request.OriginalRelations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 8, Notes = "old" });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 9, Notes = "new" });

        var command = PhoneSaveBuilder.Build(request);

        Assert.Equal(8, command.Parameters.Get<int>("@delEquipment0ConnectionEquipmentId"));
        Assert.Equal(9, command.Parameters.Get<int>("@newEquipment1ConnectionEquipmentId"));
        Assert.Contains("NULLIF(КодОборудованияПодключения, 0) = @delEquipment0ConnectionEquipmentId", command.Sql);
        Assert.DoesNotContain("UPDATE ТелефонныеНомераОборудование", command.Sql);
    }

    /// <summary>Физический PK запрещает две строки одного оборудования даже с разными подключениями.</summary>
    [Fact]
    public void Build_DuplicateEquipmentIsRejectedRegardlessOfConnection()
    {
        var request = Request();
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 8 });
        request.Relations.Equipment.Add(new PhoneEquipmentLink { EquipmentId = 7, ConnectionEquipmentId = 9 });
        Assert.Throws<InvalidOperationException>(() => PhoneSaveBuilder.Build(request));
    }

    /// <summary>NULL и 0 обозначают одну связь без оборудования подключения.</summary>
    [Fact]
    public void EquipmentKey_NormalizesEmptyConnection()
    {
        Assert.Equal(new PhoneEquipmentKey(7, null), new PhoneEquipmentKey(7, 0));
    }

    /// <summary>Для существующего номера группа сохраняется до основной записи, для нового — после.</summary>
    [Fact]
    public void Build_UsesLegacyGroupAndMainWriteOrder()
    {
        var existing = Request();
        existing.Phone.Notes = "changed";
        existing.Relations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        var existingSql = PhoneSaveBuilder.Build(existing).Sql;
        Assert.True(existingSql.IndexOf("INSERT INTO ТелефонныеНомераГруппы", StringComparison.Ordinal) <
                    existingSql.IndexOf("UPDATE vwТелефонныеНомераИзменение", StringComparison.Ordinal));

        var inserted = Request();
        inserted.Phone.Id = 0;
        inserted.Relations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        var insertedSql = PhoneSaveBuilder.Build(inserted).Sql;
        Assert.True(insertedSql.IndexOf("INSERT INTO vwТелефонныеНомераИзменение", StringComparison.Ordinal) <
                    insertedSql.IndexOf("INSERT INTO ТелефонныеНомераГруппы", StringComparison.Ordinal));
    }

    /// <summary>Изменения PBX и параллельных вызовов запускают синхронизацию станции.</summary>
    [Fact]
    public void Sync_IncludesGroupAndParallelCallChanges()
    {
        var groupRequest = Request();
        groupRequest.Relations.GroupMembers.Add(new PhoneGroupMember { PhoneId = 8 });
        Assert.Contains(PhoneSyncPlanner.Build(groupRequest, new PhoneSyncStation(1, "pbx"), new PhoneSyncStation(1, "pbx")),
            item => item.Kind == PhoneSyncKind.Xorcom);

        var parallelRequest = Request();
        parallelRequest.Relations.ParallelCalls.Add(new PhoneParallelCall { CallNumber = "102", NotUsed = true });
        Assert.Contains(PhoneSyncPlanner.Build(parallelRequest, new PhoneSyncStation(1, "pbx"), new PhoneSyncStation(1, "pbx")),
            item => item.Kind == PhoneSyncKind.Xorcom);
    }

    private static PhoneSaveRequest Request() => new()
    {
        Phone = new Phone { Id = 5, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1, Notes = "old" },
        OriginalPhone = new Phone { Id = 5, Number = "101", FullNumber = "101", PhoneTypeId = 1, StationId = 1, Notes = "old" },
    };
}
