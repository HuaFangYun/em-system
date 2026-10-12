using System.Diagnostics;
using System.Text.Json;
using Em.Api.Core.Models;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log;

/// <summary>Turns one <c>ILogger</c> call into a <see cref="LogEvent"/>: template, structured properties and scope.</summary>
internal static class LogEventFactory
{
   /// <summary>Request trace category; its lines become <see cref="LogEventKind.Access"/> events.</summary>
   public const string RequestCategory = "Em.Api.Request";

   private const int MaxPropertiesLength = 16 * 1024;

   // Scope and property names promoted to envelope fields.
   private static readonly HashSet<string> EnvelopeNames = new(StringComparer.Ordinal) {
      "RequestId", "TraceId", "SpanId", "UserId", "Account", "SessionId", "ActingAs", "ClientAddress"
   };

   public static LogEvent Create<TState>(string category, LogLevel level, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter, IExternalScopeProvider? scopes) {
      var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
      string? template = null;
      if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
         foreach (var (key, value) in pairs) {
            if (key == "{OriginalFormat}") template = value as string;
            else properties[key] = Simple(value);
         }
      }

      // Scopes are visited outermost first; an inner scope (the engine's request scope) overrides an outer one
      // with the same name (ASP.NET Core's own RequestId), and properties of the message itself win over both.
      var scoped = new Dictionary<string, object?>(StringComparer.Ordinal);
      scopes?.ForEachScope((scope, values) => {
         if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs) {
            foreach (var (key, value) in scopePairs) {
               if (key == "{OriginalFormat}") continue;
               values[key] = Simple(value);
            }
         }
      }, scoped);
      foreach (var (key, value) in scoped) properties.TryAdd(key, value);

      var e = new LogEvent {
         Time = DateTime.UtcNow,
         Level = (LogEventLevel)(int)level,
         Category = category,
         Template = template,
         Message = SafeFormat(state, exception, formatter),
         Exception = exception?.ToString(),
         TraceId = Activity.Current?.TraceId.ToString(),
         SpanId = Activity.Current?.SpanId.ToString(),
         Source = "server"
      };

      e.RequestId = Take(properties, "RequestId") ?? e.RequestId;
      e.TraceId = Take(properties, "TraceId") ?? e.TraceId;
      e.SpanId = Take(properties, "SpanId") ?? e.SpanId;
      e.UserId = Take(properties, "UserId");
      e.Account = Take(properties, "Account");
      e.SessionId = Take(properties, "SessionId");
      e.ActingAs = Take(properties, "ActingAs");
      e.ClientAddress = Take(properties, "ClientAddress");

      e.Kind = category == RequestCategory ? LogEventKind.Access
         : exception is not null && level >= LogLevel.Error ? LogEventKind.Error
         : LogEventKind.Diagnostic;

      if (properties.Count > 0) {
         var json = JsonSerializer.Serialize(properties);
         e.Properties = json.Length <= MaxPropertiesLength ? json : null;
      }

      return e;
   }

   /// <summary>Request trace phase of an access event (<c>IN</c>, <c>START</c>, <c>DONE</c>, ...).</summary>
   public static string? PhaseOf<TState>(TState state) {
      if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return null;
      foreach (var (key, value) in pairs) if (key == "Phase") return value?.ToString();
      return null;
   }

   private static string? Take(Dictionary<string, object?> properties, string name) {
      if (!EnvelopeNames.Contains(name) || !properties.TryGetValue(name, out var value)) return null;
      return value?.ToString() is { Length: > 0 } text ? text : null;
   }

   private static object? Simple(object? value) => value switch {
      null => null,
      string or bool or int or long or double or decimal or float or short or byte or uint or ulong or DateTime or DateTimeOffset or Guid => value,
      TimeSpan t => t.ToString(),
      Enum en => en.ToString(),
      _ => value.ToString()
   };

   private static string SafeFormat<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      try {
         var text = formatter(state, exception);
         return text.Length > 4000 ? text[..4000] : text;
      }
      catch (Exception ex) {
         return $"(message could not be formatted: {ex.Message})";
      }
   }
}
