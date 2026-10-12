using Em.Api.Core.Log;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core
{
   public partial class EmApp
   {
      /// <summary>
      /// Registers the audit writer (a discarding one while the log server is off) and, with
      /// <c>AddLogServer</c>, the log store, the <c>ILogger</c> provider and the background services. The store
      /// list is read from the core database here, before the host is built, because the stores are fixed until
      /// the next restart.
      /// </summary>
      private void ApplyLogRegistration(EmAppBuilder builder) {
         Services.AddScoped<AuthAudit>();
         Services.AddScoped<PasswordAudit>();
         Services.AddScoped<UserAudit>();
         Services.AddScoped<RoleAudit>();
         Services.AddScoped<SystemAudit>();

         if (!builder.LogServerAdded) {
            Services.AddSingleton(AuditCatalog.Empty);
            Services.AddScoped<IAuditWriter, NullAuditWriter>();
            BootstrapLogBuffer.TakeAll();
            return;
         }

         var settings = builder.LogSettings;
         var catalog = new AuditCatalog(builder.AuditGroups);
         var runtime = new LogRuntime(settings.AppName, Builder.Environment.EnvironmentName);
         var core = builder.DbConnections[EmAppBuilder.DefaultConnectionName];
         var sources = new List<DbConnectionInfo> { core };
         foreach (var name in builder.AuditGroups.SelectMany(g => g.Connections).Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!builder.DbConnections.TryGetValue(name, out var info))
               throw new InvalidOperationException($"An audit group names database connection '{name}', which is not registered.");
            if (!sources.Any(s => LogStoreRegistry.SameDatabase(s.ConnectionString, info.ConnectionString))) sources.Add(info);
         }

         var storeSettings = new LogStoreSettings(() => {
            var options = new DbContextOptionsBuilder<ApiCoreContext>();
            options.UseEmProvider(core.Provider, core.ConnectionString);
            return new ApiCoreContext(options.Options);
         });

         var warnings = new List<string>();
         LogStoreDocument document;
         byte[]? key = null;
         try {
            document = storeSettings.ReadStores();
            key = storeSettings.Key();
         }
         catch (Exception ex) {
            warnings.Add($"The log store settings could not be read, the internal database is used: {ex.GetBaseException().Message}");
            document = LogStoreDocument.Default();
         }

         var registry = LogStoreRegistry.Resolve(document, key, core, sources, warnings.Add);
         var queue = new LogQueue(settings.QueueCapacity, runtime);
         var provider = new EmLogProvider(queue, runtime, settings.StoreRequestStartLines);
         Builder.Logging.AddProvider(provider);
         using (var bootstrap = LoggerFactory.Create(b => b.AddProvider(provider).AddSimpleConsole(o => o.SingleLine = true))) {
            var log = bootstrap.CreateLogger("Em.Log");
            foreach (var warning in warnings) log.LogWarning("{Warning}", warning);
            log.LogInformation("Log server enabled; write store '{Store}' ({Provider}), {ReadStores} read store(s).",
               registry.Write.Name, registry.Write.Provider, registry.Read.Count);
         }

         var logPath = Path.GetFullPath(Path.IsPathRooted(settings.LogPath)
            ? settings.LogPath
            : Path.Combine(Builder.Environment.ContentRootPath, settings.LogPath));

         Services.AddSingleton(catalog);
         Services.AddSingleton(runtime);
         Services.AddSingleton(queue);
         Services.AddSingleton(registry);
         Services.AddSingleton(storeSettings);
         Services.AddSingleton(new LogSegmentFolder(Path.Combine(logPath, "outbox"), settings.OutboxMaxBytes, runtime));
         Services.AddSingleton<ILogIngest, LogIngest>();
         Services.AddSingleton<AuditReader>();
         Services.AddSingleton<IAuditReader>(sp => sp.GetRequiredService<AuditReader>());
         Services.AddSingleton<LogQuery>();
         Services.AddSingleton<LogRetention>();
         Services.AddScoped<AuditWriter>();
         Services.AddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditWriter>());
         // Order matters: the schema check runs first and stops startup with a clear message.
         Services.AddHostedService<LogStartupCheck>();
         Services.AddHostedService<LogWriterBackground>();
         Services.AddSingleton<AuditShipper>();
         Services.AddHostedService(sp => sp.GetRequiredService<AuditShipper>());
         Services.AddHostedService<LogRetentionBackground>();
      }

      /// <summary>
      /// Reports audit entries a request left parked (added without a following <c>SaveChanges</c>): a bug in the
      /// module that only shows up this way during testing.
      /// </summary>
      private static void ReportUnsavedAudit(Microsoft.AspNetCore.Http.HttpContext http, string routeLabel) {
         if (http.Items[AuditWriter.HttpItemKey] is not AuditWriter writer || writer.PendingCount == 0) return;
         var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Em.Log.Audit");
         logger.LogError("Audit entry not saved in '{Route}': {Entries}. Add entries before SaveChanges, or call IAuditWriter.SaveAsync.",
            routeLabel, string.Join(", ", writer.PendingLabels));
      }

      // Debug token use is audited at most once per key and account per hour: every request of a developer
      // carries the token, and one entry per request would bury everything else.
      private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> DebugAudited = new();

      private static bool DebugAuditDue(string keyName, string? userId) {
         var now = DateTime.UtcNow;
         var key = $"{keyName}|{userId}";
         if (DebugAudited.TryGetValue(key, out var last) && now - last < TimeSpan.FromHours(1)) return false;
         DebugAudited[key] = now;
         return true;
      }

      // Audit from the gate must never decide the answer: a store problem is logged, the request goes on.
      private static async Task TryAuditAsync(Microsoft.AspNetCore.Http.HttpContext http, Func<AuthAudit, Task> write) {
         try {
            await write(http.RequestServices.GetRequiredService<AuthAudit>());
         }
         catch (Exception ex) {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Em.Log.Audit")
               .LogError(ex, "System.Auth audit could not be written.");
         }
      }
   }
}
