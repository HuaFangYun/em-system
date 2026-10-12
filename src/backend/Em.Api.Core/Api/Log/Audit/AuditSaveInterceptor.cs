using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Em.Api.Core.Log.Audit;

/// <summary>
/// Attached to every context the engine configures (<c>UseEmProvider</c>): right before a save it adds the
/// parked audit entries that belong to that context, so they commit or roll back with its data. Does nothing
/// when there is no writer or nothing is parked.
/// </summary>
internal sealed class AuditSaveInterceptor : SaveChangesInterceptor
{
   public static AuditSaveInterceptor Instance { get; } = new();

   public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnSaving(context);
      return result;
   }

   public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
      CancellationToken cancellationToken = default) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnSaving(context);
      return ValueTask.FromResult(result);
   }

   public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnSaved(context);
      return result;
   }

   public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
      CancellationToken cancellationToken = default) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnSaved(context);
      return ValueTask.FromResult(result);
   }

   public override void SaveChangesFailed(DbContextErrorEventData eventData) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnFailed(context);
   }

   public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default) {
      if (eventData.Context is { } context) AuditWriter.Find(context)?.OnFailed(context);
      return Task.CompletedTask;
   }
}
