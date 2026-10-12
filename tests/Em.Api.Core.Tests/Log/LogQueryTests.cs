using Em.Api.Core.Log;
using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Em.Api.Core.Tests.Log;

public class LogQueryTests
{
   [Fact]
   public void RangeIsRequiredToBeForwardAndAtMost31Days() {
      var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
      Assert.Equal(400, Assert.Throws<ActionException>(() => LogQuery.CheckRange(from, from)).StatusCode);
      Assert.Equal(400, Assert.Throws<ActionException>(() => LogQuery.CheckRange(from, from.AddDays(40))).StatusCode);
      Assert.Equal(DateTimeKind.Utc, LogQuery.CheckRange(DateTime.SpecifyKind(from, DateTimeKind.Unspecified), from.AddDays(1)).From.Kind);
   }

   [Fact]
   public void CursorsRoundTripAndBadCursorsAre400() {
      var time = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc);
      Assert.Equal((time, 1, 42L), LogQuery.DecodeEventCursor(LogQuery.EncodeEventCursor(time, 1, 42)));
      Assert.Equal((time, "01J0"), AuditReader.DecodeCursor(AuditReader.EncodeCursor(time, "01J0")));
      Assert.Equal(400, Assert.Throws<ActionException>(() => LogQuery.DecodeEventCursor("not a cursor")).StatusCode);
      Assert.Equal(400, Assert.Throws<ActionException>(() => AuditReader.DecodeCursor("@@@")).StatusCode);
   }

   [Fact]
   public void LikeTextLosesItsWildcards() => Assert.Equal("50 done", LogQuery.Like("50% done_["));

   [Fact]
   public void IngestNormalizeTrimsAndFixesTimesAndKinds() {
      var received = DateTime.UtcNow;
      var e = new LogEvent {
         Time = received.AddHours(5), Level = LogEventLevel.Error, Exception = "System.Exception: x\n   at A.B()",
         Message = new string('m', 5000), Source = "anything", Properties = new string('p', 20_000)
      };
      LogIngest.Normalize(e, received);
      Assert.Equal(received, e.Time);
      Assert.Equal(LogEventKind.Error, e.Kind);
      Assert.Equal(4000, e.Message.Length);
      Assert.Null(e.Properties);
      Assert.Equal("server", e.Source);
      Assert.NotNull(e.Fingerprint);
   }
}

public class LogRetentionRuleTests
{
   private static LogRetention Retention() =>
      new(LogTestKit.Registry(), new LogStoreSettings(() => throw new InvalidOperationException("no database in unit tests")),
         LogTestKit.Catalog(), NullLogger<LogRetention>.Instance);

   [Fact]
   public void DefaultsFollowThePlan() {
      var rules = Retention().Complete(new LogRetentionRules());
      Assert.Equal((7, 30, 90, 30, 180, 2), (rules.InformationDays, rules.WarningDays, rules.ErrorDays, rules.AccessDays, rules.ErrorGroupDays, rules.RunHour));
      Assert.Equal(AuditCatalog.SystemDefaultMonths, rules.Audit.Single(a => a.Group == "System.Auth").Months);
      Assert.Null(rules.Audit.Single(a => a.Group == "Invoice").Months); // module audit is kept forever by default
   }

   [Fact]
   public void LowerBoundsAreEnforced() {
      var retention = Retention();
      var rules = retention.Complete(new LogRetentionRules());
      rules.Audit.Single(a => a.Group == "System.Auth").Months = 6;
      Assert.Contains("at least 12", Assert.Throws<ActionException>(() => retention.Validate(rules)).Message);

      rules = retention.Complete(new LogRetentionRules());
      rules.Audit.Single(a => a.Group == "Invoice").Months = 24;
      Assert.Contains("at least 121", Assert.Throws<ActionException>(() => retention.Validate(rules)).Message);

      rules = retention.Complete(new LogRetentionRules());
      rules.Audit.Single(a => a.Group == "System.User").Months = null;
      Assert.Throws<ActionException>(() => retention.Validate(rules));

      Assert.Throws<ActionException>(() => retention.Validate(new LogRetentionRules { ErrorGroupDays = 10 }));
      Assert.Throws<ActionException>(() => retention.Validate(new LogRetentionRules { InformationDays = 0 }));
   }

   [Fact]
   public void UnknownGroupsAreDroppedAndScopesCoverEveryKind() {
      var rules = Retention().Complete(new LogRetentionRules { Audit = [new LogAuditRetention { Group = "Gone", Months = 1 }] });
      Assert.DoesNotContain(rules.Audit, a => a.Group == "Gone");
      var scopes = LogRetention.Scopes(rules, DateTime.UtcNow).Select(s => s.Name).ToList();
      Assert.Contains("Telemetry Information", scopes);
      Assert.Contains("Access", scopes);
      Assert.Contains("Error groups", scopes);
      Assert.Contains("Audit System.Auth", scopes);
      Assert.DoesNotContain("Audit Invoice", scopes);
   }
}

public class LogStoreSettingsTests
{
   [Fact]
   public void ConnectionStringsArePerProviderAndSecretsRoundTrip() {
      Assert.Contains("Data Source=db,1444", LogStoreSettings.BuildConnectionString(LogStoreProvider.MicrosoftSqlServer, "db", 1444, "logs", false, "u", "p", true, true));
      Assert.Contains("Port=3307", LogStoreSettings.BuildConnectionString(LogStoreProvider.MySql, "db", 3307, "logs", false, "u", "p", false, false));
      Assert.Contains("Database=postgres", LogStoreSettings.BuildConnectionString(LogStoreProvider.PostgreSql, "db", null, "logs", false, "u", "p", false, false, withoutDatabase: true));
      Assert.Throws<ActionException>(() => LogStoreSettings.BuildConnectionString(LogStoreProvider.MySql, "", null, "x", false, null, null, false, false));
      var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
      Assert.Equal("s3cret", LogSecrets.Decrypt(key, LogSecrets.Encrypt(key, "s3cret")));
   }

   [Fact]
   public void RegistryFallsBackToTheInternalStoreAndKeepsReadStores() {
      var warnings = new List<string>();
      var doc = new LogStoreDocument { Stores = [
         new LogStoreEntry { Id = "A", Name = "Old", Kind = LogStoreKind.Internal, Role = LogStoreRole.Read },
         new LogStoreEntry { Id = "B", Name = "Broken", Kind = LogStoreKind.Connection, Role = LogStoreRole.Write, Provider = LogStoreProvider.MySql }
      ] };
      var registry = LogStoreRegistry.Resolve(doc, null, LogTestKit.Core, [LogTestKit.Core], warnings.Add);
      Assert.Equal(LogStoreKind.Internal, registry.Write.Kind);
      Assert.NotEmpty(warnings);
      Assert.True(registry.IsStoreDatabase(LogTestKit.Core.ConnectionString));
   }

   [Fact]
   public void PrepareScriptsAreEmbeddedForEveryProviderAndNeverDrop() {
      foreach (var provider in Enum.GetValues<LogStoreProvider>()) {
         var script = LogStorePrepare.Script(provider);
         Assert.Contains("ta_LogEvent", script);
         Assert.Contains("ta_LogAudit", script);
         Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
      }
      Assert.Contains("[dbo].[ta_LogError] TO [log_user]", string.Join("\n", LogStorePrepare.GrantStatements(LogStoreProvider.MicrosoftSqlServer, "logs", "log_user")));
   }
}
