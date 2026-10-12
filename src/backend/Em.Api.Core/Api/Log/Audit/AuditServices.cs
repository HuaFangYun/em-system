using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Log.Audit;

/// <summary>Audit activity actions for module screens; every action checks the group's read rule.</summary>
[Module(Defaults.AuditModuleName)]
public sealed class AuditServices : ServicesBase, IAuditServices
{
   private AuditReader Reader => App.ServiceProvider.GetRequiredService<AuditReader>();
   private AuditCatalog Catalog => App.ServiceProvider.GetRequiredService<AuditCatalog>();

   /// <inheritdoc />
   [GetAction]
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string group, string key, string? cursor, int take) {
      RequireRead(group);
      return Reader.ByDocAsync(group, key, cursor, Math.Clamp(take, 1, 200), AbortToken);
   }

   /// <inheritdoc />
   [GetAction]
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByDocNo(string group, string docNo, string? cursor, int take) {
      RequireRead(group);
      return Reader.ByDocNoAsync(group, docNo, cursor, Math.Clamp(take, 1, 200), AbortToken);
   }

   /// <inheritdoc />
   [GetAction]
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string group, DateTime? from, DateTime? to, string? userId, string? kinds,
      string? cursor, int take) {
      RequireRead(group);
      var kindList = string.IsNullOrWhiteSpace(kinds) ? [] : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      return Reader.ByTypeAsync(group, from, to, string.IsNullOrWhiteSpace(userId) ? null : userId, kindList, cursor, Math.Clamp(take, 1, 200), AbortToken);
   }

   /// <inheritdoc />
   [GetAction]
   public Task<AuditActivityPage> GetMeta_AuditActivity_ByTarget(string targetType, string key, string groups, string? cursor, int take) {
      Request.RequireUserId();
      var allowed = groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Where(g => Catalog.Find(g)?.Allows(Request) == true).Distinct().ToArray();
      if (allowed.Length == 0) throw new ActionException("You may not read any of these audit groups.", 403);
      return Reader.QueryAsync(new AuditQuery { Groups = allowed, TargetType = targetType, TargetKey = key, Cursor = cursor, Take = Math.Clamp(take, 1, 200) }, AbortToken);
   }

   /// <inheritdoc />
   [GetAction]
   public Task<AuditActivity[]> GetMeta_AuditActivity_Since(string group, string? key, DateTime afterTime, string? afterId) {
      RequireRead(group);
      return Reader.SinceAsync(group, string.IsNullOrWhiteSpace(key) ? null : key, DateTime.SpecifyKind(afterTime, DateTimeKind.Utc), afterId, AbortToken);
   }

   /// <inheritdoc />
   [PostAction]
   public async Task PostMeta_AuditEntry(string group, string key, string kind, string? docNo, string? detail) {
      var definition = RequireRead(group);
      if (!definition.Entries.TryGetValue(kind, out var entry) || !entry.FromClient)
         throw new ActionException($"'{group}/{kind}' cannot be reported by a client.", 403);
      if (detail is { Length: > 2000 }) throw new ActionException("The detail may be at most 2000 characters.", 400);
      var writer = App.ServiceProvider.GetRequiredService<AuditWriter>();
      var row = writer.Build(new AuditEntryDraft { Group = group, Key = key, Kind = kind, DocNo = docNo, Summary = detail, Source = "client" });
      var stores = App.ServiceProvider.GetRequiredService<LogStoreRegistry>();
      await using var db = LogOutboxDbContext.Create(stores.Core);
      row.cLogAuditShipState = stores.IsStoreDatabase(stores.Core.ConnectionString) ? 0 : 1;
      db.Audit.Add(row);
      await db.SaveChangesAsync(CancellationToken.None);
   }

   private AuditGroupDefinition RequireRead(string group) {
      Request.RequireUserId();
      var definition = Catalog.Find(group) ?? throw new ActionException($"Audit group '{group}' is not registered.", 404);
      if (!definition.Allows(Request)) throw new ActionException($"You may not read the audit of '{group}'.", 403);
      return definition;
   }
}
