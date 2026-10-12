using Em.Api.Core.Log.Audit;
using Em.Api.Core.Models;
using Em.Api.Shared;
using Em.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Em.Api.Core.Tests.Log;

public class AuditCatalogTests
{
   [Fact]
   public void ModulesCannotRegisterSystemGroups() {
      var builder = new EmAppBuilder { Services = new ServiceCollection() };
      Assert.Throws<InvalidOperationException>(() => builder.AddAudit("System.Invoice", a => a.Entry("created", "Created")));
   }

   [Fact]
   public void AGroupNeedsEntriesAndAUniqueName() {
      var builder = new EmAppBuilder { Services = new ServiceCollection() };
      Assert.Throws<InvalidOperationException>(() => builder.AddAudit("Empty", _ => { }));
      builder.AddAudit("Invoice", a => a.Entry("created", "Created"));
      Assert.Throws<InvalidOperationException>(() => builder.AddAudit("invoice", a => a.Entry("created", "Created")));
   }

   [Fact]
   public void UnknownKindIsRefusedBeforeAnythingIsSaved() {
      var writer = LogTestKit.Writer();
      Assert.Throws<InvalidOperationException>(() => writer.AddEntry("Invoice", "INV-1", "posted"));
      Assert.Throws<InvalidOperationException>(() => writer.AddEntry("Nope", "INV-1", "created"));
      Assert.Equal(0, writer.PendingCount);
   }

   [Fact]
   public void RequiredReasonIsEnforced() {
      var writer = LogTestKit.Writer();
      Assert.Equal(400, Assert.Throws<ActionException>(() => writer.AddEntry("Invoice", "INV-1", "voided")).StatusCode);
      writer.AddEntry("Invoice", "INV-1", "voided", reason: "Wrong customer");
      Assert.Equal(1, writer.PendingCount);
   }

   [Fact]
   public void ReadRuleAllowsAdminsAndClaimHoldersOnly() {
      var group = LogTestKit.Catalog().Require("Invoice");
      Assert.False(group.Allows(ActionRequest.None));
      Assert.True(group.Allows(new ActionRequest { cUserId = "U1", IsAdmin = true }));
      Assert.False(group.Allows(new ActionRequest { cUserId = "U2" }));
      Assert.True(group.Allows(new ActionRequest { cUserId = "U3", Claims = [ClaimAction.FromKey("Invoice:View")] }));
   }

   [Fact]
   public void MinRetentionAndFromClientAreInTheCatalog() {
      var dto = LogTestKit.Catalog().Require("Invoice").ToDto();
      Assert.Equal(121, dto.MinRetentionMonths);
      Assert.True(dto.Entries.Single(e => e.Kind == "exported").FromClient);
      Assert.Equal(AuditCatalog.SystemMinMonths, LogTestKit.Catalog().Require("System.Auth").ToDto().MinRetentionMonths);
   }

   [Fact]
   public void SecretChangesAreRedactedWhenWritten() {
      var writer = LogTestKit.Writer();
      var row = writer.Build(new AuditEntryDraft {
         Group = "Invoice", Key = "INV-1", Kind = "created",
         Changes = [new AuditFieldChange { Field = "Password", Old = "old-secret", New = "new-secret", Secret = true }]
      });
      Assert.DoesNotContain("secret", row.cLogAuditChanges);
      Assert.Contains(AuditDiff.SecretChanged, row.cLogAuditChanges);
      Assert.Equal(AuditActorTypes.System, row.cLogAuditActorType);
      Assert.Equal("Invoice", row.cLogAuditTargetType);
   }
}
