using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Em.Api.Core.Models;

namespace Em.Api.Core.Log.Audit;

/// <summary>Builds the field changes of an entry by comparing two copies of an object (manual diff).</summary>
public static class AuditDiff
{
   /// <summary>Columns that are never compared: the standard stamps.</summary>
   public static readonly IReadOnlyCollection<string> StandardIgnore = ["ustamp", "datestamp", "json_object"];

   /// <summary>Text recorded for a secret field that received a value.</summary>
   public const string SecretSet = "(set)";

   /// <summary>Text recorded for a secret field whose value changed.</summary>
   public const string SecretChanged = "(changed)";

   /// <summary>Text recorded for a secret field that was cleared.</summary>
   public const string SecretCleared = "(cleared)";

   /// <summary>
   /// Compares the public readable properties of simple type (text, numbers, dates, enums, ids) of
   /// <paramref name="before"/> and <paramref name="after"/>. <c>null</c> for <paramref name="before"/> means
   /// an insert (every non-null field is listed); <c>null</c> for <paramref name="after"/> a delete.
   /// </summary>
   /// <param name="before">Copy before the change.</param>
   /// <param name="after">Copy after the change.</param>
   /// <param name="ignore">Extra properties to skip (the standard stamps are always skipped).</param>
   /// <param name="secret">Properties whose values are never recorded.</param>
   /// <param name="labels">Display labels by property name; otherwise <c>[Display(Name)]</c>, else the name.</param>
   /// <param name="row">Row label for child rows.</param>
   public static AuditFieldChange[] Of<T>(T? before, T? after, IEnumerable<string>? ignore = null, IEnumerable<string>? secret = null,
      IReadOnlyDictionary<string, string>? labels = null, string? row = null) where T : class {
      if (before is null && after is null) return [];
      var skip = new HashSet<string>(StandardIgnore, StringComparer.OrdinalIgnoreCase);
      if (ignore is not null) skip.UnionWith(ignore);
      var secrets = new HashSet<string>(secret ?? [], StringComparer.OrdinalIgnoreCase);
      var changes = new List<AuditFieldChange>();
      foreach (var p in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
         if (!p.CanRead || p.GetIndexParameters().Length > 0 || skip.Contains(p.Name) || !IsSimple(p.PropertyType)) continue;
         var oldValue = before is null ? null : p.GetValue(before);
         var newValue = after is null ? null : p.GetValue(after);
         if (Equals(oldValue, newValue)) continue;
         if (before is null && newValue is null) continue;
         var label = labels?.GetValueOrDefault(p.Name) ?? p.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? p.Name;
         var change = new AuditFieldChange { Row = row, Field = p.Name, Label = label, Old = Format(oldValue), New = Format(newValue) };
         if (secrets.Contains(p.Name)) Redact(change);
         changes.Add(change);
      }
      return [.. changes];
   }

   /// <summary>Replaces the values of a secret change with <see cref="SecretSet"/>, <see cref="SecretChanged"/> or <see cref="SecretCleared"/>.</summary>
   public static void Redact(AuditFieldChange change) {
      var hadOld = !string.IsNullOrEmpty(change.Old);
      var hasNew = !string.IsNullOrEmpty(change.New);
      change.Secret = true;
      change.Old = null;
      change.OldText = null;
      change.NewText = null;
      change.New = !hadOld && hasNew ? SecretSet : hadOld && !hasNew ? SecretCleared : SecretChanged;
   }

   internal static string? Format(object? value) => value switch {
      null => null,
      DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
      DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
      bool b => b ? "true" : "false",
      IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString()
   };

   private static bool IsSimple(Type type) {
      type = Nullable.GetUnderlyingType(type) ?? type;
      return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) ||
             type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateOnly) ||
             type == typeof(TimeOnly) || (type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type) && type.FullName == "System.Ulid");
   }
}
