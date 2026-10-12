using System.Reflection;
using Em.Shared;

namespace Em.Api.Core.Log;

/// <summary>Identity of the running server and live counters shared by the log components.</summary>
internal sealed class LogRuntime
{
   private long _dropped;
   private volatile string? _storeError;

   public LogRuntime(string? appName, string environment) {
      App = string.IsNullOrWhiteSpace(appName) ? Assembly.GetEntryAssembly()?.GetName().Name ?? "Em.Api" : appName;
      AppVersion = Em.Shared.AppVersion.Current;
      Instance = System.Environment.MachineName;
      Environment = environment;
   }

   public string App { get; }
   public string AppVersion { get; }
   public string Instance { get; }
   public string Environment { get; }

   /// <summary>Events dropped because the queue was full, since start.</summary>
   public long Dropped => Interlocked.Read(ref _dropped);

   public void AddDropped(long count) => Interlocked.Add(ref _dropped, count);

   /// <summary>Disk segments waiting to be written to the store.</summary>
   public int PendingSegments { get; set; }

   /// <summary>True when the last store write succeeded.</summary>
   public bool StoreHealthy => _storeError is null;

   /// <summary>Last store error.</summary>
   public string? StoreError => _storeError;

   public void ReportStore(string? error) => _storeError = error;
}
