using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// Object storage management actions. Object operations go through <see cref="ObjectStore"/>; maintenance
/// runs as global business tasks through <see cref="ObjectStorageMaintenance"/>.
/// </summary>
[Module(Defaults.AdministrativeToolsModuleName)]
public sealed partial class ObjectStorageServices(ObjectStorageDbContext db, ObjectStore store, ObjectStorageRuntime runtime,
   ObjectStorageMaintenance maintenance) : ServicesBase, IObjectStorageServices
{
   private const string TaskKeyPrefix = "objectstorage:";

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public async Task<StoreStatus> GetMeta_StoreStatus() {
      var volumes = await GetMeta_StoreVolumes();
      var physical = await (from c in db.Copies
                            join b in db.Blobs on c.cStoreObjectBlobHash equals b.cStoreObjectBlobHash
                            where c.cStoreObjectBlobVolState == (int)StoreBlobCopyState.Ready
                            select (long?)b.cStoreObjectBlobSize).SumAsync(AbortToken) ?? 0;
      var orphans = db.Blobs.Where(b => b.cStoreObjectBlobState == (int)StoreBlobState.Orphan);
      return new StoreStatus(volumes.Length,
         volumes.Count(v => v.State == StoreVolumeState.Offline || !v.Probe.IsValid || v.Missing > 0),
         await db.Policies.CountAsync(AbortToken), await db.Buckets.CountAsync(AbortToken),
         await db.Buckets.SumAsync(b => (long?)b.cStoreBucketObjects, AbortToken) ?? 0,
         await db.Buckets.SumAsync(b => (long?)b.cStoreBucketBytes, AbortToken) ?? 0,
         await db.Blobs.LongCountAsync(AbortToken), physical,
         await db.Copies.LongCountAsync(c => c.cStoreObjectBlobVolState == (int)StoreBlobCopyState.Missing, AbortToken),
         await maintenance.CountUnderReplicatedAsync(AbortToken),
         await orphans.LongCountAsync(AbortToken), await orphans.SumAsync(b => (long?)b.cStoreObjectBlobSize, AbortToken) ?? 0,
         FindBusinessTasks(TaskKeyPrefix), runtime.LastMaintenanceUtc, runtime.LastMaintenanceMessage);
   }

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.ManagerClaim)]
   public Task<BusinessTaskInfo[]> GetMeta_StoreTasks() => Task.FromResult(FindBusinessTasks(TaskKeyPrefix));

   /// <inheritdoc />
   [GetAction(claim: IObjectStorageServices.SettingsClaim)]
   public Task<StoreGcReview> GetMeta_StoreGcReview(int graceHours) => maintenance.ReviewGarbageAsync(graceHours, AbortToken);

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.SettingsClaim)]
   public Task<BusinessTaskInfo> PostGetMeta_StoreGcRun(int graceHours) {
      if (graceHours is < 1 or > 720) throw new ActionException("Grace period must be between 1 and 720 hours.", 400);
      return Task.FromResult(StartMaintenance("gc", "Object storage garbage collection",
         (m, ctx) => m.CollectGarbageAsync(graceHours, ctx.Report, ctx.CancellationToken)));
   }

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.SettingsClaim)]
   public Task<BusinessTaskInfo> PostGetMeta_StoreRetentionRun() =>
      Task.FromResult(StartMaintenance("retention", "Object storage version retention",
         (m, ctx) => m.RetentionAsync(ctx.Report, ctx.CancellationToken)));

   /// <inheritdoc />
   [PostAction(claim: IObjectStorageServices.SettingsClaim)]
   public Task<BusinessTaskInfo> PostGetMeta_StoreRepairRun() =>
      Task.FromResult(StartMaintenance("repair", "Object storage repair",
         (m, ctx) => m.ReplicateAsync(true, ctx.Report, ctx.CancellationToken)));

   private BusinessTaskInfo StartMaintenance(string job, string title, Func<ObjectStorageMaintenance, BusinessTaskContext, Task<string>> work) =>
      StartBusinessTask(new BusinessTaskOptions { Key = TaskKeyPrefix + job, Scope = BusinessTaskScope.Global, Title = title }, async ctx => {
         var runner = ctx.Services.GetRequiredService<ObjectStorageMaintenance>();
         var summary = await work(runner, ctx);
         ctx.Services.GetRequiredService<ObjectStorageRuntime>().ReportMaintenance(summary);
         ctx.Report(100, summary);
      });
}
