using Clayzor.Lib.Entities.Inventory;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Clayzor.Lib.Web.Controls.Tests;

public sealed partial class EquipmentFormTests
{
    private sealed partial class FakeEquipmentFormService
    {
        public bool DeferredComputerSave { get; set; }

        public override Task UpdateAsync(EquipmentEntity equipment, int employeeId, CancellationToken ct) =>
            UpdateAsync(equipment);

        public override async Task<EquipmentPhoneSyncIntent?> SaveWithPhonesAsync(EquipmentEntity equipment,
            IReadOnlyCollection<EquipmentPhoneLink> original, IReadOnlyCollection<EquipmentPhoneLink> current,
            int employeeId, CancellationToken ct) =>
            await SaveWithPhonesAsync(equipment, original, current);

        public override Task AddLocationAsync(EquipmentLocationPeriod location, EquipmentResponsiblePeriod? responsible,
            bool expectedActiveResponsible, int employeeId, CancellationToken ct) =>
            AddLocationAsync(location, responsible, expectedActiveResponsible);

        public override Task UpdateLocationAsync(EquipmentLocationPeriod location, int employeeId, CancellationToken ct)
        {
            if (ResponsibleException is not null) throw ResponsibleException;
            LocationSaves.Add((location, null));
            SavedLocationVersion = location.ChangedAtUtc;
            return Task.CompletedTask;
        }

        public override Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, CancellationToken ct) =>
            UpdateResponsibleAsync(responsible, employeeId, ct);

        public override Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, CancellationToken ct)
        {
            ResponsibleUpdated = responsible;
            return ResponsibleException is null ? Task.CompletedTask : Task.FromException(ResponsibleException);
        }

        public override Task AddAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
            AddAccountAsync(account);

        public override Task UpdateAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
            UpdateAccountAsync(account);

        public override Task DeleteAccountAsync(EquipmentAccount account, int employeeId, CancellationToken ct) =>
            DeleteAccountAsync(account);

        public override Task SaveAccountingAsync(EquipmentAccounting accounting, int employeeId, CancellationToken ct) =>
            SaveAccountingAsync(accounting);

        public override Task DeleteAccountingAsync(int equipmentId, int accountingId, int employeeId, CancellationToken ct) =>
            DeleteAccountingAsync(equipmentId, accountingId);

        public override async Task SaveComputerAsync(EquipmentComputerSnapshot snapshot, DateTime? businessToday,
            int? expectedModelId, int employeeId, CancellationToken ct)
        {
            ComputerSaveCalls++;
            ComputerWriteBusinessDay = businessToday;
            ComputerSaveRequested.TrySetResult();
            if (DeferredComputerSave) await ComputerSaveCompletion.Task.WaitAsync(ct);
            if (snapshot.ChangedAt == default && ComputerReset?.Snapshot?.ChangedAt is { } changedAt)
                snapshot.ChangedAt = changedAt;
            StoredComputers.RemoveAll(item => item.EquipmentId == snapshot.EquipmentId && item.Date == snapshot.Date);
            StoredComputers.Add(snapshot);
        }

        public override Task SaveLicenseAsync(EquipmentLicense license, LicenseType? type, int employeeId,
            CancellationToken ct)
        {
            LicenseWriteCalls++;
            return SaveException is null ? Task.CompletedTask : Task.FromException(SaveException);
        }

        public override Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation, int employeeId,
            CancellationToken ct)
        {
            SoftwareWriteCalls++;
            SubmittedSoftware = installation;
            StoredSoftware.RemoveAll(item => item.Id == installation.Id);
            StoredSoftware.Add(installation);
            return Task.CompletedTask;
        }

        public override Task SaveSimAsync(EquipmentSimCard sim, EquipmentSimWriteMode mode, string? expectedVersion,
            int? duplicateEquipmentId, int employeeId, CancellationToken ct)
        {
            SimWriteCalls++;
            return Task.CompletedTask;
        }

        public override Task DeleteSimAsync(int equipmentId, string? expectedVersion, int employeeId, CancellationToken ct) =>
            DeleteSimAsync(equipmentId, expectedVersion);

        public override Task SaveSimServiceAsync(EquipmentSimService service, DateTime? businessToday, int employeeId,
            CancellationToken ct)
        {
            SimWriteCalls++;
            SimServiceBusinessDay = businessToday;
            SavedSimServiceVersion = service.ChangedAt == default ? SimServiceReset?.ChangedAt : service.ChangedAt;
            StoredSimServices.RemoveAll(item => item.Id == service.Id);
            StoredSimServices.Add(service);
            return Task.CompletedTask;
        }

        public override Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, CancellationToken ct)
        {
            UpsWriteCalls++;
            return UpsSaveException is null ? Task.CompletedTask : Task.FromException(UpsSaveException);
        }

        public override Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, CancellationToken ct)
        {
            UpsWriteCalls++;
            return UpsSaveException is null ? Task.CompletedTask : Task.FromException(UpsSaveException);
        }

        public override Task SaveUpsDetailsAsync(int upsEquipmentId, int sourceServerEquipmentId,
            IReadOnlyCollection<UpsServerLink> originalServers, IReadOnlyCollection<UpsServerLink> currentServers,
            IReadOnlyCollection<UpsTest> originalTests, IReadOnlyCollection<UpsTest> currentTests,
            int employeeId, CancellationToken ct)
        {
            UpsWriteCalls++;
            return SaveUpsDetailsAsync(upsEquipmentId, sourceServerEquipmentId,
                originalServers, currentServers, originalTests, currentTests);
        }

        public override Task SetNetworkAttachmentAsync(int equipmentId, int employeeId, bool attach,
            int expectedCount, CancellationToken ct)
        {
            AttachNetworkCalls++;
            NetworkAttemptId = Guid.NewGuid();
            return Task.CompletedTask;
        }
    }
}
