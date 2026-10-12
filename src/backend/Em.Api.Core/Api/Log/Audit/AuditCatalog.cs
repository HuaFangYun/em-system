using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Api.Core.Log.Audit;

/// <summary>
/// Declares one audit group for <c>EmAppBuilder.AddAudit</c>: its title, event kinds, who may read it, the
/// shortest retention allowed and the databases its entries are written to.
/// </summary>
public sealed class AuditGroupBuilder
{
   internal AuditGroupBuilder(string name) => Group = new AuditGroupDefinition(name);

   internal AuditGroupDefinition Group { get; }

   /// <summary>Display title of the group (English, shown in the Log Manager).</summary>
   public AuditGroupBuilder Title(string title) {
      Group.Title = title;
      return this;
   }

   /// <summary>Registers an event kind. Entries with an unregistered kind are refused.</summary>
   /// <param name="kind">Kind, e.g. <c>voided</c>; letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</param>
   /// <param name="label">Display label, e.g. <c>Voided invoice</c>.</param>
   /// <param name="requireReason">A reason must be given with every entry of this kind.</param>
   /// <param name="fromClient">Clients may report this kind through <c>PostMeta_AuditEntry</c>.</param>
   public AuditGroupBuilder Entry(string kind, string label, bool requireReason = false, bool fromClient = false) {
      if (string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || !kind.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
         throw new ArgumentException($"Audit kind '{kind}' of group '{Group.Name}' is not valid.", nameof(kind));
      if (!Group.Entries.TryAdd(kind, new AuditCatalogEntry { Kind = kind, Label = label, RequireReason = requireReason, FromClient = fromClient }))
         throw new ArgumentException($"Audit kind '{kind}' is registered twice in group '{Group.Name}'.", nameof(kind));
      return this;
   }

   /// <summary>Who may read entries of this group (administrators and debug tokens always may). Default: administrators only.</summary>
   public AuditGroupBuilder CanRead(Func<ActionRequest, bool> rule) {
      Group.CanRead = rule ?? throw new ArgumentNullException(nameof(rule));
      return this;
   }

   /// <summary>Shortcut for <see cref="CanRead"/>: callers holding claim <paramref name="claimKey"/> (<c>Module:Name</c>).</summary>
   public AuditGroupBuilder CanReadClaim(string claimKey) {
      var claim = ClaimAction.FromKey(claimKey);
      return CanRead(r => r.Claims.Any(c => string.Equals(c.ModuleName, claim.ModuleName, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(c.Name, claim.Name, StringComparison.OrdinalIgnoreCase)));
   }

   /// <summary>Shortest retention the Log Manager may set for this group (e.g. ten years for bookkeeping).</summary>
   public AuditGroupBuilder MinRetention(TimeSpan minimum) {
      if (minimum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimum));
      Group.MinRetention = minimum;
      return this;
   }

   /// <summary>
   /// Names a database connection whose context writes entries of this group, so startup checks that its
   /// <c>ta_LogAudit</c> table exists and the shipper moves its entries. The core database is always included.
   /// </summary>
   public AuditGroupBuilder Database(string connectionName) {
      Group.Connections.Add(connectionName);
      return this;
   }
}

/// <summary>A frozen audit group.</summary>
internal sealed class AuditGroupDefinition(string name)
{
   public string Name { get; } = name;
   public string Title { get; set; } = name;
   public Dictionary<string, AuditCatalogEntry> Entries { get; } = new(StringComparer.Ordinal);
   public Func<ActionRequest, bool>? CanRead { get; set; }
   public TimeSpan? MinRetention { get; set; }
   public HashSet<string> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);
   public bool IsSystem => Name.StartsWith(AuditCatalog.SystemPrefix, StringComparison.Ordinal);

   public int? MinRetentionMonths => MinRetention is { } m ? (int)Math.Ceiling(m.TotalDays / 30.4375) : IsSystem ? AuditCatalog.SystemMinMonths : null;

   /// <summary>Administrators and debug tokens always read; others follow <see cref="CanRead"/>.</summary>
   public bool Allows(ActionRequest request) =>
      request.IsAuthenticated && (request.IsAdmin || request.IsDebugRequest || CanRead?.Invoke(request) == true);

   public AuditCatalogGroup ToDto() => new() {
      Name = Name, Title = Title, IsSystem = IsSystem, MinRetentionMonths = MinRetentionMonths,
      Entries = [.. Entries.Values.OrderBy(e => e.Kind, StringComparer.Ordinal)]
   };
}

/// <summary>Every audit group, frozen when the host is built (like the claim catalog).</summary>
internal sealed class AuditCatalog
{
   /// <summary>Prefix reserved for engine groups.</summary>
   public const string SystemPrefix = "System.";

   /// <summary>Engine minimum retention of system audit, in months.</summary>
   public const int SystemMinMonths = 12;

   /// <summary>Default retention of system audit, in months.</summary>
   public const int SystemDefaultMonths = 24;

   private readonly Dictionary<string, AuditGroupDefinition> _groups;

   public AuditCatalog(IEnumerable<AuditGroupDefinition> groups) {
      _groups = groups.ToDictionary(g => g.Name, StringComparer.Ordinal);
   }

   /// <summary>An empty catalog, used while the log server is off.</summary>
   public static AuditCatalog Empty { get; } = new([]);

   public IReadOnlyCollection<AuditGroupDefinition> Groups => _groups.Values;

   public AuditGroupDefinition? Find(string group) => _groups.GetValueOrDefault(group);

   public AuditGroupDefinition Require(string group) =>
      Find(group) ?? throw new InvalidOperationException($"Audit group '{group}' is not registered. Register it with AddAudit.");

   public string? LabelOf(string group, string kind) =>
      Find(group)?.Entries.GetValueOrDefault(kind)?.Label;
}
