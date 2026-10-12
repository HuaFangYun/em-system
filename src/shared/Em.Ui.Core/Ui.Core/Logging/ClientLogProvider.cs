using System.Text.Json;
using Em.Api.Core.Models;
using Microsoft.Extensions.Logging;

namespace Em.Ui.Core.Logging;

/// <summary>
/// The client <c>ILogger</c> provider (alias <c>EmLog</c>): every entry at or above the minimum level is written
/// right away to the local outbox (<see cref="ClientLogFolder"/>), with the user signed in at that moment. It never
/// throws into the code that logs.
/// </summary>
[ProviderAlias("EmLog")]
public sealed class ClientLogProvider : ILoggerProvider, ISupportExternalScope
{
   private readonly ClientLogFolder _folder;
   private readonly ClientLogOptions _options;
   private readonly Func<ClientLogUser> _user;
   private IExternalScopeProvider? _scopes;

   /// <summary>Creates the provider.</summary>
   /// <param name="folder">Local outbox.</param>
   /// <param name="options">Settings.</param>
   /// <param name="user">Returns who is signed in now.</param>
   public ClientLogProvider(ClientLogFolder folder, ClientLogOptions options, Func<ClientLogUser> user) {
      _folder = folder;
      _options = options;
      _user = user;
   }

   /// <inheritdoc />
   public ILogger CreateLogger(string categoryName) => new ClientLogger(this, categoryName);

   /// <inheritdoc />
   public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

   /// <inheritdoc />
   public void Dispose() { }

   /// <summary>Writes an event built elsewhere (e.g. by an unhandled-exception handler).</summary>
   public void Write(LogEvent e) {
      try {
         var user = _user();
         e.UserId ??= user.UserId;
         e.Account ??= user.Account;
         e.SessionId ??= user.SessionId;
         e.App ??= _options.App;
         e.AppVersion ??= _options.AppVersion;
         e.Instance ??= Environment.MachineName;
         e.Source = "client";
         if (e.Time == default) e.Time = DateTime.UtcNow;
         _folder.Append(e);
      }
      catch {
         // Logging must never break the application.
      }
   }

   internal LogEvent Build<TState>(string category, LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
      string? template = null;
      if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
         foreach (var (key, value) in pairs) {
            if (key == "{OriginalFormat}") template = value as string;
            else properties[key] = value?.ToString();
         }
      }
      _scopes?.ForEachScope((scope, props) => {
         if (scope is IEnumerable<KeyValuePair<string, object?>> sp) foreach (var (k, v) in sp) if (k != "{OriginalFormat}") props.TryAdd(k, v?.ToString());
      }, properties);
      string message;
      try { message = formatter(state, exception); }
      catch (Exception ex) { message = $"(message could not be formatted: {ex.Message})"; }
      var json = properties.Count == 0 ? null : JsonSerializer.Serialize(properties);
      return new LogEvent {
         Time = DateTime.UtcNow,
         Level = (LogEventLevel)(int)level,
         Kind = exception is not null && level >= LogLevel.Error ? LogEventKind.Error : LogEventKind.Diagnostic,
         Category = category,
         Template = template,
         Message = message.Length > 4000 ? message[..4000] : message,
         Properties = json is { Length: <= 16 * 1024 } ? json : null,
         Exception = exception?.ToString()
      };
   }

   private sealed class ClientLogger(ClientLogProvider owner, string category) : ILogger
   {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);

      public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && (int)logLevel >= (int)owner._options.MinLevel;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
         if (!IsEnabled(logLevel)) return;
         try { owner.Write(owner.Build(category, logLevel, state, exception, formatter)); }
         catch { }
      }
   }
}
