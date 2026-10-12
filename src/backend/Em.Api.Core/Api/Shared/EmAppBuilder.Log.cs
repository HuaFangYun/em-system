using Em.Api.Core.Log;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Shared;

namespace Em.Api.Shared;

public partial class EmAppBuilder
{
   /// <summary>Settings of the log engine; <see cref="ApplyConfig"/> fills them from <see cref="EmApiConfig.Log"/>.</summary>
   public LogServerSettings LogSettings { get; } = new();

   internal bool LogServerAdded { get; private set; }

   internal List<AuditGroupDefinition> AuditGroups { get; } = [];

   /// <summary>
   /// Turns this API into its own log server: telemetry from <c>ILogger</c> (diagnostic, error, access) and from
   /// signed-in clients is written to the log store, audit entries of modules and of the engine
   /// (<c>System.*</c>) are written in the transaction of their data, and the Log Manager actions are available.
   /// The store is the core database unless the Log Manager configures another one.
   /// </summary>
   /// <remarks>
   /// Requires <c>doc/sqlscript/mssql/tables/070-log-audit.sql</c> and <c>071-log-store.sql</c> on the core
   /// database; startup fails without them. Without this call, audit writes are discarded and telemetry only goes
   /// to the console. One API instance per store. Guide: <c>doc/engine/engine-log.md</c>.
   /// </remarks>
   public void AddLogServer() {
      if (LogServerAdded) throw new InvalidOperationException("AddLogServer may only be called once.");
      LogServerAdded = true;
      AddService<ILogServices, LogServices>(enforceClaims: false);
      AddService<IAuditServices, AuditServices>(enforceClaims: false);
      AddClaims(ClaimAction.Create<LogServices>(ILogServices.ManagerClaim),
         ClaimAction.Create<LogServices>(ILogServices.TelemetryReadClaim),
         ClaimAction.Create<LogServices>(ILogServices.AuditReadClaim),
         ClaimAction.Create<LogServices>(ILogServices.SecurityReadClaim),
         ClaimAction.Create<LogServices>(ILogServices.SettingsClaim));
      foreach (var group in SystemAuditCatalog.Create()) AddAuditGroup(group);
   }

   /// <summary>
   /// Registers an audit group of a module: its event kinds, who may read it and its minimum retention. Entries are
   /// written through <see cref="IAuditWriter"/> (usually wrapped in a module class such as <c>InvoiceAudit</c>).
   /// The catalog is frozen when the host is built.
   /// </summary>
   /// <param name="group">Group name, e.g. <c>Invoice</c>. The prefix <c>System.</c> is reserved for the engine.</param>
   /// <param name="configure">Title, kinds, read rule, minimum retention and databases.</param>
   /// <exception cref="InvalidOperationException">The name is reserved, invalid or already registered.</exception>
   public void AddAudit(string group, Action<AuditGroupBuilder> configure) {
      if (string.IsNullOrWhiteSpace(group) || group.Length > 100)
         throw new InvalidOperationException("An audit group name must be 1-100 characters.");
      if (group.StartsWith(AuditCatalog.SystemPrefix, StringComparison.OrdinalIgnoreCase))
         throw new InvalidOperationException($"Audit group '{group}' uses the prefix '{AuditCatalog.SystemPrefix}', which is reserved for the engine.");
      var builder = new AuditGroupBuilder(group);
      configure(builder);
      if (builder.Group.Entries.Count == 0) throw new InvalidOperationException($"Audit group '{group}' has no entries.");
      AddAuditGroup(builder.Group);
   }

   private void AddAuditGroup(AuditGroupDefinition group) {
      if (AuditGroups.Any(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
         throw new InvalidOperationException($"Audit group '{group.Name}' is already registered.");
      AuditGroups.Add(group);
   }
}
