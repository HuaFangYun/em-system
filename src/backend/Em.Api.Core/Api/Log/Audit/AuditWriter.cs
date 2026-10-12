using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Log.Audit;

/// <summary>Implementation of <see cref="IAuditWriter"/> used while the log server is on.</summary>
internal sealed class AuditWriter : IAuditWriter
{
   internal const string HttpItemKey = "Em.AuditWriter";
   internal const string RequestIdItemKey = "Em.RequestId";
   private const int MaxSnapshotLength = 1024 * 1024;
   private const int MaxDetailLength = 16 * 1024;
   private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

   // Contexts that received a Use(...) or SaveAsync: the interceptor finds the writer through them when there
   // is no HTTP request (business tasks, background work).
   private static readonly ConditionalWeakTable<DbContext, AuditWriter> Directed = new();

   private readonly AuditCatalog _catalog;
   private readonly LogStoreRegistry _stores;
   private readonly LogRuntime _runtime;
   private readonly IServiceProvider _services;
   private readonly IHttpContextAccessor _http;
   private readonly List<Pending> _pending = [];
   private DbContext? _autoTarget;
   private string? _accountCache;
   private bool _accountResolved;

   public AuditWriter(AuditCatalog catalog, LogStoreRegistry stores, LogRuntime runtime, IServiceProvider services,
      IHttpContextAccessor http) {
      _catalog = catalog;
      _stores = stores;
      _runtime = runtime;
      _services = services;
      _http = http;
      if (http.HttpContext is { } context) context.Items[HttpItemKey] = this;
   }

   private sealed class Pending(ta_LogAudit row, DbContext? target, string label)
   {
      public ta_LogAudit Row { get; } = row;
      public DbContext? Target { get; } = target;
      public string Label { get; } = label;
      public DbContext? SavingIn { get; set; }
   }

   public int PendingCount { get { lock (_pending) return _pending.Count; } }

   internal IReadOnlyList<string> PendingLabels { get { lock (_pending) return _pending.Select(p => p.Label).ToList(); } }

   public void AddEntry(AuditEntryDraft entry) => Add(entry, null);

   public void AddEntry(string group, string key, string kind, string? docNo = null, string? summary = null, string? reason = null,
      IReadOnlyList<AuditFieldChange>? changes = null, object? snapshot = null, object? detail = null) =>
      Add(new AuditEntryDraft {
         Group = group, Key = key, Kind = kind, DocNo = docNo, Summary = summary, Reason = reason, Changes = changes,
         Snapshot = snapshot, Detail = detail
      }, null);

   public IAuditWriter Use(DbContext context) {
      ArgumentNullException.ThrowIfNull(context);
      Directed.AddOrUpdate(context, this);
      return new DirectedWriter(this, context);
   }

   public async Task SaveAsync(DbContext context, CancellationToken ct = default) {
      Directed.AddOrUpdate(context, this);
      lock (_pending) {
         for (var i = 0; i < _pending.Count; i++)
            if (_pending[i].Target is null) _pending[i] = new Pending(_pending[i].Row, context, _pending[i].Label);
      }
      await context.SaveChangesAsync(ct);
   }

   public async Task WriteNowAsync(AuditEntryDraft entry, CancellationToken ct = default) {
      var row = Build(entry);
      await using var db = LogOutboxDbContext.Create(_stores.Core);
      row.cLogAuditShipState = _stores.IsStoreDatabase(_stores.Core.ConnectionString) ? 0 : 1;
      db.Audit.Add(row);
      using (LogGuard.Suppress()) await db.SaveChangesAsync(ct);
   }

   private void Add(AuditEntryDraft entry, DbContext? target) {
      var row = Build(entry);
      lock (_pending) _pending.Add(new Pending(row, target, $"{entry.Group}/{entry.Kind} ({entry.Key})"));
   }

   /// <summary>Validates against the catalog and fills the engine-owned fields.</summary>
   internal ta_LogAudit Build(AuditEntryDraft entry) {
      var group = _catalog.Require(entry.Group);
      if (!group.Entries.TryGetValue(entry.Kind, out var kind))
         throw new InvalidOperationException($"Audit kind '{entry.Kind}' is not registered in group '{entry.Group}'.");
      if (kind.RequireReason && string.IsNullOrWhiteSpace(entry.Reason))
         throw new ActionException($"A reason is required for '{kind.Label}'.", 400);
      if (string.IsNullOrWhiteSpace(entry.Key)) throw new InvalidOperationException($"Audit entry {entry.Group}/{entry.Kind} has no key.");

      var changes = entry.Changes?.Select(c => new AuditFieldChange {
         Row = c.Row, Field = c.Field, Label = c.Label, Old = c.Old, New = c.New, OldText = c.OldText, NewText = c.NewText, Secret = c.Secret
      }).ToArray();
      if (changes is not null) foreach (var c in changes.Where(c => c.Secret)) AuditDiff.Redact(c);

      var http = _http.HttpContext;
      var request = _services.GetService<EmApp>()?.CurrentRequest ?? _services.GetService<BusinessTaskStarter>()?.Request ?? ActionRequest.None;
      var (actorType, actorUserId, actorAccount, actingAs) = ActorOf(request, http is null);

      return new ta_LogAudit {
         cLogAuditId = Ulid.NewUlid().ToString(),
         cLogAuditGroup = entry.Group,
         cLogAuditKind = entry.Kind,
         cLogAuditTime = DateTime.UtcNow,
         cLogAuditActorType = entry.ActorType ?? actorType,
         cLogAuditActorUserId = Cut(entry.ActorUserId ?? (entry.ActorType is null ? actorUserId : null), 26),
         cLogAuditActorAccount = Cut(entry.ActorAccount ?? (entry.ActorType is null ? actorAccount : null), 100),
         cLogAuditActingAs = Cut(entry.ActorType is null ? actingAs : null, 100),
         cLogAuditTargetType = Cut(entry.TargetType ?? entry.Group, 100),
         cLogAuditTargetKey = Cut(entry.Key, 400),
         cLogAuditDocNo = Cut(entry.DocNo, 100),
         cLogAuditSummary = Cut(entry.Summary, 1000),
         cLogAuditReason = Cut(entry.Reason, 1000),
         cLogAuditChanges = changes is { Length: > 0 } ? JsonSerializer.Serialize(changes, Json) : null,
         cLogAuditSnapshot = Serialize(entry.Snapshot, MaxSnapshotLength),
         cLogAuditDetail = Serialize(entry.Detail, MaxDetailLength),
         cLogAuditSource = entry.Source,
         cLogAuditApp = Cut(_runtime.App, 100),
         cLogAuditInstance = Cut(_runtime.Instance, 100),
         cLogAuditClientAddress = Cut(request.CallerAddress ?? http?.Connection.RemoteIpAddress?.ToString(), 64),
         cLogAuditRequestId = Cut(http?.Items[RequestIdItemKey] as string, 100),
         cLogAuditTraceId = Activity.Current?.TraceId.ToString(),
         cLogAuditSessionId = Cut(entry.SessionId ?? request.cUserSessionId, 26)
      };
   }

   private (string Type, string? UserId, string? Account, string? ActingAs) ActorOf(ActionRequest request, bool background) {
      if (request.IsDebugRequest) {
         return (AuditActorTypes.Debug, request.cUserId, $"debug:{request.DebugKeyName}",
            request.IsImpersonating ? request.cUserAccount : null);
      }
      if (request.IsAuthenticated) return (AuditActorTypes.User, request.cUserId, request.cUserAccount ?? AccountOf(request.cUserId!), null);
      return background ? (AuditActorTypes.System, null, null, null) : (AuditActorTypes.Anonymous, null, null, null);
   }

   // The access token carries only the id; the account name is looked up once per request for the audit.
   private string? AccountOf(string userId) {
      if (_accountResolved) return _accountCache;
      _accountResolved = true;
      if (userId == Defaults.AdminUserId) return _accountCache = Defaults.AdminUserAccount;
      if (userId == Defaults.DebuggerUserId) return _accountCache = Defaults.DebuggerUserAccount;
      try {
         using var _ = LogGuard.Suppress();
         using var db = _services.GetRequiredService<IDbContextFactory<Em.Api.Core.Models.ApiCoreContext>>().CreateDbContext();
         _accountCache = db.ta_Users.AsNoTracking().Where(u => u.cUserId == userId).Select(u => u.cUserAccount).FirstOrDefault();
      }
      catch {
         _accountCache = null;
      }
      return _accountCache;
   }

   private static string? Serialize(object? value, int max) {
      if (value is null) return null;
      var text = value as string ?? JsonSerializer.Serialize(value, Json);
      return text.Length <= max ? text : null;
   }

   private static string? Cut(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

   /// <summary>Finds the writer of a context that is saving: directed contexts first, then the HTTP request.</summary>
   internal static AuditWriter? Find(DbContext context) {
      if (Directed.TryGetValue(context, out var directed)) return directed;
      return new HttpContextAccessor().HttpContext?.Items[HttpItemKey] as AuditWriter;
   }

   /// <summary>Adds the entries that belong to <paramref name="context"/> to it, right before it saves.</summary>
   internal void OnSaving(DbContext context) {
      // The log engine's own contexts never take module entries.
      if (context is LogOutboxDbContext or LogStoreDbContext) return;
      List<Pending> take;
      lock (_pending) {
         take = _pending.Where(p => p.SavingIn is null && ReferenceEquals(p.Target, context)).ToList();
         var undirected = _pending.Where(p => p.SavingIn is null && p.Target is null).ToList();
         if (undirected.Count > 0) {
            _autoTarget ??= context;
            if (!ReferenceEquals(_autoTarget, context)) {
               throw new InvalidOperationException(
                  $"Audit entries {string.Join(", ", undirected.Select(p => p.Label))} are not directed, and a second context " +
                  $"('{context.GetType().Name}' after '{_autoTarget.GetType().Name}') is saving in this request. " +
                  "Call audit.Use(context) to choose the database the entries belong to.");
            }
            take.AddRange(undirected);
         }
         if (take.Count == 0) return;
         if (context.Model.FindEntityType(typeof(ta_LogAudit)) is null)
            throw new InvalidOperationException($"'{context.GetType().Name}' cannot hold audit entries; derive it from EmDbContext and call base.OnModelCreating.");
         var shipState = _stores.IsStoreDatabase(context.Database.GetConnectionString()) ? 0 : 1;
         foreach (var p in take) {
            p.SavingIn = context;
            p.Row.cLogAuditShipState = shipState;
            context.Add(p.Row);
         }
      }
   }

   /// <summary>The save succeeded: the entries are written (they commit or roll back with its transaction).</summary>
   internal void OnSaved(DbContext context) {
      lock (_pending) _pending.RemoveAll(p => ReferenceEquals(p.SavingIn, context));
   }

   /// <summary>The save failed: the entries stay parked for a retry.</summary>
   internal void OnFailed(DbContext context) {
      lock (_pending) {
         foreach (var p in _pending.Where(p => ReferenceEquals(p.SavingIn, context))) {
            var entry = context.Entry(p.Row);
            if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
            p.SavingIn = null;
         }
      }
   }

   private sealed class DirectedWriter(AuditWriter owner, DbContext context) : IAuditWriter
   {
      public int PendingCount => owner.PendingCount;
      public void AddEntry(AuditEntryDraft entry) => owner.Add(entry, context);

      public void AddEntry(string group, string key, string kind, string? docNo = null, string? summary = null, string? reason = null,
         IReadOnlyList<AuditFieldChange>? changes = null, object? snapshot = null, object? detail = null) =>
         owner.Add(new AuditEntryDraft {
            Group = group, Key = key, Kind = kind, DocNo = docNo, Summary = summary, Reason = reason, Changes = changes,
            Snapshot = snapshot, Detail = detail
         }, context);

      public IAuditWriter Use(DbContext other) => owner.Use(other);
      public Task WriteNowAsync(AuditEntryDraft entry, CancellationToken ct = default) => owner.WriteNowAsync(entry, ct);
      public Task SaveAsync(DbContext other, CancellationToken ct = default) => owner.SaveAsync(other, ct);
   }
}

/// <summary>Writer used while the log server is off: entries are accepted and discarded.</summary>
internal sealed class NullAuditWriter : IAuditWriter
{
   public int PendingCount => 0;
   public void AddEntry(AuditEntryDraft entry) { }
   public void AddEntry(string group, string key, string kind, string? docNo = null, string? summary = null, string? reason = null,
      IReadOnlyList<AuditFieldChange>? changes = null, object? snapshot = null, object? detail = null) { }
   public IAuditWriter Use(DbContext context) => this;
   public Task WriteNowAsync(AuditEntryDraft entry, CancellationToken ct = default) => Task.CompletedTask;
   public Task SaveAsync(DbContext context, CancellationToken ct = default) => context.SaveChangesAsync(ct);
}
