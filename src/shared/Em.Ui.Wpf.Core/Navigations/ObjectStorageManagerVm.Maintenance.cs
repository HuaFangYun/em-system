using System.Collections.ObjectModel;
using Em.Api.Core.Models;
using Em.Ui.Wpf.Shared;

namespace Em.Ui.Wpf.Navigations;

/// <summary>One caption/value row of the maintenance status card.</summary>
/// <param name="Caption">Caption.</param>
/// <param name="Value">Value.</param>
public sealed record StoreStatusLine(string Caption, string Value);

public sealed partial class ObjectStorageManagerVm
{
   /// <summary>Counters and health of object storage.</summary>
   public StoreStatus? StoreStatus { get => Get<StoreStatus>(); private set => Set(value, _ => NotifyChanged(nameof(StatusLines))); }

   /// <summary>Status counters as caption/value rows for the maintenance card.</summary>
   public StoreStatusLine[] StatusLines => StoreStatus is not { } s ? [] : [
      new("Buckets", $"{s.Buckets:N0}"),
      new("Objects", $"{s.Objects:N0}"),
      new("Logical size", FormatSize(s.LogicalBytes)),
      new("Physical size", $"{FormatSize(s.PhysicalBytes)} in {s.Blobs:N0} blobs"),
      new("Volumes", s.VolumesWithProblems > 0 ? $"{s.Volumes:N0} ({s.VolumesWithProblems:N0} with problems)" : $"{s.Volumes:N0}"),
      new("Missing copies", $"{s.MissingCopies:N0}"),
      new("Below replica target", $"{s.UnderReplicatedBlobs:N0} blobs"),
      new("Orphan blobs", $"{s.OrphanBlobs:N0} ({FormatSize(s.OrphanBytes)})"),
      new("Last background pass", s.LastMaintenanceUtc is { } at ? $"{DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime():g} · {s.LastMaintenanceMessage}" : "Not yet")
   ];

   /// <summary>Maintenance tasks that are alive or failed.</summary>
   public ObservableCollection<BusinessTaskInfo> Tasks { get; } = [];

   /// <summary>Grace period for garbage collection, in hours (1–720).</summary>
   public int GcGraceHours { get => Get<int?>() ?? 24; set => Set((int?)value, _ => { GcReview = null; UpdateState(); }); }

   /// <summary>Result of the last garbage collection review; a run is offered only after a review with the same grace period.</summary>
   public StoreGcReview? GcReview { get => Get<StoreGcReview>(); private set => Set(value, _ => NotifyChanged(nameof(GcReviewText))); }

   /// <summary>Summary of <see cref="GcReview"/>.</summary>
   public string GcReviewText => GcReview is not { } r ? "Review first to see what a run would delete." :
      $"{r.Eligible:N0} orphan blobs ({FormatSize(r.EligibleBytes)}) are older than {r.GraceHours} h and would be deleted. " +
      $"{r.Orphans:N0} orphans in total, {r.Unreferenced:N0} unreferenced blobs would be marked orphan.";

   private void RegisterMaintenanceCommands() {
      RegisterCommand(nameof(ReviewGcCommand), ReviewGcCommand, () => CanBrowse && GcGraceHours is >= 1 and <= 720);
      RegisterCommand(nameof(RunGcCommand), RunGcCommand, () => CanBrowse && GcReview is { } r && r.GraceHours == GcGraceHours);
      RegisterCommand(nameof(RunRetentionCommand), RunRetentionCommand, () => CanBrowse);
      RegisterCommand(nameof(RunRepairCommand), RunRepairCommand, () => CanBrowse);
      RegisterCommand(nameof(RefreshStatusCommand), RefreshStatusCommand, () => IsNotBusy);
   }

   private async Task LoadStatusAsync() {
      var status = await Service.GetMeta_StoreStatus();
      StoreStatus = status;
      Tasks.Clear();
      foreach (var task in status.Tasks) Tasks.Add(task);
   }

   /// <summary>Reloads the status card and the task list.</summary>
   public Task RefreshStatusCommand() => RunAsync("Loading status...", LoadStatusAsync);

   /// <summary>Dry run of garbage collection.</summary>
   public Task ReviewGcCommand() => RunAsync("Reviewing garbage collection...", async () => {
      GcReview = await Service.GetMeta_StoreGcReview(GcGraceHours);
      Status = "Garbage collection reviewed.";
   });

   /// <summary>Starts garbage collection after confirmation.</summary>
   public async Task RunGcCommand() {
      if (GcReview is not { } review) return;
      if (Confirm?.Invoke($"Delete {review.Eligible:N0} orphan blobs ({FormatSize(review.EligibleBytes)}) older than {review.GraceHours} hours from every volume?",
             "Garbage collection") != true) return;
      await StartTaskAsync("Starting garbage collection...", () => Service.PostGetMeta_StoreGcRun(review.GraceHours));
      GcReview = null;
   }

   /// <summary>Starts version retention.</summary>
   public Task RunRetentionCommand() => StartTaskAsync("Starting retention...", Service.PostGetMeta_StoreRetentionRun);

   /// <summary>Starts repair (verify every copy and replicate).</summary>
   public Task RunRepairCommand() => StartTaskAsync("Starting repair...", Service.PostGetMeta_StoreRepairRun);

   private Task StartTaskAsync(string caption, Func<Task<BusinessTaskInfo>> start) => RunAsync(caption, async () => {
      var task = await start();
      await LoadStatusAsync();
      Status = $"'{task.Title}' started. Follow it in the task list or the Business Task Manager.";
   });
}
