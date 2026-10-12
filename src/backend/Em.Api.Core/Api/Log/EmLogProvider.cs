using Em.Api.Core.Models;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log;

/// <summary>
/// The log engine's <c>ILogger</c> provider (alias <c>EmLog</c>, configured under <c>Logging:EmLog</c>). Turns
/// every entry into a <see cref="LogEvent"/> and queues it; it never blocks the caller and never throws.
/// </summary>
[ProviderAlias("EmLog")]
internal sealed class EmLogProvider : ILoggerProvider, ISupportExternalScope
{
   private readonly LogQueue _queue;
   private readonly LogRuntime _runtime;
   private readonly bool _storeStartLines;
   private IExternalScopeProvider? _scopes;

   public EmLogProvider(LogQueue queue, LogRuntime runtime, bool storeStartLines) {
      _queue = queue;
      _runtime = runtime;
      _storeStartLines = storeStartLines;
      // Lines written while the host was being configured (bootstrap logger) are handed over once.
      foreach (var e in BootstrapLogBuffer.TakeAll()) Accept(e);
   }

   public ILogger CreateLogger(string categoryName) => new EmLogger(this, categoryName);

   public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

   public void Dispose() { }

   internal void Accept(LogEvent e) {
      e.App ??= _runtime.App;
      e.AppVersion ??= _runtime.AppVersion;
      e.Instance ??= _runtime.Instance;
      e.Environment ??= _runtime.Environment;
      if (e.Kind == LogEventKind.Error && e.Exception is { } text) e.Fingerprint ??= ErrorFingerprint.Compute(text);
      _queue.Enqueue(e);
   }

   private sealed class EmLogger(EmLogProvider owner, string category) : ILogger
   {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
         owner._scopes?.Push(state);

      public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && !LogGuard.IsSuppressed;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
         Func<TState, Exception?, string> formatter) {
         if (!IsEnabled(logLevel)) return;
         try {
            if (category == LogEventFactory.RequestCategory && !owner._storeStartLines &&
                LogEventFactory.PhaseOf(state) is "IN" or "START") return;
            owner.Accept(LogEventFactory.Create(category, logLevel, state, exception, formatter, owner._scopes));
         }
         catch {
            // A logger must never break the code that writes the log line.
         }
      }
   }
}

/// <summary>
/// Holds what the bootstrap logger of <c>EmAppBuilder</c> writes before the host exists (at most 1000
/// entries), so configuration errors at startup also reach the store.
/// </summary>
internal static class BootstrapLogBuffer
{
   private const int Capacity = 1000;
   private static readonly List<LogEvent> Buffer = [];
   private static bool _closed;

   /// <summary>Provider to add to the bootstrap logger factory.</summary>
   public static ILoggerProvider Provider { get; } = new BufferProvider();

   public static List<LogEvent> TakeAll() {
      lock (Buffer) {
         _closed = true;
         var copy = Buffer.ToList();
         Buffer.Clear();
         return copy;
      }
   }

   private sealed class BufferProvider : ILoggerProvider
   {
      public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName);
      public void Dispose() { }
   }

   private sealed class BufferLogger(string category) : ILogger
   {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
         Func<TState, Exception?, string> formatter) {
         if (!IsEnabled(logLevel)) return;
         lock (Buffer) {
            if (_closed || Buffer.Count >= Capacity) return;
            try { Buffer.Add(LogEventFactory.Create(category, logLevel, state, exception, formatter, null)); }
            catch { }
         }
      }
   }
}
