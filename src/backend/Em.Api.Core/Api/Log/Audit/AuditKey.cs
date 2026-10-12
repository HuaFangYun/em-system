using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Em.Api.Core.Log.Audit;

/// <summary>
/// Canonical audit keys taken from the EF Core primary key: one column is stored as its value
/// (<c>U014</c>), a composite key as an ordered JSON array (<c>["U014","R-FIN"]</c>).
/// </summary>
public static class AuditKey
{
   /// <summary>Key of <paramref name="entity"/> according to the primary key of its type in <paramref name="context"/>.</summary>
   /// <exception cref="InvalidOperationException">The type is not part of the context's model or has no primary key.</exception>
   public static string Of(DbContext context, object entity) {
      ArgumentNullException.ThrowIfNull(context);
      ArgumentNullException.ThrowIfNull(entity);
      var key = context.Model.FindEntityType(entity.GetType())?.FindPrimaryKey()
         ?? throw new InvalidOperationException($"'{entity.GetType().Name}' has no primary key in '{context.GetType().Name}'.");
      var parts = key.Properties.Select(p => p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity)).ToArray();
      return Compose(parts);
   }

   /// <summary>Key from values in primary-key order.</summary>
   public static string Compose(params object?[] parts) {
      if (parts.Length == 0) throw new ArgumentException("A key needs at least one value.", nameof(parts));
      if (parts.Length == 1) return Format(parts[0]) ?? "";
      return JsonSerializer.Serialize(parts.Select(Format).ToArray());
   }

   private static string? Format(object? value) => value switch {
      null => null,
      DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
      IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString()
   };
}
