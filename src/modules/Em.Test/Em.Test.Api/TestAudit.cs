using Em.Api.Core.Log.Audit;
using Em.Api.Shared;
using Em.Shared;
using Em.Test.Models;
using Microsoft.EntityFrameworkCore;

namespace Em.Test.Api
{
   /// <summary>
   /// Audit of test items: the reference example of a module audit class. It wraps the engine's
   /// <see cref="IAuditWriter"/>, is taken from DI by the service, and only parks entries; they are written by the
   /// next <c>SaveChanges</c> in the same transaction as the item.
   /// </summary>
   public sealed class TestAudit(IAuditWriter audit)
   {
      /// <summary>Audit group of test items.</summary>
      public const string Group = "Em.Test.Item";

      /// <summary>Registers the group; called from <c>AddTestModule</c>.</summary>
      internal static void Register(EmAppBuilder builder) =>
         builder.AddAudit(Group, a => a
            .Title("Test items")
            .Entry("created", "Created item")
            .Entry("updated", "Updated item")
            .Entry("deleted", "Deleted item", requireReason: false)
            .Entry("exported", "Exported item", fromClient: true)
            .CanReadClaim($"{ITestServices.ModuleName}:{ITestServices.EditItemsClaim}"));

      /// <summary>A writer for the case where more than one context saves in the request.</summary>
      public TestAudit Use(DbContext context) => new(audit.Use(context));

      /// <summary>A new item.</summary>
      public void Created(ta_TestItem item) =>
         audit.AddEntry(Group, item.cTestItemId, "created", docNo: item.cTestItemCode,
            summary: $"Created item {item.cTestItemCode} '{item.cTestItemName}'", snapshot: item);

      /// <summary>A changed item, with its field changes.</summary>
      public void Updated(ta_TestItem? before, ta_TestItem after) {
         var changes = AuditDiff.Of(before, after);
         if (changes.Length == 0) return;
         audit.AddEntry(Group, after.cTestItemId, "updated", docNo: after.cTestItemCode,
            summary: $"Updated item {after.cTestItemCode} ({changes.Length} field(s))", changes: changes);
      }

      /// <summary>A deleted item.</summary>
      public void Deleted(ta_TestItem item) =>
         audit.AddEntry(Group, item.cTestItemId, "deleted", docNo: item.cTestItemCode, summary: $"Deleted item {item.cTestItemCode}");
   }
}
